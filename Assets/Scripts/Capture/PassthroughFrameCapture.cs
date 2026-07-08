using Pipeline;
using UnityEngine;

// Verified against the installed com.meta.xr.mrutilitykit@201.0.0 package source:
// PassthroughCameraAccess actually lives in namespace Meta.XR (not Meta.XR.MRUtilityKit,
// despite shipping in the MRUK package).
using Meta.XR;

namespace Capture
{
    /// <summary>
    /// Wraps Meta's PassthroughCameraAccess (MRUK) component to grab a single RGB frame
    /// plus the real intrinsics and world pose of the camera at that exact moment --
    /// CLAUDE.md step 1 ("Unity captures an RGB passthrough frame ... at the same
    /// moment") and step 4 (real intrinsics for /select).
    ///
    /// Setup this depends on (see docs/unity-setup.md):
    /// - PassthroughCameraAccess component added to a GameObject (commonly the Camera
    ///   Rig), with CameraPosition/RequestedResolution configured.
    /// - "horizonos.permission.HEADSET_CAMERA" (or android.permission.CAMERA) granted.
    /// - Passthrough enabled in the scene.
    /// </summary>
    public class PassthroughFrameCapture : MonoBehaviour
    {
        [SerializeField] private PassthroughCameraAccess _cameraAccess;

        [Tooltip("Pick a concrete resolution/aspect ratio you've tested with -- do not " +
                 "auto-select the largest available. See 'Incorrect Resolution Handling' " +
                 "in Meta's Passthrough Camera API docs.")]
        [SerializeField] private Vector2Int _requestedResolution = new Vector2Int(1280, 960);

        private void Reset()
        {
            _cameraAccess = GetComponentInChildren<PassthroughCameraAccess>();
        }

        /// <summary>
        /// Captures the current camera frame synchronously. Returns false (and logs why)
        /// if the camera isn't ready yet -- e.g. permission not granted, or component not
        /// enabled long enough to receive its first frame.
        /// </summary>
        public bool TryCapture(out CapturedFrame frame)
        {
            frame = default;

            if (_cameraAccess == null)
            {
                Debug.LogError("[Capture] No PassthroughCameraAccess assigned.");
                return false;
            }

            Texture source = _cameraAccess.GetTexture();
            if (source == null)
            {
                Debug.LogWarning("[Capture] PassthroughCameraAccess has no texture yet " +
                                  "(permission not granted, or component not enabled).");
                return false;
            }

            int width = source.width;
            int height = source.height;

            // Blit + ReadPixels rather than a direct CopyTexture/GetPixels32: the camera
            // texture may be a native/external texture (not a plain Texture2D), and this
            // path works uniformly regardless of the underlying texture type.
            RenderTexture rt = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32);
            RenderTexture prevActive = RenderTexture.active;
            try
            {
                Graphics.Blit(source, rt);
                RenderTexture.active = rt;

                var snapshot = new Texture2D(width, height, TextureFormat.RGBA32, false);
                snapshot.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                snapshot.Apply();

                byte[] png = snapshot.EncodeToPNG();
                Destroy(snapshot);

                var intr = _cameraAccess.Intrinsics; // .FocalLength / .PrincipalPoint confirmed against installed package source
                Pose pose = _cameraAccess.GetCameraPose();

                frame = new CapturedFrame
                {
                    ColorPng = png,
                    Width = width,
                    Height = height,
                    Intrinsics = new CameraIntrinsics(
                        intr.FocalLength.x, intr.FocalLength.y,
                        intr.PrincipalPoint.x, intr.PrincipalPoint.y),
                    CameraWorldPosition = pose.position,
                    CameraWorldRotation = pose.rotation,
                    CaptureTimestamp = _cameraAccess.Timestamp // DateTime, not a double -- see CapturedFrame
                };
                return true;
            }
            finally
            {
                RenderTexture.active = prevActive;
                RenderTexture.ReleaseTemporary(rt);
            }
        }
    }
}
