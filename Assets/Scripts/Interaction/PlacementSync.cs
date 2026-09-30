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
    /// The transform is saved relative to the current MRUK room (see RoomFrame) so it lines up
    /// across sessions and tracking-origin resets. If no MRUK room is loaded it falls back to
    /// raw tracking space, which is only exact within a session.
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
                Vector3 pos = transform.position;
                Quaternion rot = transform.rotation;
                string frameId = null, roomUuid = null;
                // Store relative to the MRUK room when one is loaded, so the placement
                // survives a tracking-origin reset; otherwise raw world space.
                if (RoomFrame.TryGetCurrent(out Transform frame, out roomUuid))
                {
                    RoomFrame.ToLocal(frame, pos, rot, out pos, out rot);
                    frameId = RoomFrame.FrameId;
                }
                await _client.SavePlacementAsync(
                    _project, _index, pos, rot, transform.lossyScale, frameId, roomUuid);
            }
            catch (System.Exception e)
            {
                // Never let placement persistence break the interaction flow.
                Debug.LogWarning($"[Placement] Failed to save placement for {_project}#{_index}: {e.Message}");
            }
        }
    }
}
