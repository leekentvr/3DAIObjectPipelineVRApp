using System;
using Interaction;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace UI
{
    /// <summary>
    /// Drives the "processing" UI from a PointSelectController: while a capture is being
    /// processed it locks (disables + dims) whatever panels you list, shows a processing
    /// overlay with a spinning icon, the current stage, and the elapsed mm:ss timer, and
    /// color-codes the state (working / done / error). Purely presentational -- it only
    /// reads the controller's events, it never starts or cancels work.
    ///
    /// Wiring: assign _controller, the CanvasGroup(s) to lock (e.g. the config panel), and
    /// the overlay pieces. All overlay refs are optional -- leave any unassigned and that
    /// bit is simply skipped, so you can start minimal and add polish later.
    /// </summary>
    public class PipelineStatusHud : MonoBehaviour
    {
        [Header("Source")]
        [SerializeField] private PointSelectController _controller;

        [Header("Busy lock")]
        [Tooltip("CanvasGroups made non-interactable and dimmed while a capture is " +
                 "processing -- e.g. the config panel, so buttons can't be used mid-run.")]
        [SerializeField] private CanvasGroup[] _lockWhileBusy;
        [SerializeField, Range(0.1f, 1f)] private float _lockedAlpha = 0.4f;

        [Header("Processing overlay (all optional)")]
        [Tooltip("Root object shown only while processing.")]
        [SerializeField] private GameObject _processingRoot;
        [SerializeField] private TMP_Text _statusText;
        [SerializeField] private TMP_Text _timerText;
        [Tooltip("Icon rotated while busy to read as a spinner.")]
        [SerializeField] private Image _spinner;
        [SerializeField] private float _spinnerDegPerSecond = 180f;
        [Tooltip("Optional graphic (bar/dot) tinted by state.")]
        [SerializeField] private Graphic _statusAccent;

        [Header("State colors")]
        [SerializeField] private Color _workingColor = new Color(0.20f, 0.55f, 0.95f);
        [SerializeField] private Color _doneColor = new Color(0.25f, 0.72f, 0.35f);
        [SerializeField] private Color _errorColor = new Color(0.90f, 0.30f, 0.28f);

        private bool _busy;

        private void OnEnable()
        {
            if (_controller == null)
            {
                Debug.LogWarning("[StatusHud] No PointSelectController assigned.");
                return;
            }
            _controller.OnBusyChanged += HandleBusy;
            _controller.OnStatusChanged += HandleStatus;
            _controller.OnProcessingTimeChanged += HandleTime;
            ApplyBusy(false);
        }

        private void OnDisable()
        {
            if (_controller == null) return;
            _controller.OnBusyChanged -= HandleBusy;
            _controller.OnStatusChanged -= HandleStatus;
            _controller.OnProcessingTimeChanged -= HandleTime;
        }

        private void Update()
        {
            if (_busy && _spinner != null)
            {
                _spinner.rectTransform.Rotate(0f, 0f, -_spinnerDegPerSecond * Time.unscaledDeltaTime);
            }
        }

        private void HandleBusy(bool busy)
        {
            _busy = busy;
            ApplyBusy(busy);
        }

        private void ApplyBusy(bool busy)
        {
            if (_processingRoot != null) _processingRoot.SetActive(busy);

            if (_lockWhileBusy != null)
            {
                foreach (CanvasGroup cg in _lockWhileBusy)
                {
                    if (cg == null) continue;
                    cg.interactable = !busy;
                    cg.blocksRaycasts = !busy;
                    cg.alpha = busy ? _lockedAlpha : 1f;
                }
            }

            if (!busy && _timerText != null) _timerText.text = string.Empty;
        }

        private void HandleStatus(string msg)
        {
            if (_statusText != null) _statusText.text = msg;
            if (_statusAccent == null || msg == null) return;

            bool isError = msg.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0
                        || msg.IndexOf("failed", StringComparison.OrdinalIgnoreCase) >= 0;
            bool isDone = msg.StartsWith("Done", StringComparison.OrdinalIgnoreCase);
            _statusAccent.color = isError ? _errorColor : isDone ? _doneColor : _workingColor;
        }

        private void HandleTime(TimeSpan t)
        {
            if (_timerText == null) return;
            _timerText.text = t == TimeSpan.Zero
                ? string.Empty
                : (t.TotalHours >= 1
                    ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
                    : $"{(int)t.TotalMinutes:00}:{t.Seconds:00}");
        }
    }
}
