using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Pipeline;
using UnityEngine;

namespace Interaction
{
    /// <summary>
    /// Loads reconstructed objects for a whole "room" back into the scene.
    ///
    /// A room is just the base project name (prefix grouping on the server): each capture
    /// is stored as &lt;room&gt;_&lt;timestamp&gt;, and GET /api/rooms/&lt;room&gt;/objects returns them all.
    /// For each object this downloads its mesh and instantiates it at its saved placement, so a
    /// room comes back laid out as it was built. Objects with no saved placement are laid out in
    /// a fallback grid so they're at least visible.
    ///
    /// Safety for the headset (loading many test captures at once has crashed it):
    ///  - everything loads at a moderate level of detail first (_startLodRatio, default 0.5);
    ///    step it up or down afterwards with the LoD buttons (LodTuner),
    ///  - at most _maxObjects are loaded (the newest ones),
    ///  - only one load runs at a time (a second press is ignored),
    ///  - objects are created one at a time with a frame yielded between them, and destroyed
    ///    objects release their glTFast meshes/textures.
    ///
    /// Wiring: put this on any GameObject, set _room, assign _meshInstantiator, and bind a VR
    /// button's OnClick to LoadRoom().
    ///
    /// Placements are saved relative to the MRUK room (see RoomFrame), so they realign after a
    /// Guardian/space reset provided the same room is loaded. Older placements saved in raw
    /// tracking space still load, but only line up if the origin hasn't moved.
    /// MRUK must have loaded the scene before LoadRoom() is called.
    /// </summary>
    public class RoomLoader : MonoBehaviour
    {
        [Tooltip("Room to load = the base project/capture name (e.g. \"ToyodaLab2\"). Captures " +
                 "are grouped server-side by this prefix. Editable in VR from the control panel.")]
        [SerializeField] private string _room = "ToyodaLab";

        [SerializeField] private MeshInstantiator _meshInstantiator;

        [Tooltip("Legacy override: if > 0, load every object at this LoD ratio instead of " +
                 "_startLodRatio.")]
        [SerializeField, Range(0f, 1f)] private float _lodRatio = 0f;

        [Tooltip("LoD ratio (fraction of the full-res face count, 1 = full detail) everything is " +
                 "loaded at first. Defaults to the middle of the LoD ladder; step it up or down " +
                 "afterwards with the LoD buttons.")]
        [SerializeField, Range(0.001f, 1f)] private float _startLodRatio = 0.5f;

        [Tooltip("Maximum objects to load (the newest ones win). 0 = unlimited. Protects the " +
                 "headset from a room with a lot of old test captures.")]
        [SerializeField] private int _maxObjects = 20;

        [Tooltip("Make loaded objects grabbable so you can re-align them.")]
        [SerializeField] private bool _makeGrabbable = true;

        [Tooltip("Destroy previously room-loaded objects before loading again.")]
        [SerializeField] private bool _clearBeforeLoad = true;

        [Header("Fallback layout (objects with no saved placement)")]
        [SerializeField] private Transform _fallbackOrigin;
        [SerializeField] private float _fallbackSpacing = 0.6f;
        [SerializeField] private int _fallbackColumns = 5;

        private PipelineApiClient _client;
        private bool _loading;

        private PipelineApiClient Client => _client ??= new PipelineApiClient();

        public string Room { get => _room; set => _room = value; }

        /// <summary>The LoD ratio the next room load starts at.</summary>
        public float StartLodRatio => _lodRatio > 0f ? _lodRatio : _startLodRatio;

        public bool IsLoading => _loading;

        /// <summary>Short progress/status text ("Loading 3/12 ...", "Loaded 12 objects").</summary>
        public event Action<string> OnStatusChanged;

        /// <summary>Fired when a load finishes, with the number of objects placed.</summary>
        public event Action<int> OnRoomLoaded;

        /// <summary>Button-friendly entry point: loads the currently configured room.</summary>
        public async void LoadRoom() => await LoadRoomAsync(_room);

        /// <summary>Loads all objects for <paramref name="room"/>. Returns the number placed.</summary>
        public async Task<int> LoadRoomAsync(string room, CancellationToken ct = default)
        {
            if (_loading)
            {
                Report("Already loading -- please wait.");
                return 0;
            }
            if (string.IsNullOrWhiteSpace(room))
            {
                Report("No room name set.");
                return 0;
            }

            _loading = true;
            try
            {
                return await LoadInternal(room.Trim(), ct);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                Report($"Load failed: {e.Message}");
                return 0;
            }
            finally
            {
                _loading = false;
            }
        }

