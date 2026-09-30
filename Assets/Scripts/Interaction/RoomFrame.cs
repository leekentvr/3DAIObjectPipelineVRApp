using Meta.XR.MRUtilityKit;
using UnityEngine;

namespace Interaction
{
    /// <summary>
    /// Expresses placements relative to the MRUK room (scene anchor) instead of raw tracking
    /// space. The room anchor is persisted by the OS and re-localized every session, so a pose
    /// stored relative to it survives Guardian/space resets that move the tracking origin.
    /// Saved placements carry FrameId + the room's UUID; when the UUID doesn't match the room
    /// currently loaded, callers fall back to the raw world pose.
    /// </summary>
    public static class RoomFrame
    {
        public const string FrameId = "mruk_room";

        /// <summary>The room the headset is currently in, if MRUK has loaded one.</summary>
        public static bool TryGetCurrent(out Transform frame, out string uuid)
        {
            frame = null;
            uuid = null;
            if (MRUK.Instance == null) return false;
            MRUKRoom room = MRUK.Instance.GetCurrentRoom();
            if (room == null) return false;
            frame = room.transform;
            uuid = room.Anchor.Uuid.ToString();
            return true;
        }

        public static void ToLocal(Transform frame, Vector3 pos, Quaternion rot,
                                   out Vector3 localPos, out Quaternion localRot)
        {
            localPos = frame.InverseTransformPoint(pos);
            localRot = Quaternion.Inverse(frame.rotation) * rot;
        }

        public static void ToWorld(Transform frame, Vector3 localPos, Quaternion localRot,
                                   out Vector3 pos, out Quaternion rot)
        {
            pos = frame.TransformPoint(localPos);
            rot = frame.rotation * localRot;
        }
    }
}
