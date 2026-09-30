using System;
using Pipeline;
using TMPro;
using UnityEngine;

namespace UI
{
    /// <summary>
    /// In-VR panel for setting the pipeline server's LAN address without an adb
    /// pull/edit/push round-trip -- useful since that address changes regularly
    /// (different networks, server restarts with a new DHCP lease, etc.).
    ///
    /// UI surface is a single host field (e.g. "192.168.1.45") -- typing in VR works
    /// fine now that OVR Manager's "Requires System Keyboard" is enabled (see
    /// docs/unity-setup.md step 2), so there's no need to split the address into
    /// separate prefix/suffix fields anymore. The port is still NOT a field -- it's
    /// fixed at DefaultPort (5000, per docs/pipeline-api.md) since this is a single,
    /// known private server. If you ever genuinely need a different port, change
    /// DefaultPort below and rebuild.
    /// This combines into "http://{host}:{port}" on Save. The scheme is always
    /// http:// (the pipeline server doesn't serve https).
    ///
    /// Saving here takes effect immediately for the rest of the running app: unlike a
    /// naive config object, PipelineApiClient reads PipelineConfig.Instance's fields
    /// live rather than a copy taken at construction, so any PipelineApiClient already
    /// created elsewhere (e.g. PointSelectController.Awake) picks up the change with no
    /// restart and no re-wiring.
    ///
    /// SECURITY, for this "one server + 1-2 known headsets, same private LAN" setup:
    /// the live pipeline server currently has NO authentication at all (see
    /// docs/pipeline-api.md "Security") -- anything on the network can call it. This
    /// panel still applies _staticApiTokens (an Inspector-only LIST of candidate tokens,
    /// never typed in VR): PipelineApiClient tries them against the server and sends
    /// whichever one the server accepts as an X-API-Token header on every request. That
    /// header being sent accomplishes NOTHING by itself -- it's inert until the
    /// pipeline-controller server (a separate repo; out of scope here per CLAUDE.md's
    /// "Non-goals") is changed to actually check it. A minimal server-side check that
    /// accepts a whitelist of tokens (Flask, sketch -- adapt to that repo's structure):
    ///
    ///   # comma-separated whitelist; add a machine's token here to let it in
    ///   ALLOWED = set(os.environ["PIPELINE_API_TOKENS"].split(","))  # don't commit it
    ///
    ///   @app.before_request
    ///   def check_token():
    ///       if request.path.startswith("/api/") and \
    ///          request.headers.get("X-API-Token") not in ALLOWED:
    ///           abort(401)
    ///
    /// That's proportionate for this threat model (trusted private LAN, not internet-
    /// facing) -- it stops other devices on the same network from casually hitting the
    /// API, not a determined attacker who can sniff LAN traffic (the token still
    /// travels in plaintext over plain HTTP). Put every headset's token in this
    /// component's _staticApiTokens list in the Editor before building, and whitelist
    /// each machine's token on the server as you roll them out.
    ///
    /// This script only handles the logic (load current values, validate, save, test
    /// connectivity) -- it does not build the UI for you. Getting a canvas that's
    /// actually clickable in VR needs a `GraphicRaycaster` (on the Canvas) +
    /// `PointableCanvas`/`RayInteractable` (Interaction SDK) + `PointableCanvasModule`
    /// (on the EventSystem); see docs/unity-setup.md for the exact wiring.
    /// </summary>
    public class PipelineConfigPanel : MonoBehaviour
    {
        [Header("Server host, e.g. \"192.168.1.45\" (no scheme, no port)")]
        [SerializeField] private TMP_InputField _hostInput;

        [Header("Feedback")]
        [SerializeField] private TMP_Text _statusText;

        [Header("API tokens -- static list, NOT typed in VR (see class doc re: security)")]
        [Tooltip("Candidate tokens, e.g. one per machine/headset. PipelineApiClient tries " +
                 "them against the server and sticks with the first the server accepts. " +
                 "Set here in the Editor to match the server's expected token(s), if " +
                 "you've added token-checking there. Applied automatically on every Save; " +
                 "the list on disk is left as-is if this is empty. Does nothing until the " +
                 "server actually validates the X-API-Token header.")]
        [SerializeField] private string[] _staticApiTokens = new string[0];

        private const string DefaultHost = "192.168.1.100";
        private const string DefaultPort = "5000";

        private PipelineApiClient _client;

        private void Awake()
        {
            _client = new PipelineApiClient();
        }

        private void OnEnable()
        {
            RefreshFieldsFromConfig();
        }

        private void RefreshFieldsFromConfig()
        {
            PipelineConfig config = PipelineConfig.Instance;

            SetText(_hostInput, TryExtractHost(config.baseUrl, out string host) ? host : DefaultHost);

            SetStatus($"Current: {config.baseUrl}");
        }

        /// <summary>Wire to the panel's Save button OnClick.</summary>
        public void OnSavePressed()
        {
            string host = _hostInput != null ? _hostInput.text.Trim() : string.Empty;

            if (string.IsNullOrEmpty(host))
            {
                SetStatus("Enter the server's host/IP (e.g. 192.168.1.45).", isError: true);
                return;
            }

            string url = $"http://{host}:{DefaultPort}";
            if (!Uri.TryCreate(url, UriKind.Absolute, out _))
            {
                SetStatus($"'{url}' isn't a valid URL -- check the host.", isError: true);
                return;
            }

            PipelineConfig config = PipelineConfig.Instance;
            config.baseUrl = url;
            // Trim each token -- a value pasted into the Inspector with a stray leading/
            // trailing space otherwise gets persisted verbatim and 401s against the server.
            // De-dupe and drop blanks so the candidate list stays clean.
            var tokens = new System.Collections.Generic.List<string>();
            if (_staticApiTokens != null)
            {
                foreach (string raw in _staticApiTokens)
                {
                    string t = raw?.Trim();
                    if (!string.IsNullOrEmpty(t) && !tokens.Contains(t)) tokens.Add(t);
                }
            }
            if (tokens.Count > 0)
            {
                config.apiTokens = tokens.ToArray();
                // New/changed candidate list -> forget any previously stuck token so the
                // client re-checks against this list on the next request.
                config.apiToken = "";
            }
            config.SaveToDisk();

            SetStatus($"Saved: {config.baseUrl}");
        }

        /// <summary>Wire to the panel's "Test Connection" button OnClick. Saves first
        /// (so you're testing what you just typed, not the old value), then checks
        /// reachability.</summary>
        public async void OnTestConnectionPressed()
        {
            OnSavePressed();
            SetStatus("Testing connection...");

            (bool reachable, string detail) = await _client.TestConnectionAsync();
            SetStatus(detail, isError: !reachable);
        }

        /// <summary>Extracts just the host ("192.168.1.45") from a saved
        /// "http://192.168.1.45:5000"-style URL. Returns false for anything that
        /// doesn't parse as an absolute URL, so the caller can fall back to a default
        /// instead of showing something wrong.</summary>
        private static bool TryExtractHost(string url, out string host)
        {
            host = null;

            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri uri))
            {
                return false;
            }

            host = uri.Host;
            return true;
        }

        private static void SetText(TMP_InputField field, string value)
        {
            if (field != null) field.text = value;
        }

        private void SetStatus(string message, bool isError = false)
        {
            Debug.Log($"[PipelineConfigPanel] {message}");
            if (_statusText != null)
            {
                _statusText.text = message;
                _statusText.color = isError ? Color.red : Color.white;
            }
        }
    }
}
