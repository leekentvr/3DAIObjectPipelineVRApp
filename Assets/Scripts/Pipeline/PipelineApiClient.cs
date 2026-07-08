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
    /// configured (PipelineConfig.apiToken) and omit it otherwise, so this keeps working
    /// either way. Flag this mismatch to whoever owns the server repo rather than trusting
    /// either doc blindly.
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

        public PipelineApiClient(PipelineConfig config = null)
        {
            _config = config ?? PipelineConfig.Instance;
        }

        private string BaseUrl => _config.baseUrl.TrimEnd('/');
        private string ApiToken => _config.apiToken;

        /// <summary>Resolves a possibly-relative URL (e.g. a mesh_url/mask_url from the
        /// objects endpoint, which comes back as "/project/&lt;id&gt;/meshes/...") against
        /// the configured base URL.</summary>
        public string ResolveUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return url;
            if (url.StartsWith("http://") || url.StartsWith("https://")) return url;
            return BaseUrl + (url.StartsWith("/") ? url : "/" + url);
        }

        private void ApplyAuthHeader(UnityWebRequest req)
        {
            if (!string.IsNullOrEmpty(ApiToken))
            {
                req.SetRequestHeader("X-API-Token", ApiToken);
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
            ThrowIfError(req);

            var resp = JsonConvert.DeserializeObject<JobHandle>(req.downloadHandler.text);
            return resp.JobId;
        }

        /// <summary>GET /api/jobs/{job_id}.</summary>
        public async Task<JobStatusResponse> GetJobAsync(string jobId, CancellationToken ct = default)
        {
            using var req = UnityWebRequest.Get($"{BaseUrl}/api/jobs/{UnityWebRequest.EscapeURL(jobId)}");
            ApplyAuthHeader(req);
            using (ct.Register(() => req.Abort()))
            {
                await req.SendWebRequest();
            }
            ThrowIfError(req);

            return JsonConvert.DeserializeObject<JobStatusResponse>(req.downloadHandler.text);
        }

        /// <summary>GET /api/projects/{project}/objects.</summary>
        public async Task<ObjectsResponse> GetObjectsAsync(string project, CancellationToken ct = default)
        {
            using var req = UnityWebRequest.Get($"{BaseUrl}/api/projects/{UnityWebRequest.EscapeURL(project)}/objects");
            ApplyAuthHeader(req);
            using (ct.Register(() => req.Abort()))
            {
                await req.SendWebRequest();
            }
            ThrowIfError(req);

            return JsonConvert.DeserializeObject<ObjectsResponse>(req.downloadHandler.text);
        }

        /// <summary>Downloads raw bytes from an (possibly relative) URL, e.g. a mesh_url.</summary>
        public async Task<byte[]> DownloadBytesAsync(string url, CancellationToken ct = default)
        {
            using var req = UnityWebRequest.Get(ResolveUrl(url));
            ApplyAuthHeader(req);
            using (ct.Register(() => req.Abort()))
            {
                await req.SendWebRequest();
            }
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
