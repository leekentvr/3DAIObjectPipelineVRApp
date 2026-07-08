using System;
using Pipeline;
using UnityEngine;

namespace Capture
{
    /// <summary>
    /// Everything captured at one instant: the RGB frame Unity will upload as
    /// input.png, the camera's real intrinsics for that frame (required for metric
    /// SAM3D reconstruction), and the camera's world pose at capture time (required to
    /// place the returned mesh correctly -- see "Object placement for Unity" in
    /// docs/pipeline-api.md and step 7 of CLAUDE.md's Flow).
    /// </summary>
    [Serializable]
    public struct CapturedFrame
    {
        public byte[] ColorPng;
        public int Width;
        public int Height;
        public CameraIntrinsics Intrinsics;
        public Vector3 CameraWorldPosition;
        public Quaternion CameraWorldRotation;

        /// <summary>PassthroughCameraAccess.Timestamp is a DateTime (verified against the
        /// installed com.meta.xr.mrutilitykit package source) -- not a float/double.</summary>
        public DateTime CaptureTimestamp;

        /// <summary>Optional -- null if depth wasn't captured (or capture failed).
        /// When present, guaranteed to be the same Width x Height as ColorPng so it's
        /// pixel-for-pixel aligned per the API doc's requirement.</summary>
        public byte[] DepthPng16;
    }
}
