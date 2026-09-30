using UnityEngine;

namespace Interaction
{
    /// <summary>
    /// The missing input binding for PointSelectController.OnPointerSelect(Ray) --
    /// without this (or something like it), nothing in the project ever calls that
    /// method at all (confirmed by grepping the whole project for call sites: none
    /// existed before this file).
    ///
    /// Polls the controller trigger every frame and, on press, builds a world-space ray
    /// from _rayOrigin's position/forward and forwards it to
    /// PointSelectController.OnPointerSelect(). _rayOrigin should be the same
    /// controller transform your RayInteractor/RayInteractorRayVisual are already using
    /// for the visible pointing ray, so "what you see is what you select" -- e.g. the
    /// right controller anchor under the Camera Rig.
    ///
    /// Doesn't duplicate PointSelectController's own "already processing" guard --
    /// that's handled there (see _busy in PointSelectController.OnPointerSelect).
    /// </summary>
    public class ControllerSelectTrigger : MonoBehaviour
    {
        [Tooltip("The PointSelectController to drive.")]
        [SerializeField] private PointSelectController _pointSelectController;

        [Tooltip("Where the selection ray originates -- use the same controller " +
                 "transform your visible ray/RayInteractor points from, so the ray you " +
                 "select with matches the ray you see.")]
        [SerializeField] private Transform _rayOrigin;

        [Tooltip("Which controller's trigger fires a selection.")]
        [SerializeField] private OVRInput.Controller _controller = OVRInput.Controller.RTouch;

        [Tooltip("Which button counts as \"select\" -- defaults to the index trigger.")]
        [SerializeField] private OVRInput.Button _selectButton = OVRInput.Button.PrimaryIndexTrigger;

        [Tooltip("When the ray is over UI (tracked by UiHoverTracker), ignore the trigger " +
                 "so clicking a panel button doesn't also start a capture+upload. Requires " +
                 "a UiHoverTracker on the panel's background/buttons.")]
        [SerializeField] private bool _ignorePressesOverUi = true;

        [Header("Select feedback")]
        [Tooltip("Played when a selection fires, so the press is obviously registered.")]
        [SerializeField] private AudioSource _feedbackAudio;
        [SerializeField] private AudioClip _selectClip;
        [SerializeField] private bool _hapticsOnSelect = true;
        [SerializeField, Range(0f, 1f)] private float _hapticAmplitude = 0.5f;
        [SerializeField] private float _hapticSeconds = 0.08f;

        private void Reset()
        {
            _pointSelectController = FindAnyObjectByType<PointSelectController>();
        }

        private void Update()
        {
            if (_pointSelectController == null || _rayOrigin == null)
            {
                return;
            }

            if (OVRInput.GetDown(_selectButton, _controller))
            {
                // Don't turn a UI click into a world selection. Without this, pressing the
                // trigger to click Save/Test on the config panel also fires OnPointerSelect,
                // which POSTs a new capture to the server.
                if (_ignorePressesOverUi && UiHoverTracker.IsPointerOverUi)
                {
                    return;
                }

                var ray = new Ray(_rayOrigin.position, _rayOrigin.forward);
                PlaySelectFeedback();
                _pointSelectController.OnPointerSelect(ray);
            }
        }

        private void PlaySelectFeedback()
        {
            if (_feedbackAudio != null && _selectClip != null)
            {
                _feedbackAudio.PlayOneShot(_selectClip);
            }

            if (_hapticsOnSelect)
            {
                OVRInput.SetControllerVibration(1f, _hapticAmplitude, _controller);
                CancelInvoke(nameof(StopHaptics));
                Invoke(nameof(StopHaptics), _hapticSeconds);
            }
        }

        private void StopHaptics()
        {
            OVRInput.SetControllerVibration(0f, 0f, _controller);
        }
    }
}
