using UnityEngine;

// Verified against the installed com.meta.xr.mrutilitykit@201.0.0 package source:
// both PassthroughCameraAccess and EnvironmentRaycastManager/EnvironmentRaycastHit live
// in namespace Meta.XR (not Meta.XR.MRUtilityKit, despite shipping in the MRUK package).
using Meta.XR;

namespace Capture
{
    /// <summary>
    /// Produces a depth.png aligned with a PassthroughFrameCapture RGB frame, WITHOUT
    /// touching the raw per-pixel environment depth texture.
    ///
    /// Why: Meta's Environment Depth API (EnvironmentDepthManager) is documented and
    /// shipped as a GPU occlusion feature -- its public surface gives you an occlusion
    /// factor in a shader (via EnvironmentOcclusionURP.hlsl macros), not a plain "depth
    /// in meters at this pixel" function you can call from C#. Reading the true raw
    /// depth texture on the CPU (via its native texture id, e.g. through
    /// XRDisplaySubsystem.GetRenderTexture) is possible in principle but undocumented
    /// enough that we didn't want to guess at it here -- see the "unverified" TODO at
    /// the bottom of this file if you want to chase that path for higher resolution.
    ///
    /// Instead, this samples depth at a sparse grid of points using MRUK's
    /// EnvironmentRaycastManager (fully documented, used for exactly this kind of
    /// "how far away is the thing under this pixel" query -- see the CameraToWorld
    /// sample referenced in Meta's PCA docs), then nearest-neighbor-upsamples to the
    /// RGB frame's exact resolution so the two images line up pixel-for-pixel as
    /// docs/pipeline-api.md requires.
    ///
    /// Trade-off: lower spatial resolution than true sensor depth, and any point that
    /// doesn't hit a scanned surface is written as 0 (invalid/no-data) rather than a
    /// real distance. Per docs/pipeline-api.md, depth is optional -- if too few of the
    /// grid samples hit anything, prefer skipping depth entirely (TryCapture returns
    /// false) over uploading a mostly-empty depth map.
    /// </summary>
    public class DepthFrameCapture : MonoBehaviour
    {
        [SerializeField] private PassthroughCameraAccess _cameraAccess;
        [SerializeField] private EnvironmentRaycastManager _raycastManager;

        [Tooltip("Sparse sample grid resolution. Higher = smoother depth but more raycasts per capture.")]
        [SerializeField] private Vector2Int _gridSize = new Vector2Int(64, 48);

        [Tooltip("Raycast hits farther than this are treated as invalid (helps reject spurious far hits).")]
        [SerializeField] private float _maxRangeMeters = 8f;

        [Tooltip("Minimum fraction of grid samples that must hit something for the capture to be considered usable.")]
        [SerializeField] private float _minHitFraction = 0.3f;

        private void Reset()
        {
            _cameraAccess = GetComponentInChildren<PassthroughCameraAccess>();
            _raycastManager = GetComponentInChildren<EnvironmentRaycastManager>();
        }

        /// <summary>
        /// Samples depth for a frame that's outputWidth x outputHeight (pass the same
        /// dimensions as the matching PassthroughFrameCapture.TryCapture output).
        /// Returns false if too few samples hit real geometry -- caller should then just
        /// upload without depth/intrinsics rather than send a mostly-empty depth map.
        /// </summary>
        public bool TryCapture(int outputWidth, int outputHeight, out byte[] depthPng16Mm)
        {
            depthPng16Mm = null;

            if (_cameraAccess == null || _raycastManager == null)
            {
                Debug.LogError("[Capture] DepthFrameCapture is missing PassthroughCameraAccess or EnvironmentRaycastManager.");
                return false;
            }

            int gw = _gridSize.x;
            int gh = _gridSize.y;
            var gridMm = new ushort[gw * gh];
            int hitCount = 0;

            for (int gy = 0; gy < gh; gy++)
            {
                for (int gx = 0; gx < gw; gx++)
                {
                    // Sample at cell centers. NOTE (verify-on-first-real-test): whether
                    // viewport.y = 0 is the TOP or BOTTOM of the frame depends on
                    // PassthroughCameraAccess's convention, which we haven't confirmed
                    // against actual device output yet -- if the depth map comes out
                    // vertically flipped relative to the color image, flip this gy
                    // mapping (or the row order below) to match.
                    var viewportPoint = new Vector2(
                        (gx + 0.5f) / gw,
                        (gy + 0.5f) / gh);

                    Ray ray = _cameraAccess.ViewportPointToRay(viewportPoint);

                    ushort depthMm = 0;
                    if (_raycastManager.Raycast(ray, out EnvironmentRaycastHit hit))
                    {
                        float meters = Vector3.Distance(ray.origin, hit.point);
                        if (meters <= _maxRangeMeters)
                        {
                            depthMm = (ushort)Mathf.Clamp(Mathf.RoundToInt(meters * 1000f), 1, ushort.MaxValue);
                            hitCount++;
                        }
                    }

                    gridMm[gy * gw + gx] = depthMm;
                }
            }

            float hitFraction = (float)hitCount / (gw * gh);
            if (hitFraction < _minHitFraction)
            {
                Debug.LogWarning($"[Capture] Depth grid only hit {hitFraction:P0} of samples " +
                                   $"(min {_minHitFraction:P0}) -- skipping depth for this capture.");
                return false;
            }

            ushort[] fullRes = UpsampleNearest(gridMm, gw, gh, outputWidth, outputHeight);

            var tex = new Texture2D(outputWidth, outputHeight, TextureFormat.R16, false);
            tex.SetPixelData(fullRes, 0);
            tex.Apply();
            depthPng16Mm = tex.EncodeToPNG();
            Destroy(tex);

            return true;
        }

        private static ushort[] UpsampleNearest(ushort[] grid, int gw, int gh, int outW, int outH)
        {
            var result = new ushort[outW * outH];
            for (int y = 0; y < outH; y++)
            {
                int gy = Mathf.Clamp(y * gh / outH, 0, gh - 1);
                for (int x = 0; x < outW; x++)
                {
                    int gx = Mathf.Clamp(x * gw / outW, 0, gw - 1);
                    result[y * outW + x] = grid[gy * gw + gx];
                }
            }
            return result;
        }

        // TODO(higher-resolution path, unverified): if the sparse-grid depth turns out
        // too coarse for good SAM3D reconstructions, the alternative is reading
        // EnvironmentDepthManager's actual per-pixel depth texture on the CPU. Meta's
        // docs describe a method that returns a native texture id you can resolve to a
        // RenderTexture via XRDisplaySubsystem.GetRenderTexture, which you could then
        // AsyncGPUReadback and convert (their reversed-Z/NDC depth encoding isn't fully
        // documented publicly as of this writing -- expect to reverse-engineer or find
        // the conversion in Meta's shader include at
        // "Packages/com.meta.xr.sdk.core/Shaders/EnvironmentDepth/URP/EnvironmentOcclusionURP.hlsl"
        // once the package is installed and you can actually read that file).
    }
}
