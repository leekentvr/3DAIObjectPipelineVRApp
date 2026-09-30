using System;
using System.IO;
using System.Threading.Tasks;
using Pipeline;
using UnityEngine;

namespace Testing
{
    /// <summary>
    /// Loads a .glb file straight off disk through the exact runtime path the real flow
    /// uses (GltfImport -> MeshInstantiator), with NO server and NO headset. This is the
    /// isolation test for "the returned glb has an 'unassignedobject' error": it takes the
    /// pipeline, the network, /select, and passthrough capture out of the picture so you're
    /// testing only "can Unity load and render THIS glb".
    ///
    /// Why this is the useful bisection:
    ///  * Loads fine here in the Editor but broken on the Quest  -> almost certainly shader
    ///    stripping. The Editor has every shader; an Android/URP *build* strips the glTFast
    ///    shaders unless you keep them (Project Settings -> Graphics -> Always Included
    ///    Shaders, or a shader variant collection). The imported object -- whose glTF node
    ///    SAM3D often names "UnassignedObject" for an unlabelled select-flow mesh -- then
    ///    renders pink/blank and logs a missing-shader error.
    ///  * Broken here too (in the Editor) -> the glb itself is the problem (an unsupported
    ///    glTF extension, bad data, etc.). The CollectingLogger inside MeshInstantiator will
    ///    print glTFast's exact reason to the Console.
    ///
    /// Setup:
    /// 1. Put this component on any GameObject (empty is fine) and assign _meshInstantiator.
    /// 2. Point _glbFilePath at a real .glb:
    ///      - In the Editor: an absolute path works, e.g.
    ///        "C:/Users/you/Downloads/transformedobject_0.glb".
    ///      - On device: leave it a bare filename or relative path and it's resolved under
    ///        Application.persistentDataPath -- adb push the glb there first. This lets you
    ///        reproduce a Quest-only failure on the actual build with a known-good file,
    ///        removing the server as a variable.
    /// 3. Enter Play mode, right-click the component header -> "Load Local GLB" (or call
    ///    LoadNow() from anywhere). Watch the Console; on success the mesh appears at
    ///    _placementPosition.
    /// </summary>
    public class LocalGlbLoadTest : MonoBehaviour
    {
        [Header("File")]
        [Tooltip("Absolute path (Editor) or a name/relative path resolved under " +
                 "Application.persistentDataPath (device). Points at a .glb to load.")]
        [SerializeField] private string _glbFilePath = "transformedobject_0.glb";

        [Header("Mesh loading")]
        [SerializeField] private MeshInstantiator _meshInstantiator;
        [SerializeField] private Vector3 _placementPosition = new Vector3(0, 1, 2);

        [Tooltip("Auto-load once on Start (handy for testing inside a built app on the " +
                 "headset, where you can't right-click an Inspector context menu).")]
        [SerializeField] private bool _loadOnStart = false;

        private bool _loading;

        private void Start()
        {
            if (_loadOnStart) LoadNow();
        }

        [ContextMenu("Load Local GLB")]
        public void LoadNow()
        {
            if (_loading)
            {
                Log("Already loading -- wait for it to finish.");
                return;
            }
            _ = LoadAsync();
        }

        public async Task LoadAsync()
        {
            _loading = true;
            try
            {
                if (_meshInstantiator == null)
                {
                    LogError("No MeshInstantiator assigned.");
                    return;
                }

                string path = ResolvePath(_glbFilePath);
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                {
                    LogError($"No .glb found for '{_glbFilePath}'. Tried: " +
                             string.Join("  |  ", CandidatePaths(_glbFilePath)) + ". " +
                             "Use an absolute path, a project-relative path like " +
                             "'Assets/Scripts/Testing/scene.glb', or (on device) a name " +
                             "under Application.persistentDataPath.");
                    return;
                }

                byte[] glb;
                try
                {
                    glb = File.ReadAllBytes(path);
                }
                catch (Exception e)
                {
                    LogError($"Couldn't read '{path}': {e.Message}");
                    return;
                }

                Log($"Loading {glb.Length} bytes from {path} via glTFast...");
                GameObject placed = await _meshInstantiator.LoadAndPlaceAsync(
                    glb, _placementPosition, Quaternion.identity, Path.GetFileNameWithoutExtension(path));

                if (placed == null)
                {
                    LogError("glTFast failed to load/instantiate -- see the [Mesh][glTFast] " +
                             "lines above for the exact reason.");
                    return;
                }

                Log($"SUCCESS -- '{placed.name}' instantiated at {_placementPosition}. " +
                    "If it's here in the Editor but pink/blank/missing in a Quest build, " +
                    "that's shader stripping (see this class's doc comment). If it's tiny/" +
                    "huge/rotated, that's the glTF<->Unity axis convention, handled by " +
                    "MeshInstantiator._axisCorrectionEulerDegrees -- not a load failure.");
            }
            catch (Exception e)
            {
                LogError($"Unexpected error: {e.Message}");
                Debug.LogException(e);
            }
            finally
            {
                _loading = false;
            }
        }

        /// <summary>Returns the first candidate location that actually exists, so the same
        /// field accepts an absolute path (Editor), a project-relative path like
        /// "Assets/Scripts/Testing/scene.glb", or a bare filename under
        /// persistentDataPath/StreamingAssets (device). Falls back to the first candidate
        /// for the error message if none exist.</summary>
        private static string ResolvePath(string configured)
        {
            if (string.IsNullOrWhiteSpace(configured)) return configured;
            foreach (string candidate in CandidatePaths(configured))
            {
                if (File.Exists(candidate)) return candidate;
            }
            return CandidatePaths(configured)[0];
        }

        private static string[] CandidatePaths(string configured)
        {
            if (Path.IsPathRooted(configured))
            {
                return new[] { configured };
            }

            // Application.dataPath is the project's Assets folder; its parent is the
            // project root, so a path starting with "Assets/..." resolves there.
            string projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath;
            return new[]
            {
                Path.Combine(projectRoot, configured),                    // project-relative, e.g. "Assets/.../scene.glb"
                Path.Combine(Application.dataPath, configured),           // relative to Assets/
                Path.Combine(Application.persistentDataPath, configured), // device: adb push target
                Path.Combine(Application.streamingAssetsPath, configured) // StreamingAssets
            };
        }

        private static void Log(string message) => Debug.Log($"[LocalGlbLoadTest] {message}");
        private static void LogError(string message) => Debug.LogError($"[LocalGlbLoadTest] {message}");
    }
}
