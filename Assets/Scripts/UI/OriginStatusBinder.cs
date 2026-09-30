using Interaction;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace UI
{
    /// <summary>
    /// Panel controls for the manual origin (OriginCalibration): the "Set origin here" button and a
    /// status line that shows whether the origin is set (green) or needs setting (amber).
    /// </summary>
    public class OriginStatusBinder : MonoBehaviour
    {
        private static readonly Color Ok = new Color(0.35f, 0.9f, 0.6f, 1f);
        private static readonly Color Warn = new Color(1f, 0.75f, 0.25f, 1f);

        [SerializeField] private TMP_Text _status;
        [SerializeField] private Button _button;
        [SerializeField] private TMP_Text _buttonLabel;

        private OriginCalibration _origin;

        public void Configure(TMP_Text status, Button button, TMP_Text buttonLabel)
        {
            _status = status;
            _button = button;
            _buttonLabel = buttonLabel;
            if (Application.isPlaying && isActiveAndEnabled) { OnDisable(); OnEnable(); }
        }

        /// <summary>Wire to the button's OnClick.</summary>
        public void SetOriginPressed()
        {
            Debug.Log("[Origin] Set origin pressed.");
            Bind();
            if (_origin != null) _origin.CalibrateWithCountdown();
            else Debug.LogWarning("[Origin] No OriginCalibration available.");
        }

        private void OnEnable() => Bind();

        private void OnDisable()
        {
            if (_origin != null) _origin.OnStateChanged -= Refresh;
            _origin = null;
        }

        // Resolved lazily: the panel can initialise before OriginCalibration exists.
        private void Bind()
        {
            if (_origin != null) return;
            _origin = OriginCalibration.GetOrCreate();
            if (_origin == null) return;
            _origin.OnStateChanged += Refresh;
            Refresh(_origin.IsCalibrated, _origin.StatusMessage);
        }

        private void Refresh(bool calibrated, string message)
        {
            if (_status != null)
            {
                _status.text = calibrated ? message : $"{message}";
                _status.color = calibrated ? Ok : Warn;
            }
            if (_buttonLabel != null)
            {
                int s = _origin != null ? Mathf.RoundToInt(_origin.CountdownSeconds) : 5;
                _buttonLabel.text = (calibrated ? "Recalibrate" : "Set origin") + $"  ({s} s)";
            }
        }
    }
}
