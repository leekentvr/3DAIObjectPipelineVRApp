using Interaction;
using TMPro;
using UnityEngine;

namespace UI
{
    /// <summary>
    /// Shows PointSelectController's live status on a TMP text -- most importantly the
    /// "PENDING" state after an image has been sent for processing, during which new
    /// selections are locked (PointSelectController rejects them via its _busy guard;
    /// this UI just makes that lock visible instead of silent).
    ///
    /// Wiring: put this anywhere in the scene (e.g. on the status text's GameObject),
    /// assign _controller (the PointSelectController) and _statusText (a TMP_Text).
    /// Use a DEDICATED text element, not PipelineConfigPanel's status text -- both
    /// would overwrite each other mid-run.
    /// </summary>
    public class PointSelectStatusUI : MonoBehaviour
    {
        [SerializeField] private PointSelectController _controller;
        [SerializeField] private TMP_Text _statusText;

        [Header("Colors")]
        [SerializeField] private Color _idleColor = Color.white;
        [SerializeField] private Color _pendingColor = new Color(1f, 0.75f, 0.2f); // amber
        [SerializeField] private Color _doneColor = new Color(0.4f, 1f, 0.4f);

        private void Reset()
        {
            _controller = FindAnyObjectByType<PointSelectController>();
            _statusText = GetComponent<TMP_Text>();
        }

        private void OnEnable()
        {
            if (_controller == null)
            {
                Debug.LogWarning("[PointSelectStatusUI] No PointSelectController assigned -- status will not update.");
                return;
            }

            _controller.OnStatusChanged += HandleStatus;
            _controller.OnBusyChanged += HandleBusyChanged;
            _controller.OnObjectPlaced += HandleObjectPlaced;

            Show(_controller.IsBusy ? "PENDING -- processing..." : "Ready. Point and pull the trigger to select an object.",
                 _controller.IsBusy ? _pendingColor : _idleColor);
        }

        private void OnDisable()
        {
            if (_controller == null) return;
            _controller.OnStatusChanged -= HandleStatus;
            _controller.OnBusyChanged -= HandleBusyChanged;
            _controller.OnObjectPlaced -= HandleObjectPlaced;
        }

        private void HandleStatus(string message)
        {
            // While a job is in flight, keep the PENDING prefix so the locked state
            // stays obvious no matter which step message just arrived.
            if (_controller.IsBusy)
            {
                Show($"PENDING -- {message}", _pendingColor);
            }
            else
            {
                Show(message, _idleColor);
            }
        }

        private void HandleBusyChanged(bool busy)
        {
            if (!busy)
            {
                // Job finished (success or failure) -- the final OnStatusChanged message
                // already said which; just restore idle color and append readiness.
                Show($"{CurrentText()}\nReady for next selection.", _idleColor);
            }
        }

        private void HandleObjectPlaced(GameObject placed)
        {
            Show($"Done -- placed '{placed.name}'.", _doneColor);
        }

        private string CurrentText() => _statusText != null ? _statusText.text : string.Empty;

        private void Show(string message, Color color)
        {
            if (_statusText == null) return;
            _statusText.text = message;
            _statusText.color = color;
        }
    }
}
