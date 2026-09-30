using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Pipeline;
using UnityEngine;

namespace Interaction
{
    /// <summary>
    /// Remembers which server object a placed mesh came from (project + index) and can
    /// reload it at a different level of detail in place -- same world transform, so you
    /// can eyeball the detail/framerate tradeoff without moving or re-selecting anything.
    ///
    /// Attached by PointSelectController to each placed object. The actual mesh swap
    /// replaces the whole root (destroying the old one) via MeshInstantiator.InstantiateAtAsync
    /// at the captured transform, and re-attaches this component to the new root so it can
    /// be tuned again. Grab support is re-added by MeshInstantiator if it was on before.
    /// </summary>
    public class PlacedObjectLod : MonoBehaviour
    {
        private PipelineApiClient _client;
        private MeshInstantiator _meshInstantiator;
        private string _projectId;
        private int _index;
        private string _label;
        private bool _grabbable;
        private bool _busy;

        /// <summary>The LoD ratio (0-1 of full-res faces) currently displayed, or -1 if the
        /// object is showing the server's default decimation (its first-placed mesh).</summary>
        public float CurrentRatio { get; private set; } = -1f;

        public bool IsBusy => _busy;

        /// <summary>True for objects created by RoomLoader (so a reload can clear just those).
        /// Carried across LoD swaps.</summary>
        public bool IsRoomLoaded { get; set; }

        private static readonly List<PlacedObjectLod> s_all = new List<PlacedObjectLod>();

        /// <summary>Every live LoD-tunable object in the scene.</summary>
        public static IReadOnlyList<PlacedObjectLod> All => s_all;

        private void OnEnable()
        {
            if (!s_all.Contains(this)) s_all.Add(this);
        }

        private void OnDisable() => s_all.Remove(this);

        /// <summary>Fired after a successful swap with the new root and the ratio shown.</summary>
        public event Action<GameObject, float> OnLodChanged;

        public void Configure(PipelineApiClient client, MeshInstantiator meshInstantiator,
                              string projectId, int index, string label, bool grabbable,
                              float currentRatio = -1f)
        {
            _client = client;
            _meshInstantiator = meshInstantiator;
            _projectId = projectId;
            _index = index;
            _label = label;
            _grabbable = grabbable;
            CurrentRatio = currentRatio;
        }

        /// <summary>Reloads this object at <paramref name="ratio"/> (0-1 of the full-res
        /// face count) in place. Returns the new root (or null on failure). The old root --
        /// including this component -- is destroyed on success; use the returned object or
        /// the OnLodChanged event to keep a reference.</summary>
        public async Task<GameObject> SetLodAsync(float ratio, CancellationToken ct = default)
        {
            if (_busy)
            {
                Debug.LogWarning("[LoD] Busy with a previous swap; ignoring request.");
                return null;
            }
            if (_client == null || _meshInstantiator == null || string.IsNullOrEmpty(_projectId))
            {
                Debug.LogError("[LoD] Not configured; cannot change LoD.");
                return null;
            }

            _busy = true;
            try
            {
                // Capture the exact current placement so the reloaded mesh doesn't move.
                Vector3 pos = transform.position;
                Quaternion rot = transform.rotation;
                Vector3 scale = transform.localScale;

                Debug.Log($"[LoD] Requesting object {_index} at ratio {ratio:0.###} ...");
                byte[] glb = await _client.DownloadObjectLodAsync(_projectId, _index, ratio, ct);
                if (glb == null || glb.Length == 0)
                {
                    Debug.LogError("[LoD] Server returned no mesh; keeping current LoD.");
                    return null;
                }

                GameObject newRoot = await _meshInstantiator.InstantiateAtAsync(
                    glb, pos, rot, scale, _label, _grabbable);
                if (newRoot == null)
                {
                    Debug.LogError("[LoD] Failed to instantiate the new LoD mesh; keeping current.");
                    return null;
                }

                // Carry the LoD identity onto the new root so it can be tuned again.
                PlacedObjectsRoot.Adopt(newRoot.transform);
                var next = newRoot.GetComponent<PlacedObjectLod>();
                if (next == null) next = newRoot.AddComponent<PlacedObjectLod>();
                next.Configure(_client, _meshInstantiator, _projectId, _index, _label, _grabbable, ratio);
                next.IsRoomLoaded = IsRoomLoaded;

                // Keep persisting hand adjustments after a swap (without re-saving right now).
                if (GetComponent<PlacementSync>() != null && newRoot.GetComponent<PlacementSync>() == null)
                    newRoot.AddComponent<PlacementSync>().Configure(_client, _projectId, _index, saveNow: false);

                OnLodChanged?.Invoke(newRoot, ratio);
                Debug.Log($"[LoD] Object {_index} now showing ratio {ratio:0.###}.");

                Destroy(gameObject); // replace the old root
                return newRoot;
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (Exception e)
            {
                Debug.LogError($"[LoD] LoD change failed: {e.Message}");
                return null;
            }
            finally
            {
                _busy = false;
            }
        }
    }
}
