using System;
using System.IO;
using System.Threading.Tasks;
using Capture;
using Pipeline;
using UnityEngine;

// Verified against the installed com.meta.xr.mrutilitykit@201.0.0 package source:
// both types live in namespace Meta.XR (not Meta.XR.MRUtilityKit).
using Meta.XR;

namespace Interaction
{
    /// <summary>
    /// Drives the full "point at a real object" flow end to end (CLAUDE.md steps 1-7):
    /// capture RGB+depth -> convert the pointing ray to a pixel in that frame -> upload
    /// -> /select -> poll the job -> fetch the object -> download + instantiate the mesh
    /// at the captured camera's pose.
    ///
    /// Call OnPointerSelect(ray) from whatever input binding fires the "select" action
    /// (controller trigger, hand pinch, etc.) with a world-space ray -- wiring that up to
    /// the actual Input System / OVR controller action is left to you, since it depends
    /// on which input method you end up using.
    ///
    /// Per CLAUDE.md's "Known open risks": reconstruction latency is unmeasured and may
    /// take tens of seconds to minutes, and no request has been driven through /select
    /// end-to-end yet. Treat the first real run of this script as validating the whole
    /// pipeline, not just this code -- OnStatusChanged is exposed specifically so you can
    /// wire up a visible "processing..." state instead of assuming this returns quickly.
    /// </summary>
    public class PointSelectController : MonoBehaviour
    {
        [Header("Passthrough / MRUK")]
        [SerializeField] private PassthroughCameraAccess _cameraAccess;
        [SerializeField] private EnvironmentRaycastManager _raycastManager;

        [Header("Capture")]
        [SerializeField] private PassthroughFrameCapture _frameCapture;
        [SerializeField] private DepthFrameCapture _depthCapture; // optional; leave null to always skip depth

        [Header("Mesh")]
        [SerializeField] private MeshInstantiator _meshInstantiator;

        [Header("Polling")]
        [SerializeField] private float _pollIntervalSeconds = 2f;

        [Header("Debugging")]
        [Tooltip("Write every captured frame's exact color/depth PNG bytes -- the same " +
                 "bytes that get uploaded -- to Application.persistentDataPath/debug-captures " +
                 "so you can adb pull and open them on a PC to verify passthrough capture is " +
                 "actually returning real image data before worrying about the rest of the " +
                 "pipeline. Saved as soon as capture succeeds, before the raycast/upload/select " +
                 "steps, so a capture can be verified even if a later step fails.")]
        [SerializeField] private bool _saveDebugCapturesToDisk = true;

        /// <summary>Fired with a short human-readable status string at each step -- hook
        /// this up to a "processing" UI element. Not a substitute for real progress UX,
        /// just enough to see the flow isn't stuck.</summary>
        public event Action<string> OnStatusChanged;

        /// <summary>Fired with the instantiated GameObject once placement succeeds.</summary>
        public event Action<GameObject> OnObjectPlaced;

        private PipelineApiClient _client;
        private bool _busy;

        private void Awake()
        {
            _client = new PipelineApiClient();
        }

        /// <summary>Entry point -- call with a world-space pointing ray (controller ray,
        /// hand ray, etc.) at the moment the user selects an object.</summary>
        public async void OnPointerSelect(Ray pointerRay)
        {
            if (_busy)
            {
                Report("Already processing a selection -- ignoring new pointer input.");
                return;
            }

            _busy = true;
            try
            {
                await RunSelectFlow(pointerRay);
            }
            catch (PipelineApiException e)
            {
                Report($"Pipeline server error: {e.Message}");
            }
            catch (Exception e)
            {
                Report($"Unexpected error: {e.Message}");
                Debug.LogException(e);
            }
            finally
            {
                _busy = false;
            }
        }

