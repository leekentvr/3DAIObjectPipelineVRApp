using System;
using TMPro;
using UnityEngine;

namespace Interaction
{
    /// <summary>
    /// In-VR level-of-detail control. Tracks the most recently placed object and steps it
    /// through a ladder of LoD ratios (fraction of the full-res reconstruction) so you can
    /// compare detail vs framerate on the headset and settle on a good value.
    ///
    /// Wiring (in the Editor, once): assign _pointSelectController. Bind VR buttons'
    /// OnClick to the parameterless methods here -- Finer(), Coarser(), or the
    /// SetRatioNN() presets -- since Unity Button/Interaction-SDK events can call
    /// public void methods with no return value. Optionally bind a TMP_Text via
    /// OnStatusChanged to show the current level.
    ///
    /// The tuner operates on the LAST placed object. Each swap replaces that object's root,
    /// so the tuner follows the new root automatically.
    /// </summary>
    public class LodTuner : MonoBehaviour
    {
        [Tooltip("Source of newly placed objects. The tuner controls whichever object was " +
                 "placed most recently.")]
        [SerializeField] private PointSelectController _pointSelectController;

        [Tooltip("LoD ratios to step through (fraction of the full-res face count), coarsest " +
                 "to finest is however you order them. 1 = full detail.")]
        [SerializeField] private float[] _ratioLadder = { 1f, 0.5f, 0.25f, 0.1f, 0.05f };

        [Tooltip("Ladder index used for the FIRST explicit LoD request on a freshly placed " +
                 "object (before that it shows the server's default decimation).")]
        [SerializeField] private int _startIndex = 1; // 0.5 by default

        [Tooltip("Optional TMP text that shows the current LoD status, e.g. \"LoD 0.5 (2/5)\". " +
                 "Assign a world-space TMP_Text on your panel and it updates automatically.")]
        [SerializeField] private TMP_Text _statusLabel;

        /// <summary>Human-readable status, e.g. \"LoD 0.5 (2/5)\" -- also written to
        /// _statusLabel if assigned. Subscribe here for any additional UI.</summary>
        public event Action<string> OnStatusChanged;

        private PlacedObjectLod _current;
        private int _ladderIndex;

        private void OnEnable()
        {
            if (_pointSelectController != null)
                _pointSelectController.OnObjectPlaced += HandleObjectPlaced;
            Report("LoD: place an object, then use finer / coarser");
        }

        private void OnDisable()
        {
            if (_pointSelectController != null)
                _pointSelectController.OnObjectPlaced -= HandleObjectPlaced;
        }

        private void HandleObjectPlaced(GameObject placed)
        {
            _current = placed != null ? placed.GetComponent<PlacedObjectLod>() : null;
            _ladderIndex = Mathf.Clamp(_startIndex, 0, Mathf.Max(0, _ratioLadder.Length - 1));
            Report(_current == null
                ? "LoD: (server has no LoD endpoint for this object)"
                : "LoD: default -- press finer/coarser to change");
        }

        /// <summary>Step one level finer (more detail).</summary>
        public async void Finer()
        {
            if (!Ready()) return;
            _ladderIndex = Mathf.Min(_ladderIndex + IndexDirTowardFiner(), _ratioLadder.Length - 1);
            _ladderIndex = Mathf.Max(_ladderIndex, 0);
            await ApplyCurrentLadder();
        }

        /// <summary>Step one level coarser (less detail, faster).</summary>
        public async void Coarser()
        {
            if (!Ready()) return;
            _ladderIndex = Mathf.Max(_ladderIndex - IndexDirTowardFiner(), 0);
            _ladderIndex = Mathf.Min(_ladderIndex, _ratioLadder.Length - 1);
            await ApplyCurrentLadder();
        }

        // Convenience presets for direct button binding.
        public async void SetRatio100() => await SetRatio(1f);
        public async void SetRatio50() => await SetRatio(0.5f);
        public async void SetRatio25() => await SetRatio(0.25f);
        public async void SetRatio10() => await SetRatio(0.1f);

        /// <summary>Request an arbitrary ratio (0-1) on the current object.</summary>
        public async System.Threading.Tasks.Task SetRatio(float ratio)
        {
            if (!Ready()) return;
            await Swap(Mathf.Clamp(ratio, 0.001f, 1f));
        }

        private async System.Threading.Tasks.Task ApplyCurrentLadder()
        {
            if (_ratioLadder == null || _ratioLadder.Length == 0) return;
            await Swap(_ratioLadder[_ladderIndex]);
        }

        private async System.Threading.Tasks.Task Swap(float ratio)
        {
            Report($"LoD: loading {ratio:0.###} ...");
            GameObject newRoot = await _current.SetLodAsync(ratio);
            if (newRoot != null)
            {
                _current = newRoot.GetComponent<PlacedObjectLod>();
                Report($"LoD {ratio:0.###}" +
                       (_ratioLadder != null && _ratioLadder.Length > 0
                            ? $" ({_ladderIndex + 1}/{_ratioLadder.Length})" : ""));
            }
            else
            {
                Report($"LoD: request failed (kept current)");
            }
        }

        // "Finer" = MORE detail = a HIGHER ratio (more faces). The ladder may be ordered
        // either way, so return the index step that moves toward the larger ratio.
        private int IndexDirTowardFiner()
        {
            if (_ratioLadder == null || _ratioLadder.Length < 2) return 1;
            return _ratioLadder[1] > _ratioLadder[0] ? 1 : -1;
        }

        private bool Ready()
        {
            // Fall back to any adjustable object in the scene if we never caught a placement
            // event (e.g. this tuner was enabled after the object was placed). If nothing
            // has a PlacedObjectLod, the server almost certainly isn't returning lod_url yet
            // -- see class notes / logcat guidance.
            if (_current == null)
            {
                _current = FindAnyObjectByType<PlacedObjectLod>();
            }
            if (_current == null)
            {
                Report("LoD: no adjustable object (place one, and ensure the server exposes lod_url)");
                return false;
            }
            if (_current.IsBusy)
            {
                Report("LoD: still loading previous level");
                return false;
            }
            return true;
        }

        private void Report(string msg)
        {
            Debug.Log($"[LoD] {msg}");
            if (_statusLabel != null) _statusLabel.text = msg;
            OnStatusChanged?.Invoke(msg);
        }
    }
}
