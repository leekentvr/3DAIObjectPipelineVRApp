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

        [Header("Pipeline project")]
        [Tooltip("Base project name. Editable here in the engine. A unique timestamp suffix " +
                 "is appended per capture so every selection is its own server-side project " +
                 "(metadata is tracked server-side). Base must be letters/digits/_/- only " +
                 "(server validates ^[A-Za-z0-9_-]{1,100}$).")]
        [SerializeField] private string _projectName = "ToyodaLab";

        [Header("Polling")]
        [SerializeField] private float _pollIntervalSeconds = 2f;

        [Header("Rotation")]
        [Tooltip("If true (default -- try this first now that the pipeline's Y-up/Z-up bake " +
                 "fix is in, see sam-3d-objects/notebook/run_sam3d.py's transform_like_notebook), " +
                 "trust the mesh's own baked orientation: the object is placed with identity " +
                 "rotation (modulo MeshInstantiator._axisCorrectionEulerDegrees), letting SAM3D's " +
                 "reconstructed pose show through untouched. If false, falls back to the old " +
                 "yaw-to-camera heuristic below -- kept only so the two can be compared on a real " +
                 "capture; that heuristic predates the pipeline fix and was a stopgap for when " +
                 "the baked rotation looked wrong.")]
        [SerializeField] private bool _trustBakedRotation = true;

        [Header("Placed objects")]
        [Tooltip("Max reconstructed objects kept in the scene. When exceeded, the oldest is " +
                 "destroyed (its saved server placement is unaffected). Unbounded accumulation " +
                 "degrades Quest framerate over a long session. 0 = unlimited.")]
        [SerializeField] private int _maxPlacedObjects = 12;

        [Header("Debugging")]
        [Tooltip("Write every captured frame's exact color/depth PNG bytes -- the same " +
                 "bytes that get uploaded -- to Application.persistentDataPath/debug-captures " +
                 "so you can adb pull and open them on a PC to verify passthrough capture is " +
                 "actually returning real image data before worrying about the rest of the " +
                 "pipeline. Saved as soon as capture succeeds, before the raycast/upload/select " +
                 "steps, so a capture can be verified even if a later step fails.")]
        [SerializeField] private bool _saveDebugCapturesToDisk = true;

        /// <summary>Base project/room name used to name new captures (see _projectName).</summary>
        public string ProjectName
        {
            get => _projectName;
            set => _projectName = value;
        }

        /// <summary>Fired with a short human-readable status string at each step -- hook
        /// this up to a "processing" UI element. Not a substitute for real progress UX,
        /// just enough to see the flow isn't stuck.</summary>
        public event Action<string> OnStatusChanged;

        /// <summary>Fired with the instantiated GameObject once placement succeeds.</summary>
        public event Action<GameObject> OnObjectPlaced;

        private PipelineApiClient _client;
        private bool _busy;
        private readonly System.Collections.Generic.Queue<GameObject> _placedObjects =
            new System.Collections.Generic.Queue<GameObject>();

        /// <summary>True while a selection is being processed end to end (capture ->
        /// upload -> reconstruction -> placement). While true, new selections are
        /// rejected -- UI can use this to show a locked/pending state.</summary>
        public bool IsBusy => _busy;

        /// <summary>Fired when _busy changes, with the new value. Complements
        /// OnStatusChanged for UI that wants to lock/unlock rather than parse strings.</summary>
        public event Action<bool> OnBusyChanged;

        /// <summary>Fired ~4x/second while a selection is processing, with elapsed time.
        /// Bind a timer label to this to show the user how long it's been running (the
        /// status string also carries mm:ss, but this updates smoothly between poll ticks).
        /// Fires once with TimeSpan.Zero when processing ends so a bound label can reset.</summary>
        public event Action<TimeSpan> OnProcessingTimeChanged;

        // Measures how long the current selection has been processing (capture -> upload ->
        // reconstruction -> placement). Update() pumps OnProcessingTimeChanged from it.
        private readonly System.Diagnostics.Stopwatch _processingStopwatch = new System.Diagnostics.Stopwatch();
        private float _lastTimerEmit;

        private void Awake()
        {
            _client = new PipelineApiClient();
        }

        private void Update()
        {
            if (!_processingStopwatch.IsRunning) return;
            // Throttle to ~4 updates/sec: smooth enough for a mm:ss readout, no per-frame spam.
            if (Time.unscaledTime - _lastTimerEmit < 0.25f) return;
            _lastTimerEmit = Time.unscaledTime;
            OnProcessingTimeChanged?.Invoke(_processingStopwatch.Elapsed);
        }

        /// <summary>Entry point -- call with a world-space pointing ray (controller ray,
        /// hand ray, etc.) at the moment the user selects an object.</summary>
        public async void OnPointerSelect(Ray pointerRay)
        {
            if (_busy)
            {
                Report("Pending -- still processing the previous selection. Input locked until it finishes.");
                return;
            }

            _busy = true;
            OnBusyChanged?.Invoke(true);
            _processingStopwatch.Restart();
            _lastTimerEmit = 0f;
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
                _processingStopwatch.Stop();
                OnProcessingTimeChanged?.Invoke(TimeSpan.Zero); // let a bound timer label reset
                _busy = false;
                OnBusyChanged?.Invoke(false);
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
            // Y-AXIS FLIP -- this is the fix for "the marker looks right but the server
            // processes the wrong spot". Unity viewport space is BOTTOM-LEFT origin
            // (y=0 at the bottom), but the server reads point[1] as a row in input.png,
            // which is an ordinary raster image: TOP-LEFT origin (row 0 at the top). So
            // the value we send must be measured from the top. Without this flip the point
            // is vertically mirrored, and SAM3D segments whatever sits the same distance
            // from the OPPOSITE edge of the frame. The debug marker hid this because it was
            // ALSO drawn in bottom-left space, so it happened to land on the object while
            // the number sent to the server did not.
            float pixelY = (1f - viewportPoint.y) * frame.Height;

            if (_saveDebugCapturesToDisk)
            {
                // Pass the exact top-left-origin pixel we're about to send. The marker
                // helper flips it back into Texture2D's bottom-left space so the crosshair
                // now verifies the REAL coordinate the server will use, not a lookalike.
                SaveDebugSelectionMarker(frame, pixelX, pixelY);
            }

            Report("Uploading capture...");
            string baseName = string.IsNullOrWhiteSpace(_projectName) ? "ToyodaLab" : _projectName.Trim();
            // Unique per capture -> a fresh server-side project every time, so no stale
            // meshes and no cross-run URL collisions. fff (ms) keeps rapid captures distinct.
            string projectName = $"{baseName}_{DateTime.UtcNow:yyyyMMdd_HHmmss_fff}";
            string projectId = await _client.CreateProjectAsync(frame.ColorPng, frame.DepthPng16, projectName);

            CameraIntrinsics? intrinsics = hasDepth ? frame.Intrinsics : (CameraIntrinsics?)null;

            Report("Requesting reconstruction...");
            string jobId = await _client.SelectAsync(projectId, pixelX, pixelY, intrinsics: intrinsics);
            Report($"Pending -- image sent for processing (job {jobId}). Waiting for the server...");

            JobStatusResponse final = await _client.PollJobUntilDoneAsync(
                jobId,
                status => Report($"Processing ({status.Stage ?? status.Status})... {FormatElapsed(_processingStopwatch.Elapsed)}"),
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

            // Log the FULL manifest every run -- this is how we tell "wrong object" apart
            // from "unexpected extra object". Each capture is now its own fresh project, and
            // the /select flow reconstructs exactly one object at index 0, so a healthy
            // manifest here has a single entry. More than one means the server produced extra
            // objects for this project -- worth a look, but index 0 is still this selection.
            Debug.Log($"[PointSelect] Objects manifest for '{projectId}': count={objects.Objects.Length}");
            foreach (ObjectEntry e in objects.Objects)
            {
                Debug.Log($"[PointSelect]   index={e.Index} desc=\"{e.Description}\" mesh={e.MeshUrl} mask={e.MaskUrl}");
            }
            if (objects.Objects.Length > 1)
            {
                Report($"Note: {objects.Objects.Length} objects returned for this capture (expected 1). " +
                       "Using index 0. Check the manifest log if it's not what you selected.");
            }

            // Deterministically take index 0 (the select-flow object), not just array slot 0.
            ObjectEntry obj = System.Array.Find(objects.Objects, e => e.Index == 0) ?? objects.Objects[0];

            if (_saveDebugCapturesToDisk)
            {
                await SaveDebugMask(obj); // pull the mask the server actually segmented, to compare vs where you clicked
            }

            Report("Downloading mesh...");
            byte[] glb = await _client.DownloadBytesAsync(obj.MeshUrl);

            string label = obj.Description ?? "ReconstructedObject";

            // Raycast-anchored placement: put the object where the pointing ray actually
            // hit the real, Unity-tracked environment (EnvironmentRaycast uses the headset's
            // own scene depth). This deliberately does NOT trust the server's baked
            // camera-relative transform for POSITION -- that's in SAM3D's camera convention
            // and depends on depth units/intrinsics we haven't reconciled yet. Anchoring on
            // Unity's own tracking gives a solid position regardless. Scale is still tunable
            // via MeshInstantiator._uniformScaleMultiplier until the metric path is fixed.
            // Depth is still uploaded above -- it improves the reconstruction itself, it just
            // no longer drives placement.
            //
            // ROTATION: previously this always yawed the object to face the capture camera,
            // discarding SAM3D's baked orientation entirely -- a stopgap from when rotation
            // "looked wrong" for reasons later traced to a Y-up/Z-up bug on the pipeline side
            // (see sam-3d-objects/notebook/run_sam3d.py's transform_like_notebook, which now
            // does the correct round-trip conversion). With that fixed upstream, the baked
            // rotation may be trustworthy now -- _trustBakedRotation toggles between the two
            // so you can compare them on a real capture.
            Quaternion facing;
            if (_trustBakedRotation)
            {
                facing = Quaternion.identity;
            }
            else
            {
                Vector3 toCamera = frame.CameraWorldPosition - hit.point;
                toCamera.y = 0f;
                facing = toCamera.sqrMagnitude > 1e-4f
                    ? Quaternion.LookRotation(toCamera, Vector3.up)
                    : Quaternion.identity;
            }
            GameObject placed = await _meshInstantiator.LoadAndAnchorAsync(glb, hit.point, facing, label);

            if (placed == null)
            {
                Report("Mesh load failed -- see console.");
                return;
            }

            // Tag the placed object with its server identity so its level of detail can be
            // re-requested and swapped in place later (see PlacedObjectLod / LodTuner). The
            // first-placed mesh is the server's default decimation, so CurrentRatio starts
            // unknown (-1). Only meaningful if the server exposes the LoD endpoint (lod_url).
            if (!string.IsNullOrEmpty(obj.LodUrl) || !string.IsNullOrEmpty(obj.FullMeshUrl))
            {
                var lod = placed.GetComponent<PlacedObjectLod>();
                if (lod == null) lod = placed.AddComponent<PlacedObjectLod>();
                lod.Configure(_client, _meshInstantiator, projectId, obj.Index, label,
                              grabbable: true, currentRatio: -1f);
            }

            // Persist where this object was placed (and re-save when hand-adjusted) so the
            // whole room can be reloaded later with everything back in place -- see RoomLoader.
            var placement = placed.GetComponent<PlacementSync>();
            if (placement == null) placement = placed.AddComponent<PlacementSync>();
            placement.Configure(_client, projectId, obj.Index);

            PlacedObjectsRoot.Adopt(placed.transform);
            TrackAndTrim(placed);

            Report($"Done in {FormatElapsed(_processingStopwatch.Elapsed)}.");
            OnObjectPlaced?.Invoke(placed);
        }

        /// <summary>Remembers a newly placed object and destroys the oldest ones beyond
        /// _maxPlacedObjects.</summary>
        private void TrackAndTrim(GameObject placed)
        {
            _placedObjects.Enqueue(placed);
            if (_maxPlacedObjects <= 0) return;
            while (_placedObjects.Count > _maxPlacedObjects)
            {
                GameObject oldest = _placedObjects.Dequeue();
                if (oldest != null) Destroy(oldest);
            }
            Resources.UnloadUnusedAssets();
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
        private void SaveDebugSelectionMarker(CapturedFrame frame, float pixelX, float pixelYTopLeft)
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

                // pixelYTopLeft is in the server's top-left-origin convention (exactly the
                // value sent in the /select request). Texture2D pixel coords are bottom-left
                // origin, so flip Y back before drawing -- this way the crosshair in the
                // saved PNG lands where the server will actually look.
                int drawX = Mathf.RoundToInt(pixelX);
                int drawY = Mathf.RoundToInt(frame.Height - pixelYTopLeft);
                DrawCrosshair(tex, drawX, drawY, 14, Color.red);

                string dir = Path.Combine(Application.persistentDataPath, "debug-captures");
                Directory.CreateDirectory(dir);
                string stamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff");
                string path = Path.Combine(dir, $"{stamp}_selection_marker.png");
                File.WriteAllBytes(path, tex.EncodeToPNG());

                Debug.Log($"[PointSelect] Selection pixel (top-left origin) ({pixelX:F0}, {pixelYTopLeft:F0}) " +
                          $"of {frame.Width}x{frame.Height} -- marker saved to {path}. Pull it " +
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

        /// <summary>Downloads the mask the server actually segmented for this object and
        /// saves it next to the capture/marker, so you can directly compare "what I clicked"
        /// (the red crosshair) against "what the server selected" (the white mask). If they
        /// disagree, the selection pixel or SAM3's exemplar matching is the culprit, not the
        /// mesh. Never lets a debug convenience break the real flow.</summary>
        private async Task SaveDebugMask(ObjectEntry obj)
        {
            if (string.IsNullOrEmpty(obj.MaskUrl)) return;
            try
            {
                byte[] maskBytes = await _client.DownloadBytesAsync(obj.MaskUrl);
                string dir = Path.Combine(Application.persistentDataPath, "debug-captures");
                Directory.CreateDirectory(dir);
                string stamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff");
                string path = Path.Combine(dir, $"{stamp}_mask_idx{obj.Index}.png");
                File.WriteAllBytes(path, maskBytes);
                Debug.Log($"[PointSelect] Saved server mask (index {obj.Index}) to {path}. " +
                          "Compare it against the same run's _selection_marker.png -- the white " +
                          "region is what the server segmented; the red crosshair is where you pointed.");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[PointSelect] Failed to save debug mask: {e.Message}");
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

        /// <summary>mm:ss (or h:mm:ss past an hour) for status text.</summary>
        private static string FormatElapsed(TimeSpan t)
        {
            return t.TotalHours >= 1
                ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
                : $"{(int)t.TotalMinutes:00}:{t.Seconds:00}";
        }

        private void Report(string message)
        {
            Debug.Log($"[PointSelect] {message}");
            OnStatusChanged?.Invoke(message);
        }
    }
}
