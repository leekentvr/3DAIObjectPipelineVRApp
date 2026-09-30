using UnityEngine;

namespace Interaction
{
    /// <summary>
    /// Parent for every placed / room-loaded object. It follows the MRUK room's pose (relative to
    /// where the room was when the first object was adopted), so objects stay glued to the REAL
    /// room when the headset recenters, its tracking origin moves, or the device is handed to
    /// someone else and the room is re-localized -- instead of staying at fixed Unity world
    /// coordinates that no longer match the room.
    ///
    /// Deliberately a separate object rather than a child of the MRUK room: MRUK may destroy and
    /// recreate its room GameObjects when it reloads the scene, which would destroy the objects.
    /// With no MRUK room available the root simply stays where it is.
    /// </summary>
    public class PlacedObjectsRoot : MonoBehaviour
    {
        private static PlacedObjectsRoot s_instance;

        private bool _bound;
        private string _boundRoomUuid;
        private Vector3 _bindPos;
        private Quaternion _bindRot;

        private static PlacedObjectsRoot Instance
        {
            get
            {
                if (s_instance == null)
                {
                    var go = new GameObject("PlacedObjectsRoot");
                    s_instance = go.AddComponent<PlacedObjectsRoot>();
                }
                return s_instance;
            }
        }

        /// <summary>Parents <paramref name="t"/> under the root, keeping its current world pose.</summary>
        public static void Adopt(Transform t)
        {
            if (t == null) return;
            t.SetParent(Instance.transform, worldPositionStays: true);
        }

        private void LateUpdate()
        {
            if (!RoomFrame.TryGetCurrent(out Transform frame, out string uuid)) return;

            if (!_bound || uuid != _boundRoomUuid)
            {
                // First sighting of this room: remember where it is now. Rebinding when the room
                // changes keeps existing objects where they are rather than teleporting them.
                _bound = true;
                _boundRoomUuid = uuid;
                _bindPos = frame.position;
                _bindRot = frame.rotation;
                transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                return;
            }

            // Root = (room now) * (room at bind)^-1
            Quaternion dq = frame.rotation * Quaternion.Inverse(_bindRot);
            Vector3 dp = frame.position - dq * _bindPos;
            transform.SetPositionAndRotation(dp, dq);
        }
    }
}
