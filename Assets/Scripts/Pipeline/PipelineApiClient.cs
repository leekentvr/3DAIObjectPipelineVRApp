using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.Networking;

namespace Pipeline
{
    /// <summary>Thrown for any non-2xx response, carrying enough detail to show the user
    /// something useful and to distinguish "pipeline busy, retry" from a hard failure.</summary>
    public class PipelineApiException : Exception
    {
        public long StatusCode { get; }
        public string Body { get; }

        public PipelineApiException(long statusCode, string body, string message)
            : base(message)
        {
            StatusCode = statusCode;
            Body = body;
        }
    }

    /// <summary>
    /// HTTP client for the pipeline REST API described in docs/pipeline-api.md. Talks to
    /// a Python pipeline server elsewhere on the LAN -- this repo has no AI/pipeline code
    /// of its own (see CLAUDE.md "Non-goals").
    ///
    /// All calls are async and return as soon as the server responds; the slow work
    /// (SAM3/SAM3D) happens server-side in a background job you poll for separately via
    /// PollJobUntilDoneAsync. Don't block a frame waiting on these -- per CLAUDE.md,
    /// design the UX assuming reconstruction takes anywhere from tens of seconds to
    /// several minutes.
    ///
    /// The live server currently has NO authentication (see "Security" in
    /// docs/pipeline-api.md). CLAUDE.md's Auth section describes an X-API-Token header
    /// requirement that doesn't match that -- we send the header when a token is
    /// configured and omit it otherwise, so this keeps working either way. Flag this
    /// mismatch to whoever owns the server repo rather than trusting either doc blindly.
    ///
    /// Token selection: PipelineConfig.apiTokens holds a LIST of candidate tokens (one per
    /// machine/headset, so machines can be whitelisted server-side one at a time). Before
    /// the first request, EnsureTokenResolvedAsync tries each candidate against the server
    /// and "sticks" with the first the server accepts (i.e. doesn't 401), caching it in
    /// PipelineConfig.apiToken so it persists across restarts. If none are accepted yet
    /// (this machine not whitelisted), it keeps retrying the list on later requests; if a
    /// stuck token later starts 401ing, it's dropped and the list is re-checked.
    /// </summary>
    public class PipelineApiClient
    {
        // Holds a reference to the PipelineConfig object itself, NOT a copy of its
        // baseUrl/apiToken taken at construction time. The server address regularly
        // changes (LAN IP changes between sessions), and it can now be edited from
        // inside the running app via PipelineConfigPanel -- reading _config.baseUrl /
        // _config.apiToken fresh on every request means any PipelineApiClient already
        // constructed elsewhere (e.g. in PointSelectController.Awake) automatically
        // picks up a change saved through the panel, with no restart or re-wiring.
        private readonly PipelineConfig _config;

        // The token currently in use, chosen from _config.apiTokens by trying each against
        // the server and keeping the first one it accepts (see EnsureTokenResolvedAsync).
        // Null means "send no token".
        private string _activeToken;

        // False until we've either locked in a working token or confirmed there are no
        // candidates to try. Stays false while candidates exist but none have been accepted
        // yet, so requests keep re-checking the list -- this is what lets a headset start
        // working the moment its token is whitelisted server-side, with no app restart.
        private bool _tokenResolved;

        // Serializes token discovery so concurrent requests don't all probe at once.
        private readonly SemaphoreSlim _tokenLock = new SemaphoreSlim(1, 1);

        public PipelineApiClient(PipelineConfig config = null)
        {
            _config = config ?? PipelineConfig.Instance;
        }

        private string BaseUrl => _config.baseUrl.Trim().TrimEnd('/');
        // The token actually sent, resolved from the candidate list by
        // EnsureTokenResolvedAsync. Whitespace is already trimmed at resolution time --
        // tokens routinely pick up stray whitespace when pasted into the Inspector or
        // edited into pipeline-config.json by hand, and a token with a trailing space
        // fails server-side comparison with a 401 that's miserable to diagnose (the
        // header LOOKS right in every log).
        private string ApiToken => _activeToken;