        private async Task RunSelectFlow(Ray pointerRay)
        {
            Report("Capturing frame...");
            if (_frameCapture == null || !_frameCapture.TryCapture(out CapturedFrame frame))
            {
                Report("Frame capture failed -- see console.");
                return;
            }

            byte[] depthPng = null;
            bool hasDepth = _depthCapture != null &&
                            _depthCapture.TryCapture(frame.Width, frame.Height, out depthPng);
            frame.DepthPng16 = hasDepth ? depthPng : null;

            if (_saveDebugCapturesToDisk)
            {
                SaveDebugCapture(frame);
            }

            if (_raycastManager == null || !_raycastManager.Raycast(pointerRay, out EnvironmentRaycastHit hit))
            {
                Report("Pointing ray didn't hit any scanned surface. Point at something " +
                       "the headset has already scanned (Scene/Space Setup), closer to the user.");
                return;
            }

            // Project the real-world hit point back into the already-captured RGB frame to
            // get the pixel coordinate /select needs. Explicitly pass the POSE CAPTURED
            // WITH THE FRAME here, not the live/current one: WorldToViewportPoint defaults
            // to PassthroughCameraAccess.GetCameraPose(), which resolves to whatever the
            // camera's *most recent* frame timestamp is -- and the camera keeps streaming
            // in the background while we do the raycast, so by this point that may already
            // be a newer frame than the one we captured and are about to upload. Passing
            // frame.CameraWorldPosition/Rotation (captured at TryCapture() time, in lockstep
            // with the ColorPng bytes) keeps the reprojection synchronized to the actual
            // uploaded image regardless of how much the head moved since.
            var capturePose = new Pose(frame.CameraWorldPosition, frame.CameraWorldRotation);
            Vector2 viewportPoint = _cameraAccess.WorldToViewportPoint(hit.point, capturePose);
            float pixelX = viewportPoint.x * frame.Width;
            // NOTE (verify-on-first-real-test): flip this if the selected pixel ends up
            // vertically mirrored relative to the uploaded image -- see the same caveat
            // in DepthFrameCapture. Viewport-space here is bottom-left origin (per
            // PassthroughCameraAccess's own doc comment), same convention Texture2D.SetPixel
            // uses, so SaveDebugSelectionMarker below should need no flip if this is right.
            float pixelY = viewportPoint.y * frame.Height;

            if (_saveDebugCapturesToDisk)
            {
                SaveDebugSelectionMarker(frame, pixelX, pixelY);
            }

            Report("Uploading capture...");
            string projectName = $"vr_{DateTime.UtcNow:yyyyMMdd_HHmmss}";
            string projectId = await _client.CreateProjectAsync(frame.ColorPng, frame.DepthPng16, projectName);

            CameraIntrinsics? intrinsics = hasDepth ? frame.Intrinsics : (CameraIntrinsics?)null;

            Report("Requesting reconstruction...");
            string jobId = await _client.SelectAsync(projectId, pixelX, pixelY, intrinsics: intrinsics);

            JobStatusResponse final = await _client.PollJobUntilDoneAsync(
                jobId,
                status => Report($"Processing ({status.Stage ?? status.Status})..."),
                _pollIntervalSeconds);

            if (final.IsError)
            {
                Report($"Reconstruction failed: {final.Error}");
                return;
            }

            Report("Fetching object manifest...");
            ObjectsResponse objects = await _client.GetObjectsAsync(projectId);
            if (objects.Objects == null || objects.Objects.Length == 0)
            {
                Report("Job finished but no objects were returned.");
                return;
            }

            ObjectEntry obj = objects.Objects[0]; // /select always reconstructs exactly one object
            Report("Downloading mesh...");
            byte[] glb = await _client.DownloadBytesAsync(obj.MeshUrl);

            GameObject placed;
            string label = obj.Description ?? "ReconstructedObject";
            if (hasDepth)
            {
                // Metric, camera-relative pose is baked into the mesh -- instantiate at
                // the CAPTURED camera's world transform (this frame's, not wherever the
                // headset is NOW) per docs/pipeline-api.md "Object placement for Unity".
                placed = await _meshInstantiator.LoadAndPlaceAsync(
                    glb, frame.CameraWorldPosition, frame.CameraWorldRotation, label);
            }
            else
            {
                // No depth -> shape-only mesh, not metrically grounded (see API doc).
                // Rough fallback: drop it at the raycast hit point, facing the capture
                // camera. This is explicitly a placeholder, not a real placement solution.
                Quaternion facing = Quaternion.LookRotation(hit.point - frame.CameraWorldPosition, Vector3.up);
                placed = await _meshInstantiator.LoadAndPlaceAsync(glb, hit.point, facing, label);
            }

            if (placed == null)
            {
                Report("Mesh load failed -- see console.");
                return;
            }

            Report("Done.");
            OnObjectPlaced?.Invoke(placed);
        }

