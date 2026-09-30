using Meta.XR;
using UnityEngine;

namespace Interaction
{
    /// <summary>
    /// Draws a persistent, always-visible laser from a controller -- unlike the Quest OS's
    /// own system pointer (which only renders automatically over PointableCanvas-registered
    /// UI surfaces), this is needed for the app's actual purpose: showing the user where
    /// they're pointing at REAL objects via passthrough, not just UI. Confirmed by searching
    /// the whole project: there was no RayInteractor, RayInteractorRayVisual, or
    /// LineRenderer anywhere before this script -- the only ray ever visible was the OS's,
    /// which is why it only appeared over the config panel.
    ///
    /// If _raycastManager is assigned, the ray terminates exactly at whatever real-world
    /// surface EnvironmentRaycastManager.Raycast() hits -- the same raycast
    /// PointSelectController uses for selection, so "what you see is what you'll select".
    /// Falls back to a fixed _maxLength line into empty space if nothing is hit (unscanned
    /// area, out of range, etc.) so the ray is still visible rather than disappearing.
    /// </summary>
    [RequireComponent(typeof(LineRenderer))]
    public class ControllerRayVisual : MonoBehaviour
    {
        [Tooltip("Ray origin -- use the same controller transform ControllerSelectTrigger " +
                 "uses, so the visible ray matches what actually gets selected.")]
        [SerializeField] private Transform _rayOrigin;

        [Tooltip("Optional -- if assigned, the ray terminates at the real hit point " +
                 "instead of always drawing a fixed-length line into empty space.")]
        [SerializeField] private EnvironmentRaycastManager _raycastManager;

        [SerializeField] private float _maxLength = 5f;
        [SerializeField] private float _width = 0.004f;
        [SerializeField] private Color _color = new Color(0.2f, 0.9f, 1f, 0.9f);

        [Tooltip("Hide this custom ray while the pointer is over UI (tracked by " +
                 "UiHoverTracker), so Meta's own UI ray is the only one shown over panels " +
                 "-- avoids two rays at once. Needs a UiHoverTracker on the panel.")]
        [SerializeField] private bool _hideOverUi = true;

        [Tooltip("How often (seconds) to re-query EnvironmentRaycastManager for where the " +
                 "ray hits a real surface. This was previously called every single frame, " +
                 "which was expensive enough (a real-time depth query, at full VR frame " +
                 "rate) to make the whole app stutter -- confirmed as the cause of reported " +
                 "'shaky/unusable' after adding this visual. The ray's origin/direction " +
                 "still update every frame for smooth tracking; only the hit-point sampling " +
                 "is throttled, since real-world surfaces don't move between samples.")]
        [SerializeField] private float _raycastIntervalSeconds = 0.1f;

        private LineRenderer _line;
        private float _cachedHitDistance = -1f;
        private float _nextRaycastTime;

        private void Awake()
        {
            _line = GetComponent<LineRenderer>();
            _line.positionCount = 2;
            _line.startWidth = _width;
            _line.endWidth = _width;
            _line.useWorldSpace = true;

            if (_line.sharedMaterial == null)
            {
                // Sprites/Default is a simple unlit shader available in every built-in-RP
                // project by default -- just needs a flat, always-visible line, no lighting.
                var shader = Shader.Find("Sprites/Default");
                if (shader != null)
                {
                    _line.sharedMaterial = new Material(shader);
                }
            }

            _line.startColor = _color;
            _line.endColor = _color;
        }

        private void Update()
        {
            if (_rayOrigin == null)
            {
                _line.enabled = false;
                return;
            }

            // Over UI, defer to Meta's own ray (which terminates neatly on the panel) so
            // there aren't two rays at once.
            if (_hideOverUi && UiHoverTracker.IsPointerOverUi)
            {
                _line.enabled = false;
                return;
            }

            _line.enabled = true;
            Vector3 start = _rayOrigin.position;
            Vector3 direction = _rayOrigin.forward;

            if (_raycastManager != null && Time.time >= _nextRaycastTime)
            {
                _nextRaycastTime = Time.time + _raycastIntervalSeconds;
                var ray = new Ray(start, direction);
                // EnvironmentRaycastHit has no distance field -- only point/normal/status --
                // so compute it ourselves from the hit point.
                _cachedHitDistance = _raycastManager.Raycast(ray, out EnvironmentRaycastHit hit)
                    ? Vector3.Distance(start, hit.point)
                    : -1f;
            }

            Vector3 end = _cachedHitDistance >= 0f
                ? start + direction * _cachedHitDistance
                : start + direction * _maxLength;

            _line.SetPosition(0, start);
            _line.SetPosition(1, end);
        }
    }
}