        /// <summary>Resolves a possibly-relative URL (e.g. a mesh_url/mask_url from the
        /// objects endpoint, which comes back as "/project/&lt;id&gt;/meshes/...") against
        /// the configured base URL.</summary>
        public string ResolveUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return url;
            if (url.StartsWith("http://") || url.StartsWith("https://")) return url;
            return BaseUrl + (url.StartsWith("/") ? url : "/" + url);
        }

        /// <summary>Appends a unique query param so caches (UnityWebRequest's, any proxy,
        /// or the browser-style conditional GET the Flask static handler enables via
        /// ETag/Last-Modified) can't serve a stale response. Defensive: projects are unique
        /// per capture now, but this also keeps repeated GETs (e.g. object manifests) fresh
        /// and costs nothing.</summary>
        private static string WithCacheBust(string url)
        {
            if (string.IsNullOrEmpty(url)) return url;
            string sep = url.Contains("?") ? "&" : "?";
            return $"{url}{sep}_cb={DateTime.UtcNow.Ticks}";
        }

        /// <summary>Belt-and-braces no-cache headers to go with WithCacheBust.</summary>
        private static void ApplyNoCache(UnityWebRequest req)
        {
            req.SetRequestHeader("Cache-Control", "no-cache, no-store, must-revalidate");
            req.SetRequestHeader("Pragma", "no-cache");
        }

        private void ApplyAuthHeader(UnityWebRequest req)
        {
            if (!string.IsNullOrEmpty(ApiToken))
            {
                req.SetRequestHeader("X-API-Token", ApiToken);
            }
        }

        /// <summary>
        /// Ensures _activeToken is set to a token the server accepts, chosen from
        /// _config.apiTokens. Called at the start of every request; cheap once resolved.
        ///
        /// Rules:
        ///  * If a token is already stuck (from a successful request this run, or persisted
        ///    in _config.apiToken from a previous run), keep using it -- no probing.
        ///  * If there are no candidate tokens, run with no auth header and stop.
        ///  * Otherwise try each candidate against the lightweight connection-test endpoint
        ///    and lock in the first the server does NOT answer with 401. Persist it so it
        ///    survives restarts.
        ///  * If every candidate is rejected (e.g. this headset isn't whitelisted on the
        ///    server yet), stay unresolved so the next request tries the list again -- the
        ///    moment the server whitelists one of these tokens it starts working, no restart.
        /// </summary>
        public async Task EnsureTokenResolvedAsync(CancellationToken ct = default)
        {
            if (_tokenResolved) return;

            await _tokenLock.WaitAsync(ct);
            try
            {
                if (_tokenResolved) return;

                // A previously stuck token (this run, or persisted to disk) wins outright.
                string stuck = _config.apiToken?.Trim();
                if (!string.IsNullOrEmpty(stuck))
                {
                    _activeToken = stuck;
                    _tokenResolved = true;
                    return;
                }

                // Build the candidate list: trimmed, de-duped, no blanks.
                var candidates = new List<string>();
                if (_config.apiTokens != null)
                {
                    foreach (string raw in _config.apiTokens)
                    {
                        string t = raw?.Trim();
                        if (!string.IsNullOrEmpty(t) && !candidates.Contains(t))
                        {
                            candidates.Add(t);
                        }
                    }
                }

                if (candidates.Count == 0)
                {
                    // No auth configured at all -- run with no header and stop probing.
                    _activeToken = null;
                    _tokenResolved = true;
                    return;
                }

                foreach (string candidate in candidates)
                {
                    ct.ThrowIfCancellationRequested();
                    (bool accepted, bool reachable) = await ProbeTokenAsync(candidate, ct);

                    if (!reachable)
                    {
                        // Server is unreachable right now -- can't tell good tokens from bad.
                        // Don't stick anything; the real request that follows surfaces the
                        // connection error, and we retry the list next time.
                        return;
                    }

                    if (accepted)
                    {
                        _activeToken = candidate;
                        _config.apiToken = candidate;   // stick it, persist across restarts
                        _config.SaveToDisk();
                        _tokenResolved = true;
                        Debug.Log("[Pipeline] Locked in a working API token from the candidate list.");
                        return;
                    }
                }

                // Every candidate was rejected (401). Most likely this machine isn't
                // whitelisted on the server yet. Stay unresolved (send nothing) so the next
                // request re-checks the list -- once the server whitelists one of these
                // tokens it'll start working with no app restart.
                _activeToken = null;
                Debug.LogWarning(
                    "[Pipeline] None of the configured API tokens were accepted by the server yet " +
                    "(is this machine whitelisted?). Will keep retrying the list.");
            }
            finally
            {
                _tokenLock.Release();
            }
        }

