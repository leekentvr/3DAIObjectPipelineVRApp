using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using TMPro;
using UnityEngine;

namespace Interaction
{
    /// <summary>
    /// In-VR level-of-detail control. Steps objects through a ladder of LoD ratios (fraction of
    /// the full-res reconstruction) so you can compare detail vs framerate on the headset and
    /// settle on a good value.
    ///
    /// Scope: it acts on either the most recently placed object ("last") or on every LoD-tunable
    /// object in the scene ("all", applied one at a time). After a room load it switches to "all"
    /// (everything starts at the lowest LoD and you raise it together); after a new point-select
    /// placement it switches to "last". ToggleScope() flips it manually.
    ///
    /// Wiring (in the Editor, once): assign _pointSelectController (and optionally _roomLoader).
    /// Bind VR buttons' OnClick to Finer() / Coarser(). The ladder is sorted ascending internally,
    /// so its order in the Inspector doesn't matter.
    /// </summary>
    public class LodTuner : MonoBehaviour
    {
        [Tooltip("Source of newly placed objects.")]
        [SerializeField] private PointSelectController _pointSelectController;

        [Tooltip("Optional. Found automatically if left empty. After a room load the tuner " +
                 "starts from the loader's start ratio and switches to 'all objects'.")]
        [SerializeField] private RoomLoader _roomLoader;

        [Tooltip("LoD ratios to step through (fraction of the full-res face count). 1 = full detail.")]
        [SerializeField] private float[] _ratioLadder = { 0.05f, 0.2f, 0.5f, 0.75f, 1f };

        [Tooltip("Optional TMP text that shows the current LoD status, e.g. \"LoD 0.25 (4/7)\".")]
        [SerializeField] private TMP_Text _statusLabel;

        /// <summary>Human-readable status -- also written to _statusLabel if assigned.</summary>
        public event Action<string> OnStatusChanged;

        /// <summary>Fired when the scope (all vs last placed) changes.</summary>
        public event Action<bool> OnScopeChanged;

        private PlacedObjectLod _current;
        private float _currentRatio;   // last ratio applied; 0 = unknown / server default
        private bool _applyToAll;
        private bool _swapping;

        /// <summary>True = act on every tunable object; false = only the last placed one.</summary>
        public bool ApplyToAll => _applyToAll;

        private void OnEnable()
        {
            if (_pointSelectController != null)
                _pointSelectController.OnObjectPlaced += HandleObjectPlaced;
            if (_roomLoader == null) _roomLoader = FindAnyObjectByType<RoomLoader>();
            if (_roomLoader != null)
                _roomLoader.OnRoomLoaded += HandleRoomLoaded;
            Report("LoD: load a room or place an object");
        }

        private void OnDisable()
        {
            if (_pointSelectController != null)
                _pointSelectController.OnObjectPlaced -= HandleObjectPlaced;
            if (_roomLoader != null)
                _roomLoader.OnRoomLoaded -= HandleRoomLoaded;
        }

        private void HandleObjectPlaced(GameObject placed)
        {
            _current = placed != null ? placed.GetComponent<PlacedObjectLod>() : null;
            _currentRatio = MiddleRatio();
            SetScope(false);
            Report(_current == null
                ? "LoD: (no LoD endpoint for this object)"
                : "LoD: default -- use finer / coarser");
        }

        private void HandleRoomLoaded(int count)
        {
            if (count <= 0) return;
            _currentRatio = _roomLoader.StartLodRatio;
            SetScope(true);
            Report($"LoD {_currentRatio:0.###} -- all {count} objects");
        }

        /// <summary>Flip between "all objects" and "last placed object".</summary>
        public void ToggleScope()
        {
            SetScope(!_applyToAll);
            Report(_applyToAll ? "LoD: applies to ALL objects" : "LoD: applies to LAST placed object");
        }

        private void SetScope(bool all)
        {
            if (_applyToAll == all) return;
            _applyToAll = all;
            OnScopeChanged?.Invoke(all);
        }

        /// <summary>Step one level finer (more detail).</summary>
        public async void Finer()
        {
            float[] ladder = SortedLadder();
            float next = -1f;
            foreach (float r in ladder)
                if (r > _currentRatio + 1e-4f) { next = r; break; }
            if (next < 0f) { Report("LoD: already at full detail"); return; }
            await SetRatio(next);
        }

        /// <summary>Step one level coarser (less detail, faster).</summary>
        public async void Coarser()
        {
            float[] ladder = SortedLadder();
            float next = -1f;
            foreach (float r in ladder)
                if (r < _currentRatio - 1e-4f) next = r;
            if (next < 0f) { Report("LoD: already at the coarsest level"); return; }
            await SetRatio(next);
        }

        /// <summary>Request an arbitrary ratio (0-1) on the current scope.</summary>
        public async Task SetRatio(float ratio)
        {
            if (_swapping) { Report("LoD: still loading the previous level"); return; }
            ratio = Mathf.Clamp(ratio, 0.001f, 1f);

            var targets = new List<PlacedObjectLod>();
            if (_applyToAll)
            {
                foreach (PlacedObjectLod t in PlacedObjectLod.All)
                    if (t != null) targets.Add(t);
            }
            else
            {
                if (_current == null) _current = FindAnyObjectByType<PlacedObjectLod>();
                if (_current != null) targets.Add(_current);
            }
            if (targets.Count == 0)
            {
                Report("LoD: no adjustable object (load a room or place one)");
                return;
            }

            _swapping = true;
            try
            {
                int ok = 0;
                for (int i = 0; i < targets.Count; i++)
                {
                    PlacedObjectLod t = targets[i];
                    if (t == null || t.IsBusy) continue;
                    Report($"LoD: loading {ratio:0.###}  ({i + 1}/{targets.Count})");
                    GameObject newRoot = await t.SetLodAsync(ratio);
                    if (newRoot != null)
                    {
                        ok++;
                        if (!_applyToAll) _current = newRoot.GetComponent<PlacedObjectLod>();
                    }
                    await Task.Yield();
                }

                if (ok > 0) _currentRatio = ratio;
                Report(ok > 0
                    ? $"LoD {ratio:0.###}" + (targets.Count > 1 ? $"  ({ok}/{targets.Count})" : "")
                    : "LoD: request failed (kept current)");
            }
            finally
            {
                _swapping = false;
            }
        }

        // Convenience presets for direct button binding.
        public async void SetRatio100() => await SetRatio(1f);
        public async void SetRatio50() => await SetRatio(0.5f);
        public async void SetRatio25() => await SetRatio(0.25f);
        public async void SetRatio10() => await SetRatio(0.1f);

        private float[] SortedLadder()
        {
            var copy = (_ratioLadder != null && _ratioLadder.Length > 0)
                ? (float[])_ratioLadder.Clone()
                : new[] { 0.05f, 0.2f, 0.5f, 0.75f, 1f };
            Array.Sort(copy);
            return copy;
        }

        /// <summary>The middle rung of the ladder -- where a fresh object starts.</summary>
        private float MiddleRatio()
        {
            float[] ladder = SortedLadder();
            return ladder[ladder.Length / 2];
        }

        private void Report(string msg)
        {
            Debug.Log($"[LoD] {msg}");
            if (_statusLabel != null) _statusLabel.text = msg;
            OnStatusChanged?.Invoke(msg);
        }
    }
}
