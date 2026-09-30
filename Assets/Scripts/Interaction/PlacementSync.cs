using Pipeline;
using UnityEngine;

namespace Interaction
{
    /// <summary>
    /// Persists a placed object's world transform to the server so a whole room can be
    /// reloaded later with everything back where it was put. Attached by PointSelectController
    /// to each placed object with its server identity (project + index).
    ///
    /// Saves once on configure (the initial placement), and again whenever the object is
    /// hand-adjusted -- it listens to GrabbableReconstruction.OnComparisonRecorded (fired on
    /// grab release) so the FINAL, human-aligned pose is what gets stored.
    ///
    /// IMPORTANT: the saved transform is in the headset's tracking space. It only lines up
    /// across sessions if a shared spatial anchor / consistent origin is used (Meta Spatial
    /// Anchors). Within a session it's exact; across a Guardian/space reset it will drift.
    /// </summary>
    public class PlacementSync : MonoBehaviour
    {
        private PipelineApiClient _client;
        private string _project;
        private int _index;
        private GrabbableReconstruction _grab;

        public void Configure(PipelineApiClient client, string project, int index)
        {
            _client = client;
            _project = project;
            _index = index;

            _grab = GetComponent<GrabbableReconstruction>();
            if (_grab != null) _grab.OnComparisonRecorded += HandleAdjusted;

            Save(); // initial placement
        }

        private void OnDestroy()
        {
            if (_grab != null) _grab.OnComparisonRecorded -= HandleAdjusted;
        }

        private void HandleAdjusted(GrabbableReconstruction.PoseComparison _) => Save();

        /// <summary>Push the current world transform to the server (fire-and-forget).</summary>
        public async void Save()
        {
            if (_client == null || string.IsNullOrEmpty(_project)) return;
            try
            {
                await _client.SavePlacementAsync(
                    _project, _index, transform.position, transform.rotation, transform.lossyScale);
            }
            catch (System.Exception e)
            {
                // Never let placement persistence break the interaction flow.
                Debug.LogWarning($"[Placement] Failed to save placement for {_project}#{_index}: {e.Message}");
            }
        }
    }
}
