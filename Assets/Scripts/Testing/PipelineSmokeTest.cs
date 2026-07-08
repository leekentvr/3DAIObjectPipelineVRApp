using System;
using System.Threading.Tasks;
using Pipeline;
using UnityEngine;

namespace Testing
{
    /// <summary>
    /// Runs the pipeline API + mesh loading flow from inside the Unity Editor, on a
    /// normal PC, with NO headset and no Meta XR components involved. It deliberately
    /// skips PassthroughFrameCapture/DepthFrameCapture/PointSelectController -- those
    /// need real device passthrough to test. This script exists to answer a narrower,
    /// much easier to test question: "does my Unity project actually talk to the
    /// pipeline server, run /select, and load the resulting .glb?"
    ///
    /// Per CLAUDE.md's "Known open risks" (as of the original hand-off, no request had
    /// ever been driven through /select end-to-end) -- this is the fastest way to find
    /// out whether that works at all, before ever putting a headset on.
    ///
    /// Setup:
    /// 1. Add this component to any GameObject in a scene (an empty GameObject is fine).
    /// 2. Assign _testImage -- a real photo with a clear, isolated-ish object in it
    ///    (import settings: Read/Write Enabled must be ON, or EncodeToPNG will throw).
    ///    A blank/synthetic image won't reconstruct anything meaningful -- SAM3 needs a
    ///    real object to segment.
    /// 3. Set _testPointNormalized to roughly where that object is in the image (0,0 =
    ///    bottom-left, 1,1 = top-right, matching Unity's Texture2D convention).
    /// 4. Add a MeshInstantiator component somewhere and assign it.
    /// 5. Make sure Application.persistentDataPath/pipeline-config.json points at your
    ///    actual server (see docs/unity-setup.md) before running.
    /// 6. Enter Play mode, then right-click this component's header in the Inspector ->
    ///    "Run Pipeline Smoke Test" (or call RunTest() from anywhere). Watch the Console
    ///    for [SmokeTest] progress lines; on success the mesh appears in the Scene at
    ///    _placementPosition, and is also logged with its object description.
    /// </summary>
    public class PipelineSmokeTest : MonoBehaviour
    {
        [Header("Test input")]
        [Tooltip("A real photo containing a clear object. Import settings must have Read/Write Enabled checked.")]
        [SerializeField] private Texture2D _testImage;

        [Tooltip("Where the object is in the image, normalized (0,0=bottom-left, 1,1=top-right).")]
        [SerializeField] private Vector2 _testPointNormalized = new Vector2(0.5f, 0.5f);

        [Header("Mesh loading")]
        [SerializeField] private MeshInstantiator _meshInstantiator;
        [SerializeField] private Vector3 _placementPosition = new Vector3(0, 1, 2);

        [Header("Polling")]
        [SerializeField] private float _pollIntervalSeconds = 2f;

        private PipelineApiClient _client;
        private bool _running;

        private void Awake()
        {
            _client = new PipelineApiClient();
        }

        [ContextMenu("Run Pipeline Smoke Test")]
        public void RunTest()
        {
            if (_running)
            {
                Log("Already running -- wait for it to finish.");
                return;
            }
            _ = RunTestAsync(); // fire-and-forget from the context menu / inspector button
        }

        public async Task RunTestAsync()
        {
            _running = true;
            try
            {
                if (_testImage == null)
                {
                    LogError("No _testImage assigned.");
                    return;
                }
                if (_meshInstantiator == null)
                {
                    LogError("No MeshInstantiator assigned.");
                    return;
                }

                byte[] pngBytes;
                try
                {
                    pngBytes = _testImage.EncodeToPNG();
                }
                catch (UnityException e)
                {
                    LogError($"Couldn't encode _testImage to PNG -- is 'Read/Write Enabled' checked in its " +
                             $"import settings? ({e.Message})");
                    return;
                }

                float pixelX = _testPointNormalized.x * _testImage.width;
                float pixelY = _testPointNormalized.y * _testImage.height;

                Log($"Uploading test image ({_testImage.width}x{_testImage.height})...");
                string projectName = $"smoketest_{DateTime.UtcNow:yyyyMMdd_HHmmss}";
                string projectId = await _client.CreateProjectAsync(pngBytes, depthBytes: null, projectName: projectName);
                Log($"Project created: {projectId}");

                // No depth/intrinsics on purpose -- this is a plain photo, not a real
                // aligned depth capture, so we're only testing plumbing + mesh format
                // here, not metric placement. See PointSelectController for the real,
                // depth-assisted flow.
                Log($"Requesting /select at pixel ({pixelX:F0}, {pixelY:F0})...");
                string jobId = await _client.SelectAsync(projectId, pixelX, pixelY);
                Log($"Job started: {jobId}");

                JobStatusResponse final = await _client.PollJobUntilDoneAsync(
                    jobId,
                    status => Log($"Job status: {status.Status} (stage: {status.Stage ?? "-"})"),
                    _pollIntervalSeconds);

                if (final.IsError)
                {
                    LogError($"Job failed: {final.Error}");
                    return;
                }

                Log("Job done. Fetching object manifest...");
                ObjectsResponse objects = await _client.GetObjectsAsync(projectId);
                if (objects.Objects == null || objects.Objects.Length == 0)
                {
                    LogError("Job finished but /objects returned nothing.");
                    return;
                }

                ObjectEntry obj = objects.Objects[0];
                Log($"Got object: index={obj.Index} description=\"{obj.Description}\" mesh_url={obj.MeshUrl}");

                Log("Downloading mesh...");
                byte[] glb = await _client.DownloadBytesAsync(obj.MeshUrl);
                Log($"Downloaded {glb.Length} bytes. Loading with glTFast...");

                GameObject placed = await _meshInstantiator.LoadAndPlaceAsync(
                    glb, _placementPosition, Quaternion.identity, obj.Description ?? "SmokeTestObject");

                if (placed == null)
                {
                    LogError("glTFast failed to load/instantiate the mesh -- see errors above.");
                    return;
                }

                Log($"SUCCESS -- '{placed.name}' instantiated at {_placementPosition}. " +
                    "Check the Scene view (it may be tiny/large/oddly rotated -- that's the " +
                    "axis-convention risk noted in CLAUDE.md, not necessarily a pipeline bug).");
            }
            catch (PipelineApiException e)
            {
                LogError($"Pipeline server error ({e.StatusCode}): {e.Message}");
            }
            catch (Exception e)
            {
                LogError($"Unexpected error: {e.Message}");
                Debug.LogException(e);
            }
            finally
            {
                _running = false;
            }
        }

        private static void Log(string message) => Debug.Log($"[SmokeTest] {message}");
        private static void LogError(string message) => Debug.LogError($"[SmokeTest] {message}");
    }
}
