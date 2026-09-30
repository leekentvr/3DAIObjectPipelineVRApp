using System;
using System.IO;
using UnityEngine;

namespace Pipeline
{
    /// <summary>
    /// Loads pipeline server connection details (base URL + optional API token) from a
    /// JSON file OUTSIDE the Unity project/build, at Application.persistentDataPath.
    ///
    /// Why persistentDataPath and not a committed asset: per CLAUDE.md, the server's
    /// LAN address and auth token are per-deployment values ("get the current value
    /// from whoever's running the server") and must never be hardcoded/committed. A
    /// file at persistentDataPath lives outside the repo entirely, so there is nothing
    /// to gitignore or accidentally commit. On device it can be edited with `adb shell`
    /// or overwritten via `adb push`; in the Editor it's a normal path under your OS
    /// user profile (see the Debug.Log line on first run for the exact path).
    ///
    /// If the file doesn't exist yet, a template is written and the app logs an error
    /// with the path so you can go fill it in.
    ///
    /// One-time bootstrap on a fresh install (before PipelineConfigPanel exists in your
    /// build, or if you just want to do it from a PC):
    ///   adb logcat -s Unity                     # find the exact path from the "Wrote a
    ///                                            # template to: ..." log line
    ///   adb pull "&lt;path&gt;" .
    ///   # edit pipeline-config.json locally, then:
    ///   adb push pipeline-config.json "&lt;path&gt;"
    /// After that, prefer editing it from inside the running app via
    /// Assets/Scripts/UI/PipelineConfigPanel.cs -- see docs/unity-setup.md.
    /// </summary>
    [Serializable]
    public class PipelineConfig
    {
        public string baseUrl = "http://192.168.1.100:5000";

        // Candidate API tokens to try, in order. Per docs/pipeline-api.md the pipeline
        // server currently has NO authentication, but as machines get whitelisted on the
        // server one at a time, each headset can carry a list of possible tokens here.
        // PipelineApiClient tries them against the server and the first one the server
        // ACCEPTS (any response that isn't 401) gets "stuck" -- remembered in apiToken
        // below and reused for every subsequent request. Leave empty while the server has
        // no auth. Populate it in the Inspector via PipelineConfigPanel, or by hand in
        // pipeline-config.json:  "apiTokens": ["tokenForHeadsetA", "tokenForHeadsetB"]
        public string[] apiTokens = new string[0];

        // The token that was found to work and is now in use. Auto-filled by
        // PipelineApiClient the first time it finds an accepted token from apiTokens (and
        // honored if you set it by hand). This is the "stick with the one that works"
        // cache; it persists across restarts so there's no re-probing every launch. It's
        // cleared automatically if the server later rejects it with a 401, so the
        // apiTokens list gets re-checked (covers a rotated token, or this machine getting
        // whitelisted under a different one of the candidates).
        public string apiToken = "";

        private const string FileName = "pipeline-config.json";

        private static PipelineConfig _instance;

        public static PipelineConfig Instance
        {
            get
            {
                if (_instance == null)
                {
                    _instance = Load();
                }
                return _instance;
            }
        }

        private static string FilePath => Path.Combine(Application.persistentDataPath, FileName);

        private static PipelineConfig Load()
        {
            string path = FilePath;
            try
            {
                if (!File.Exists(path))
                {
                    var template = new PipelineConfig();
                    File.WriteAllText(path, JsonUtility.ToJson(template, true));
                    Debug.LogError(
                        $"[Pipeline] No config found. Wrote a template to:\n{path}\n" +
                        "Edit it with the real server LAN address (and API token, if the " +
                        "server ever requires one) before using the pipeline.");
                    return template;
                }

                string json = File.ReadAllText(path);
                var config = JsonUtility.FromJson<PipelineConfig>(json);
                if (config == null)
                {
                    Debug.LogError($"[Pipeline] Config at {path} is empty/invalid. Using defaults.");
                    return new PipelineConfig();
                }

                if (string.IsNullOrWhiteSpace(config.baseUrl))
                {
                    Debug.LogError($"[Pipeline] Config at {path} has no baseUrl set.");
                }

                // Normalize whitespace from hand-edited/adb-pushed json -- a trailing
                // space in apiToken means every request 401s against a token-checking
                // server, and it's invisible in logs.
                config.baseUrl = config.baseUrl?.Trim();
                config.apiToken = config.apiToken?.Trim();
                if (config.apiTokens != null)
                {
                    for (int i = 0; i < config.apiTokens.Length; i++)
                    {
                        config.apiTokens[i] = config.apiTokens[i]?.Trim();
                    }
                }

                return config;
            }
            catch (Exception e)
            {
                Debug.LogError($"[Pipeline] Failed to load config from {path}: {e.Message}");
                return new PipelineConfig();
            }
        }

        /// <summary>Forces the next Instance access to re-read the file from disk.</summary>
        public static void Reload() => _instance = null;

        /// <summary>
        /// Writes this instance's current values back to disk. Deliberately does NOT
        /// touch the static _instance reference or call Reload() -- callers (e.g.
        /// PipelineConfigPanel) mutate PipelineConfig.Instance's fields in place and then
        /// call this on that same instance, so any PipelineApiClient already holding a
        /// reference to PipelineConfig.Instance sees the new values immediately, without
        /// needing to be recreated. This is what makes changing the server address from
        /// inside the running app actually take effect right away.
        /// </summary>
        public void SaveToDisk()
        {
            string path = FilePath;
            try
            {
                File.WriteAllText(path, JsonUtility.ToJson(this, true));
                Debug.Log($"[Pipeline] Config saved to {path}");
            }
            catch (Exception e)
            {
                Debug.LogError($"[Pipeline] Failed to save config to {path}: {e.Message}");
            }
        }
    }
}
