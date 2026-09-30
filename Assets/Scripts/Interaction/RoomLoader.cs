using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Pipeline;
using UnityEngine;

namespace Interaction
{
    /// <summary>
    /// Loads every reconstructed object for a whole "room" back into the scene at once.
    ///
    /// A room is just the base project name (prefix grouping on the server): each capture
    /// is stored as &lt;room&gt;_&lt;timestamp&gt;, and GET /api/rooms/&lt;room&gt;/objects returns them all.
    /// For each object this downloads its mesh (optionally at a chosen LoD ratio) and
    /// instantiates it at its saved placement -- so a room comes back laid out as it was
    /// built. Objects with no saved placement are laid out in a fallback grid so they're at
    /// least visible.
    ///
    /// Wiring: put this on any GameObject, set _room (or leave it to match your capture base
    /// name), assign _meshInstantiator, and bind a VR button's OnClick to LoadRoom().
    ///
    /// Placements are saved relative to the MRUK room (see RoomFrame), so they realign after a
    /// Guardian/space reset provided the same room is loaded. Older placements saved in raw
    /// tracking space still load, but only line up if the origin hasn't moved.
    /// MRUK must have loaded the scene before LoadRoom() is called.
    /// </summary>
    public class RoomLoader : MonoBehaviour
    {
        [Tooltip("Room to load = the base project/capture name (e.g. \"ToyodaLab\"). Captures " +
                 "are grouped server-side by this prefix.")]
        [SerializeField] private string _room = "ToyodaLab";

        [SerializeField] private MeshInstantiator _meshInstantiator;

        [Tooltip("Optional LoD ratio (0-1 of full-res) to load every object at. <=0 or >=1 " +
                 "uses the server's default decimated mesh.")]
        [SerializeField, Range(0f, 1f)] private float _lodRatio = 0f;

        [Tooltip("Make loaded objects grabbable so you can re-align them.")]
        [SerializeField] private bool _makeGrabbable = true;

        [Tooltip("Destroy previously room-loaded objects before loading again.")]
        [SerializeField] private bool _clearBeforeLoad = true;

        [Header("Fallback layout (objects with no saved placement)")]
        [SerializeField] private Transform _fallbackOrigin;
        [SerializeField] private float _fallbackSpacing = 0.6f;
        [SerializeField] private int _fallbackColumns = 5;

        private PipelineApiClient _client;
        private readonly List<GameObject> _loaded = new List<GameObject>();

        private PipelineApiClient Client => _client ??= new PipelineApiClient();

        public string Room { get => _room; set => _room = value; }

        /// <summary>Button-friendly entry point: loads the currently configured room.</summary>
        public async void LoadRoom() => await LoadRoomAsync(_room);

        /// <summary>Loads all objects for <paramref name="room"/>. Returns the number placed.</summary>
        public async Task<int> LoadRoomAsync(string room, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(room))
            {
                Debug.LogWarning("[Room] No room name set.");
                return 0;
            }

            if (_clearBeforeLoad) ClearLoaded();

            RoomObjectsResponse resp;
            try
            {
                resp = await Client.GetRoomObjectsAsync(room, ct);
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[Room] Failed to list objects for room '{room}': {e.Message}");
                return 0;
            }

            if (resp?.Objects == null || resp.Objects.Length == 0)
            {
                Debug.Log($"[Room] No objects found for room '{room}'.");
                return 0;
            }

            Debug.Log($"[Room] Loading {resp.Objects.Length} object(s) from {resp.ProjectCount} capture(s) in '{room}'...");

            int placed = 0;
            int fallbackCount = 0;
            foreach (ObjectEntry obj in resp.Objects)
            {
                if (ct.IsCancellationRequested) break;
                try
                {
                    byte[] glb = await DownloadMesh(obj, ct);
                    if (glb == null || glb.Length == 0) continue;

                    GetPose(obj, fallbackCount, out Vector3 pos, out Quaternion rot, out Vector3 scale,
                            out bool usedFallback);
                    if (usedFallback) fallbackCount++;

                    string label = string.IsNullOrEmpty(obj.Description) ? $"{obj.Project}#{obj.Index}" : obj.Description;
                    GameObject go = await _meshInstantiator.InstantiateAtAsync(
                        glb, pos, rot, scale, label, _makeGrabbable);
                    if (go != null)
                    {
                        _loaded.Add(go);
                        placed++;
                    }
                }
                catch (System.Exception e)
                {
                    Debug.LogWarning($"[Room] Skipped {obj.Project}#{obj.Index}: {e.Message}");
                }
            }

            Debug.Log($"[Room] Placed {placed}/{resp.Objects.Length} object(s) for '{room}' " +
                      $"({fallbackCount} without saved placement used the fallback grid).");
            return placed;
        }

        /// <summary>Destroys everything this loader has placed.</summary>
        public void ClearLoaded()
        {
            foreach (GameObject go in _loaded)
                if (go != null) Destroy(go);
            _loaded.Clear();
        }

        private async Task<byte[]> DownloadMesh(ObjectEntry obj, CancellationToken ct)
        {
            bool wantLod = _lodRatio > 0f && _lodRatio < 1f && !string.IsNullOrEmpty(obj.Project);
            if (wantLod)
                return await Client.DownloadObjectLodAsync(obj.Project, obj.Index, _lodRatio, ct);
            return await Client.DownloadBytesAsync(obj.MeshUrl, ct);
        }

        private void GetPose(ObjectEntry obj, int fallbackIndex,
                             out Vector3 pos, out Quaternion rot, out Vector3 scale, out bool usedFallback)
        {
            Placement p = obj.Placement;
            if (p != null && p.IsValid)
            {
                pos = new Vector3(p.Position[0], p.Position[1], p.Position[2]);
                rot = new Quaternion(p.Rotation[0], p.Rotation[1], p.Rotation[2], p.Rotation[3]);
                scale = new Vector3(p.Scale[0], p.Scale[1], p.Scale[2]);
                usedFallback = false;

                if (p.Frame == RoomFrame.FrameId)
                {
                    if (RoomFrame.TryGetCurrent(out Transform frame, out string uuid) && uuid == p.RoomUuid)
                        RoomFrame.ToWorld(frame, pos, rot, out pos, out rot);
                    else
                        Debug.LogWarning($"[Room] {obj.Project}#{obj.Index} was saved in MRUK room " +
                                         $"{p.RoomUuid}, which isn't the current room -- placed at its raw pose.");
                }
                return;
            }

            // No saved placement: lay out in a grid so it's at least visible.
            Vector3 origin = _fallbackOrigin != null ? _fallbackOrigin.position : transform.position;
            int col = fallbackIndex % Mathf.Max(1, _fallbackColumns);
            int row = fallbackIndex / Mathf.Max(1, _fallbackColumns);
            pos = origin + new Vector3(col * _fallbackSpacing, 0f, row * _fallbackSpacing);
            rot = Quaternion.identity;
            scale = Vector3.one;
            usedFallback = true;
        }
    }
}
