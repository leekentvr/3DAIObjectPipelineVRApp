using Newtonsoft.Json;

namespace Pipeline
{
    /// <summary>
    /// Real camera intrinsics for the frame that was uploaded. Per docs/pipeline-api.md,
    /// the server requires all four fields together or none at all -- there is no
    /// partial-intrinsics mode. Get these from the Passthrough Camera API's
    /// PassthroughCameraAccess.Intrinsics (FocalLength / PrincipalPoint), NOT invented
    /// or hardcoded; the server's fallback default "almost certainly does not match
    /// your camera" per the API doc.
    /// </summary>
    [System.Serializable]
    public struct CameraIntrinsics
    {
        public float fx;
        public float fy;
        public float cx;
        public float cy;

        public CameraIntrinsics(float fx, float fy, float cx, float cy)
        {
            this.fx = fx;
            this.fy = fy;
            this.cx = cx;
            this.cy = cy;
        }
    }

    [JsonObject(MemberSerialization.OptIn)]
    public class CreateProjectResponse
    {
        [JsonProperty("project_id")] public string ProjectId;
    }

    /// <summary>Body for POST /api/projects/{project}/select.</summary>
    [JsonObject(MemberSerialization.OptIn)]
    public class SelectRequest
    {
        [JsonProperty("point")] public float[] Point; // [x, y] pixel coords in input.png

        [JsonProperty("box_half_size", NullValueHandling = NullValueHandling.Ignore)]
        public float? BoxHalfSize;

        // fx/fy/cx/cy: all four or none. Flattened manually (rather than nesting a
        // CameraIntrinsics object) because that's the exact shape the server expects.
        [JsonProperty("fx", NullValueHandling = NullValueHandling.Ignore)] public float? Fx;
        [JsonProperty("fy", NullValueHandling = NullValueHandling.Ignore)] public float? Fy;
        [JsonProperty("cx", NullValueHandling = NullValueHandling.Ignore)] public float? Cx;
        [JsonProperty("cy", NullValueHandling = NullValueHandling.Ignore)] public float? Cy;

        public static SelectRequest Create(float pointX, float pointY, float? boxHalfSize, CameraIntrinsics? intrinsics)
        {
            var req = new SelectRequest
            {
                Point = new[] { pointX, pointY },
                BoxHalfSize = boxHalfSize
            };
            if (intrinsics.HasValue)
            {
                req.Fx = intrinsics.Value.fx;
                req.Fy = intrinsics.Value.fy;
                req.Cx = intrinsics.Value.cx;
                req.Cy = intrinsics.Value.cy;
            }
            return req;
        }
    }

    [JsonObject(MemberSerialization.OptIn)]
    public class JobHandle
    {
        [JsonProperty("job_id")] public string JobId;
    }

    /// <summary>Response of GET /api/jobs/{job_id}.</summary>
    [JsonObject(MemberSerialization.OptIn)]
    public class JobStatusResponse
    {
        [JsonProperty("job_id")] public string JobId;
        [JsonProperty("project")] public string Project;
        [JsonProperty("kind")] public string Kind; // vision | sam3 | sam3d | process | select
        [JsonProperty("status")] public string Status; // queued | running | done | error
        [JsonProperty("stage")] public string Stage; // sub-stage for process/select, else null
        [JsonProperty("error")] public string Error;
        [JsonProperty("created_at")] public string CreatedAt;
        [JsonProperty("started_at")] public string StartedAt;
        [JsonProperty("finished_at")] public string FinishedAt;

        public bool IsDone => Status == "done";
        public bool IsError => Status == "error";
        public bool IsFinished => IsDone || IsError;
    }

    [JsonObject(MemberSerialization.OptIn)]
    public class ObjectEntry
    {
        [JsonProperty("index")] public int Index;
        [JsonProperty("description")] public string Description; // may be null
        [JsonProperty("mesh_url")] public string MeshUrl; // relative, e.g. /project/<id>/meshes/transformedobject_0.glb
        [JsonProperty("mask_url")] public string MaskUrl;
        // Full-res LoD source + the on-demand LoD endpoint base (append ?ratio=0-1).
        // Null on servers/projects that predate LoD support -- callers must null-check.
        [JsonProperty("full_mesh_url")] public string FullMeshUrl;
        [JsonProperty("lod_url")] public string LodUrl;
        // Owning project (per-object, since a room aggregates many projects) and the
        // saved world placement, if any. Null when unknown/unsaved -- null-check both.
        [JsonProperty("project")] public string Project;
        [JsonProperty("placement")] public Placement Placement;
    }

    /// <summary>A saved world-space placement (Unity coords) for an object -- where it was
    /// put in the real room. Round-trips with the server's placement_{i}.json.</summary>
    [JsonObject(MemberSerialization.OptIn)]
    public class Placement
    {
        [JsonProperty("position")] public float[] Position;   // x, y, z
        [JsonProperty("rotation")] public float[] Rotation;   // x, y, z, w (quaternion)
        [JsonProperty("scale")] public float[] Scale;         // x, y, z

        public bool IsValid =>
            Position != null && Position.Length == 3 &&
            Rotation != null && Rotation.Length == 4 &&
            Scale != null && Scale.Length == 3;
    }

    /// <summary>Response of GET /api/rooms/{room}/objects -- every object across all of a
    /// room's captures.</summary>
    [JsonObject(MemberSerialization.OptIn)]
    public class RoomObjectsResponse
    {
        [JsonProperty("room")] public string Room;
        [JsonProperty("project_count")] public int ProjectCount;
        [JsonProperty("object_count")] public int ObjectCount;
        [JsonProperty("objects")] public ObjectEntry[] Objects;
    }

    /// <summary>Response of GET /api/projects/{project}/objects.</summary>
    [JsonObject(MemberSerialization.OptIn)]
    public class ObjectsResponse
    {
        [JsonProperty("project_id")] public string ProjectId;
        [JsonProperty("object_count")] public int ObjectCount;
        [JsonProperty("objects")] public ObjectEntry[] Objects;
    }
}