        /// <summary>Clears the stuck token so the candidate list is re-checked on the next
        /// request. Called automatically when the server 401s a request that used a
        /// previously-working token (rotated/revoked, or a different candidate is now the
        /// right one).</summary>
        public void InvalidateToken()
        {
            _activeToken = null;
            _tokenResolved = false;
            if (!string.IsNullOrEmpty(_config.apiToken))
            {
                _config.apiToken = "";
                _config.SaveToDisk();
            }
        }

        /// <summary>Hits the connection-test endpoint with a specific token to see whether
        /// the server accepts it. accepted == server answered with anything other than 401;
        /// reachable == we actually got a response (vs. a connection-level failure).</summary>
        private async Task<(bool accepted, bool reachable)> ProbeTokenAsync(string token, CancellationToken ct)
        {
            using var req = UnityWebRequest.Get($"{BaseUrl}/api/jobs/__pipeline_token_probe__");
            if (!string.IsNullOrEmpty(token))
            {
                req.SetRequestHeader("X-API-Token", token);
            }
            using (ct.Register(() => req.Abort()))
            {
                await req.SendWebRequest();
            }

            bool reachable = req.result != UnityWebRequest.Result.ConnectionError;
            bool accepted = reachable && req.responseCode != 401;
            return (accepted, reachable);
        }

        /// <summary>If the server rejected the token we sent, drop it so the candidate list
        /// gets re-checked on the next call. Safe to call after any request.</summary>
        private void HandleAuthResult(UnityWebRequest req)
        {
            if (req.responseCode == 401)
            {
                InvalidateToken();
            }
        }

        private static void ThrowIfError(UnityWebRequest req)
        {
            if (req.result == UnityWebRequest.Result.Success) return;

            string body = req.downloadHandler != null ? req.downloadHandler.text : null;
            long code = req.responseCode;

            // Try to surface the server's JSON error message if there is one.
            string message = $"{req.method} {req.url} failed ({code}): {req.error}";
            if (!string.IsNullOrEmpty(body))
            {
                message += $" -- {body}";
            }

            throw new PipelineApiException(code, body, message);
        }

        /// <summary>POST /api/projects -- create a project (or attach a new image to an
        /// existing one). imageBytes is required unless the project already has an
        /// input.png server-side. depthBytes is optional and only meaningful alongside
        /// real intrinsics later passed to select/sam3d.</summary>
        public async Task<string> CreateProjectAsync(
            byte[] imageBytes,
            byte[] depthBytes = null,
            string projectName = null,
            CancellationToken ct = default)
        {
            await EnsureTokenResolvedAsync(ct);

            var form = new List<IMultipartFormSection>();
            if (imageBytes != null)
            {
                form.Add(new MultipartFormFileSection("image", imageBytes, "input.png", "image/png"));
            }
            if (depthBytes != null)
            {
                form.Add(new MultipartFormFileSection("depth", depthBytes, "depth.png", "image/png"));
            }
            if (!string.IsNullOrEmpty(projectName))
            {
                form.Add(new MultipartFormDataSection("project", projectName));
            }

            using var req = UnityWebRequest.Post($"{BaseUrl}/api/projects", form);
            ApplyAuthHeader(req);
            using (ct.Register(() => req.Abort()))
            {
                await req.SendWebRequest();
            }
            HandleAuthResult(req);
            ThrowIfError(req);

            var resp = JsonConvert.DeserializeObject<CreateProjectResponse>(req.downloadHandler.text);
            return resp.ProjectId;
        }