        private async Task<int> LoadInternal(string room, CancellationToken ct)
        {
            if (_clearBeforeLoad) ClearLoaded();

            Report($"Fetching '{room}' ...");
            RoomObjectsResponse resp;
            try
            {
                resp = await Client.GetRoomObjectsAsync(room, ct);
            }
            catch (Exception e)
            {
                Debug.LogError($"[Room] Failed to list objects for room '{room}': {e.Message}");
                Report($"Couldn't reach the server for '{room}'.");
                return 0;
            }

            if (resp?.Objects == null || resp.Objects.Length == 0)
            {
                Report($"No objects found for room '{room}'.");
                OnRoomLoaded?.Invoke(0);
                return 0;
            }

            // Newest captures are last (server sorts by project name = timestamp). Keep the tail.
            ObjectEntry[] objects = resp.Objects;
            int skipped = 0;
            if (_maxObjects > 0 && objects.Length > _maxObjects)
            {
                skipped = objects.Length - _maxObjects;
                var tail = new ObjectEntry[_maxObjects];
                Array.Copy(objects, skipped, tail, 0, _maxObjects);
                objects = tail;
            }

            // Placements saved relative to the MRUK room can only be restored once the headset has
            // loaded that room scan; give it a few seconds instead of dropping objects at raw poses.
            if (AnyRoomRelative(objects) && !RoomFrame.TryGetCurrent(out _, out _))
            {
                Report("Waiting for the room scan ...");
                for (float t = 0f; t < 12f && !RoomFrame.TryGetCurrent(out _, out _); t += 0.25f)
                    await Task.Delay(250);
                if (!RoomFrame.TryGetCurrent(out _, out _))
                    Report("Room scan not found -- placing at raw positions (may be misaligned).");
            }

            float ratio = StartLodRatio;
            Debug.Log($"[Room] Loading {objects.Length} of {resp.Objects.Length} object(s) from " +
                      $"{resp.ProjectCount} capture(s) in '{room}' at LoD {ratio:0.###} ...");

            int placed = 0;
            int fallbackCount = 0;
            for (int i = 0; i < objects.Length; i++)
            {
                if (ct.IsCancellationRequested) break;
                ObjectEntry obj = objects[i];
                Report($"Loading {i + 1}/{objects.Length} ...");
                try
                {
                    byte[] glb = await DownloadMesh(obj, ratio, ct);
                    if (glb == null || glb.Length == 0) continue;

                    GetPose(obj, fallbackCount, out Vector3 pos, out Quaternion rot, out Vector3 scale,
                            out bool usedFallback);
                    if (usedFallback) fallbackCount++;

                    string label = string.IsNullOrEmpty(obj.Description) ? $"{obj.Project}#{obj.Index}" : obj.Description;
                    GameObject go = await _meshInstantiator.InstantiateAtAsync(
                        glb, pos, rot, scale, label, _makeGrabbable);
                    if (go != null)
                    {
                        PlacedObjectsRoot.Adopt(go.transform);
                        // Makes the object LoD-tunable and lets a later reload clear it.
                        var lod = go.AddComponent<PlacedObjectLod>();
                        lod.Configure(Client, _meshInstantiator, obj.Project, obj.Index, label,
                                      _makeGrabbable, ratio);
                        lod.IsRoomLoaded = true;

                        // Persist hand adjustments, but only for objects that already have a
                        // saved placement -- and don't overwrite it just by loading.
                        if (!usedFallback)
                            go.AddComponent<PlacementSync>().Configure(Client, obj.Project, obj.Index, saveNow: false);
                        placed++;
                    }
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[Room] Skipped {obj.Project}#{obj.Index}: {e.Message}");
                }

                await Task.Yield(); // let a frame render between objects
            }

            string summary = $"Loaded {placed} object(s) at LoD {ratio:0.###}";
            if (skipped > 0) summary += $" (skipped {skipped} older, limit {_maxObjects})";
            Report(summary);
            Debug.Log($"[Room] {summary}; {fallbackCount} without saved placement used the fallback grid.");
            OnRoomLoaded?.Invoke(placed);
            return placed;
        }

        private static bool AnyRoomRelative(ObjectEntry[] objects)
        {
            foreach (ObjectEntry o in objects)
                if (o.Placement != null && o.Placement.Frame == RoomFrame.FrameId) return true;
            return false;
        }

        /// <summary>Destroys every object a room load created (including LoD-swapped ones).</summary>
        public void ClearLoaded()
        {
            var toDestroy = new List<PlacedObjectLod>();
            foreach (PlacedObjectLod lod in PlacedObjectLod.All)
                if (lod != null && lod.IsRoomLoaded) toDestroy.Add(lod);
            foreach (PlacedObjectLod lod in toDestroy)
                Destroy(lod.gameObject);
            if (toDestroy.Count > 0) Resources.UnloadUnusedAssets();
        }

        private async Task<byte[]> DownloadMesh(ObjectEntry obj, float ratio, CancellationToken ct)
        {
            // Always via the LoD endpoint: that's the only way to get the coarse starting mesh.
            if (!string.IsNullOrEmpty(obj.Project) && ratio > 0f && ratio < 1f)
                return await Client.DownloadObjectLodAsync(obj.Project, obj.Index, ratio, ct);
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

        private void Report(string message)
        {
            Debug.Log($"[Room] {message}");
            OnStatusChanged?.Invoke(message);
        }
    }
}
