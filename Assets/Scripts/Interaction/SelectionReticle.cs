using UnityEngine;

// EnvironmentRaycastManager / EnvironmentRaycastHit live in Meta.XR (verified against
// com.meta.xr.mrutilitykit@201.0.0), same as the other scripts here.
using Meta.XR;

namespace Interaction
{
    /// <summary>
    /// Shows a small reticle on the real surface the controller ray is pointing at, using
    /// the same EnvironmentRaycast the selection flow uses -- so before pressing, the user
    /// sees exactly where (and whether) a selection will land. Hidden when the ray hits
    /// nothing scanned, when out of range, or while a capture is already processing.
    ///
    /// Assign the same _rayOrigin as ControllerSelectTrigger so the reticle matches the
    /// ray you select with, and an EnvironmentRaycastManager from the scene. _reticle is
    /// any small flat visual (a quad/ring sprite) that gets moved onto the surface.
    /// </summary>
    public class SelectionReticle : MonoBehaviour
    {
        [SerializeField] private EnvironmentRaycastManager _raycastManager;

        [Tooltip("Controller transform the ray comes from -- match ControllerSelectTrigger._rayOrigin.")]
        [SerializeField] private Transform _rayOrigin;

        [Tooltip("The reticle visual to place on the surface (a small quad/ring). Toggled on/off.")]
        [SerializeField] private Transform _reticle;

        [Tooltip("Optional: reticle is hidden while this controller is busy processing a selection.")]
        [SerializeField] private PointSelectController _controller;

        [SerializeField] private float _maxDistanceMeters = 8f;

        [Tooltip("Lift off the surface a touch to avoid z-fighting with the scene mesh.")]
        [SerializeField] private float _surfaceOffset = 0.01f;

        [Tooltip("Hide the reticle while the pointer is over UI (tracked by UiHoverTracker), " +
                 "so it doesn't sit on/behind panels.")]
        [SerializeField] private bool _hideOverUi = true;

        private void Update()
        {
            if (_reticle == null || _raycastManager == null || _rayOrigin == null)
            {
                return;
            }

            bool show = false;

            bool overUi = _hideOverUi && UiHoverTracker.IsPointerOverUi;
            bool busy = _controller != null && _controller.IsBusy;
            if (!overUi && !busy)
            {
                var ray = new Ray(_rayOrigin.position, _rayOrigin.forward);
                if (_raycastManager.Raycast(ray, out EnvironmentRaycastHit hit) &&
                    Vector3.Distance(ray.origin, hit.point) <= _maxDistanceMeters)
                {
                    _reticle.position = hit.point + hit.normal * _surfaceOffset;
                    // Lay the reticle flat against the surface (its local +Z faces out).
                    _reticle.rotation = Quaternion.LookRotation(-hit.normal);
                    show = true;
                }
            }

            if (_reticle.gameObject.activeSelf != show)
            {
                _reticle.gameObject.SetActive(show);
            }
        }
    }
}