        /// <summary>POST /api/projects/{project}/select -- the VR point-select flow.
        /// intrinsics must be either fully populated or null (all-or-nothing, matching
        /// the server's validation).</summary>
        public async Task<string> SelectAsync(
            string project,
            float pointX,
            float pointY,
            float? boxHalfSize = null,
            CameraIntrinsics? intrinsics = null,
            CancellationToken ct = default)
        {
            await EnsureTokenResolvedAsync(ct);

            var payload = SelectRequest.Create(pointX, pointY, boxHalfSize, intrinsics);
            string json = JsonConvert.SerializeObject(payload);
            byte[] bodyRaw = Encoding.UTF8.GetBytes(json);

            using var req = new UnityWebRequest($"{BaseUrl}/api/projects/{UnityWebRequest.EscapeURL(project)}/select", "POST");
            req.uploadHandler = new UploadHandlerRaw(bodyRaw);
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");
            ApplyAuthHeader(req);

            using (ct.Register(() => req.Abort()))
            {
                await req.SendWebRequest();
            }
            HandleAuthResult(req);
            ThrowIfError(req);

            var resp = JsonConvert.DeserializeObject<JobHandle>(req.downloadHandler.text);
            return resp.JobId;
        }

        /// <summary>GET /api/rooms/{room}/objects -- every object across all captures in a
        /// room (prefix grouping on the server). Each entry carries its own Project (for
        /// LoD requests) and Placement (to restore where it was put).</summary>
        public async Task<RoomObjectsResponse> GetRoomObjectsAsync(string room, CancellationToken ct = default)
        {
            await EnsureTokenResolvedAsync(ct);

            using var req = UnityWebRequest.Get(
                WithCacheBust($"{BaseUrl}/api/rooms/{UnityWebRequest.EscapeURL(room)}/objects"));
            ApplyAuthHeader(req);
            ApplyNoCache(req);
            using (ct.Register(() => req.Abort()))
            {
                await req.SendWebRequest();
            }
            HandleAuthResult(req);
            ThrowIfError(req);

            return JsonConvert.DeserializeObject<RoomObjectsResponse>(req.downloadHandler.text);
        }

        /// <summary>POST /api/projects/{project}/objects/{index}/placement -- persist where an
        /// object is placed in the real room (Unity world pos/rot/scale) so a room reload can
        /// restore it. Fire-and-forget friendly: throws on transport/HTTP error only.</summary>
        public async Task SavePlacementAsync(
            string project, int index, Vector3 position, Quaternion rotation, Vector3 scale,
            string frame = null, string roomUuid = null, CancellationToken ct = default)
        {
            await EnsureTokenResolvedAsync(ct);

            var payload = new Placement
            {
                Position = new[] { position.x, position.y, position.z },
                Rotation = new[] { rotation.x, rotation.y, rotation.z, rotation.w },
                Scale = new[] { scale.x, scale.y, scale.z },
                Frame = frame,
                RoomUuid = roomUuid,
            };
            byte[] bodyRaw = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(payload));

            using var req = new UnityWebRequest(
                $"{BaseUrl}/api/projects/{UnityWebRequest.EscapeURL(project)}/objects/{index}/placement", "POST");
            req.uploadHandler = new UploadHandlerRaw(bodyRaw);
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");
            ApplyAuthHeader(req);

