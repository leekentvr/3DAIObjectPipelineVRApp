using UnityEngine;

namespace Interaction
{
    /// <summary>
    /// Expresses placements relative to the calibrated ORIGIN (see OriginCalibration) instead of raw
    /// tracking space. The origin is re-established each session by standing at the same physical
    /// spot and heading, so a pose stored relative to it lines up again even though the tracking
    /// origin differs between sessions, after a recenter, or after the headset changes hands.
    /// Saved placements carry FrameId; without an origin set, callers use the raw world pose.
    /// </summary>
    public static class RoomFrame
    {
        public const string FrameId = "calib_origin";

        // Kept so saved placements can carry an id; the origin is a single frame per session.
        private const string OriginId = "origin";

        /// <summary>The calibrated origin frame, if the origin has been set this session.</summary>
        public static bool TryGetCurrent(out Transform frame, out string uuid)
        {
            frame = null;
            uuid = null;
            OriginCalibration oc = OriginCalibration.Instance;
            if (oc == null || !oc.IsCalibrated || oc.Frame == null) return false;
            frame = oc.Frame;
            uuid = OriginId;
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