        /// <summary>Writes the exact bytes about to be uploaded to
        /// Application.persistentDataPath/debug-captures/ so they can be pulled off the
        /// headset and opened on a PC. This is the single most reliable way to check
        /// whether PassthroughFrameCapture is returning a real photo versus blank/garbage
        /// data -- it's the literal byte array CreateProjectAsync sends, not a
        /// re-render or approximation of it.</summary>
        private void SaveDebugCapture(CapturedFrame frame)
        {
            try
            {
                string dir = Path.Combine(Application.persistentDataPath, "debug-captures");
                Directory.CreateDirectory(dir);

                string stamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff");
                string colorPath = Path.Combine(dir, $"{stamp}_color.png");
                File.WriteAllBytes(colorPath, frame.ColorPng);
                Debug.Log($"[PointSelect] Saved debug capture ({frame.Width}x{frame.Height}, " +
                          $"{frame.ColorPng.Length} bytes) to {colorPath}");

                if (frame.DepthPng16 != null)
                {
                    string depthPath = Path.Combine(dir, $"{stamp}_depth.png");
                    File.WriteAllBytes(depthPath, frame.DepthPng16);
                    Debug.Log($"[PointSelect] Saved debug depth capture ({frame.DepthPng16.Length} " +
                              $"bytes) to {depthPath}");
                }
                else
                {
                    Debug.Log("[PointSelect] No depth capture this time (depth capture " +
                              "disabled or unavailable) -- only color was saved.");
                }

                Debug.Log("[PointSelect] Pull with: adb pull " +
                          $"\"{dir}\" .  (run against your headset's persistentDataPath, " +
                          "not this literal PC path)");
            }
            catch (Exception e)
            {
                // Never let a debug convenience feature break the real capture flow.
                Debug.LogWarning($"[PointSelect] Failed to save debug capture: {e.Message}");
            }
        }

        /// <summary>Draws a red crosshair at the computed selection pixel on a copy of the
        /// captured color image and saves it to Application.persistentDataPath/debug-captures/.
        /// This is the direct way to check the raycast->pixel math is actually right: pull
        /// this file and see whether the crosshair lands on the real-world object you
        /// pointed at, or somewhere else entirely (wrong camera pose, flipped axis, wrong
        /// camera selected, etc. would all show up here as a visibly wrong marker
        /// position).</summary>
        private void SaveDebugSelectionMarker(CapturedFrame frame, float pixelX, float pixelY)
        {
            Texture2D tex = null;
            try
            {
                tex = new Texture2D(2, 2);
                if (!tex.LoadImage(frame.ColorPng))
                {
                    Debug.LogWarning("[PointSelect] Could not decode captured PNG to draw a debug marker.");
                    return;
                }

                DrawCrosshair(tex, Mathf.RoundToInt(pixelX), Mathf.RoundToInt(pixelY), 14, Color.red);

                string dir = Path.Combine(Application.persistentDataPath, "debug-captures");
                Directory.CreateDirectory(dir);
                string stamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff");
                string path = Path.Combine(dir, $"{stamp}_selection_marker.png");
                File.WriteAllBytes(path, tex.EncodeToPNG());

                Debug.Log($"[PointSelect] Selection pixel ({pixelX:F0}, {pixelY:F0}) of " +
                          $"{frame.Width}x{frame.Height} -- marker saved to {path}. Pull it " +
                          "and check the red crosshair actually lands on what you pointed at.");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[PointSelect] Failed to save selection marker: {e.Message}");
            }
            finally
            {
                if (tex != null) Destroy(tex);
            }
        }

        private static void DrawCrosshair(Texture2D tex, int cx, int cy, int armLength, Color color)
        {
            for (int d = -armLength; d <= armLength; d++)
            {
                SetPixelSafe(tex, cx + d, cy, color);
                SetPixelSafe(tex, cx + d, cy + 1, color);
                SetPixelSafe(tex, cx, cy + d, color);
                SetPixelSafe(tex, cx + 1, cy + d, color);
            }
            tex.Apply();
        }

        private static void SetPixelSafe(Texture2D tex, int x, int y, Color color)
        {
            if (x < 0 || y < 0 || x >= tex.width || y >= tex.height) return;
            tex.SetPixel(x, y, color);
        }

        private void Report(string message)
        {
            Debug.Log($"[PointSelect] {message}");
            OnStatusChanged?.Invoke(message);
        }
    }
}