            using (ct.Register(() => req.Abort()))
            {
                await req.SendWebRequest();
            }
            HandleAuthResult(req);
            ThrowIfError(req);
        }

        /// <summary>GET /api/jobs/{job_id}.</summary>
        public async Task<JobStatusResponse> GetJobAsync(string jobId, CancellationToken ct = default)
        {
            await EnsureTokenResolvedAsync(ct);

            using var req = UnityWebRequest.Get($"{BaseUrl}/api/jobs/{UnityWebRequest.EscapeURL(jobId)}");
            ApplyAuthHeader(req);
            using (ct.Register(() => req.Abort()))
            {
                await req.SendWebRequest();
            }
            HandleAuthResult(req);
            ThrowIfError(req);

            return JsonConvert.DeserializeObject<JobStatusResponse>(req.downloadHandler.text);
        }

        /// <summary>GET /api/projects/{project}/objects.</summary>
        public async Task<ObjectsResponse> GetObjectsAsync(string project, CancellationToken ct = default)
        {
            await EnsureTokenResolvedAsync(ct);

            using var req = UnityWebRequest.Get(
                WithCacheBust($"{BaseUrl}/api/projects/{UnityWebRequest.EscapeURL(project)}/objects"));
            ApplyAuthHeader(req);
            ApplyNoCache(req);
            using (ct.Register(() => req.Abort()))
            {
                await req.SendWebRequest();
            }
            HandleAuthResult(req);
            ThrowIfError(req);

            return JsonConvert.DeserializeObject<ObjectsResponse>(req.downloadHandler.text);
        }

        /// <summary>Downloads object <paramref name="index"/>'s mesh decimated to
        /// <paramref name="ratio"/> (0-1) of its full-resolution face count, via the
        /// server's on-demand LoD endpoint. The server derives every level from the one
        /// stored full-res reconstruction, so this never re-runs the pipeline. ratio is
        /// clamped to (0, 1]; 1 returns the full-res mesh.</summary>
        public async Task<byte[]> DownloadObjectLodAsync(
            string project, int index, float ratio, CancellationToken ct = default)
        {
            ratio = Mathf.Clamp(ratio, 0.001f, 1f);
            string r = ratio.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
            string url = $"{BaseUrl}/api/projects/{UnityWebRequest.EscapeURL(project)}/objects/{index}/lod?ratio={r}";
            return await DownloadBytesAsync(url, ct);
        }

        /// <summary>Downloads raw bytes from an (possibly relative) URL, e.g. a mesh_url.</summary>
        public async Task<byte[]> DownloadBytesAsync(string url, CancellationToken ct = default)
        {
            await EnsureTokenResolvedAsync(ct);

            using var req = UnityWebRequest.Get(WithCacheBust(ResolveUrl(url)));
            ApplyAuthHeader(req);
            ApplyNoCache(req);
            using (ct.Register(() => req.Abort()))
            {
                await req.SendWebRequest();
            }
            HandleAuthResult(req);
            ThrowIfError(req);
            return req.downloadHandler.data;
        }

        /// <summary>
        /// Checks whether the configured server address is reachable at all, without
        /// needing a real project/job to exist. Hits a bogus job id on GET /api/jobs/ --
        /// a plain 404 JSON response still proves the server answered, which is all this
        /// is checking. Only a connection-level failure (host unreachable, refused,
        /// timed out -- i.e. the IP/port is just wrong) counts as "not reachable".
        /// Meant for a "Test Connection" button in something like PipelineConfigPanel,
        /// so you can confirm a newly-typed LAN address is right before trying a real
        /// capture.
        /// </summary>
        public async Task<(bool reachable, string detail)> TestConnectionAsync(CancellationToken ct = default)
        {
            await EnsureTokenResolvedAsync(ct);

            using var req = UnityWebRequest.Get($"{BaseUrl}/api/jobs/__pipeline_connection_test__");
            ApplyAuthHeader(req);
            using (ct.Register(() => req.Abort()))
            {
                await req.SendWebRequest();
            }

            bool reachable = req.result != UnityWebRequest.Result.ConnectionError;
            string detail = reachable
                ? $"Server responded ({req.responseCode})."
                : $"No response from {BaseUrl}: {req.error}";
            return (reachable, detail);
        }

        /// <summary>
        /// Polls GET /api/jobs/{jobId} until status is "done" or "error". Per the API
        /// doc, only one pipeline stage runs globally at a time, and stages can take
        /// minutes -- this deliberately does not time out on its own. Pass a
        /// CancellationToken (e.g. tied to a "processing..." UI Cancel button) if you
        /// want the caller to be able to give up.
        /// </summary>
        public async Task<JobStatusResponse> PollJobUntilDoneAsync(
            string jobId,
            Action<JobStatusResponse> onUpdate = null,
            float pollIntervalSeconds = 2f,
            CancellationToken ct = default)
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();

                JobStatusResponse status = await GetJobAsync(jobId, ct);
                onUpdate?.Invoke(status);

                if (status.IsFinished)
                {
                    return status;
                }

                await Task.Delay(TimeSpan.FromSeconds(pollIntervalSeconds), ct);
            }
        }
    }
}
