# Pipeline REST API

Reference for external clients (primarily the Unity VR app) driving the pipeline over
HTTP instead of the browser UI. Implemented in `webapp/app.py`, served alongside the
existing Socket.IO browser UI on the same Flask app.

```bash
cd pipeline-controller
conda activate pipeline
python webapp/app.py
```

Base URL: `http://<host>:5000` (binds `0.0.0.0`, so reachable from other devices on
the same network, e.g. a Quest headset — see [Security](#security) below).

## Security

There is no authentication. Anything on the same network as this machine can call the
API and trigger arbitrary pipeline runs. This is intended for a trusted local/LAN
setup only — do not expose this port to the open internet.

Project names ARE validated server-side (`^[A-Za-z0-9_-]{1,100}$`) before touching
the filesystem or subprocess commands, so untrusted project name input can't do path
traversal or command injection — but that's the only input validation in place.

## Async job model

Every stage-trigger endpoint (`vision`, `sam3`, `sam3d`, `process`, `select`) starts
work in a background thread and returns immediately with a `job_id`. Poll
[`GET /api/jobs/<job_id>`](#get-apijobsjob_id) for status. Pipeline stages are slow —
SAM3D reconstruction alone can take minutes *per object* — so don't block waiting for
the initial POST to return anything but the job_id.

Only one pipeline stage runs at a time, globally, across *all* projects — this
includes stages triggered from the browser UI. If a stage is already running, a new
job is created but immediately fails with `"Pipeline already running for this
project"` — the caller must wait and retry rather than assume queuing.

## Endpoints

### `POST /api/projects`

Create a project, or attach a new image to an existing one.

**Request:** `multipart/form-data`
| Field | Required | Description |
|---|---|---|
| `image` | yes, unless `project` already exists with an `input.png` | The source photo/screenshot. Saved as `input.png`. |
| `project` | no | Project name. Falls back to the image's filename stem if omitted. Must match `^[A-Za-z0-9_-]{1,100}$` after resolution. |
| `depth` | no | A depth map matching `image`, for depth-assisted SAM3D reconstruction (see [`sam3d`](#post-apiprojectsprojectsam3d)). Saved as `depth.png`. |

**Response:** `201`
```json
{"project_id": "living_room_01"}
```
`400` if neither a usable `project` name nor `image` filename can be determined, or if
`project` was given but the project doesn't already have an `input.png` and no `image`
was uploaded.

---

### `POST /api/projects/<project>/vision`

Runs Qwen2.5-VL to identify every object in the full scene, writing `objects.csv`.
Part of the "detect everything" flow — not used by the VR point-select flow.

**Response:** `202 {"job_id": "..."}` · `400` invalid project name

---

### `POST /api/projects/<project>/sam3`

Runs SAM3 segmentation. Two modes:

- **Text-loop (default, no body needed):** segments every object description in
  `objects.csv` (requires `vision` to have run first). Writes `masks/0.png`,
  `masks/1.png`, ... for every detected instance.
- **Point mode:** segments a single object at a pixel — the VR "point at an object"
  interaction. Writes exactly `masks/0.png`.

**Request (point mode only):** `application/json`
```json
{"point": [1120.0, 750.0], "box_half_size": 90.0}
```
| Field | Required | Description |
|---|---|---|
| `point` | to use point mode | `[x, y]` pixel coordinates in `input.png`, numeric. |
| `box_half_size` | no | Half-width/height in pixels of the exemplar box built around the point. Defaults to 4% of the shorter image dimension. |

SAM3's point/box prompt is a *visual exemplar* prompt — it finds every instance that
looks similar to what's under the box, not just the thing at the exact pixel (so two
identical chairs in frame can both come back as candidates). The server disambiguates
by picking whichever candidate's own mask actually contains the clicked pixel,
falling back to the highest-confidence candidate only if none do.

**Response:** `202 {"job_id": "..."}` · `400` invalid project name or malformed `point`/`box_half_size`

---

### `POST /api/projects/<project>/sam3d`

Runs SAM3D-Objects reconstruction on whatever's in `masks/`. Writes
`meshes/pretransformobject_{i}.glb/.ply` (local/canonical space) and
`meshes/transformedobject_{i}.glb/.ply` (world/pose-baked space — see
[Object placement](#object-placement-for-unity)), plus `out/scene_combined.glb/.ply`.

If the project has a `depth.png` (uploaded via `POST /api/projects`), the
reconstruction is depth-assisted. **Without real camera intrinsics for that depth
capture, the pointmap is not metrically correct** — pass them explicitly:

**Request (optional):** `application/json`
```json
{"fx": 591.0125, "fy": 590.16775, "cx": 322.525, "cy": 244.11084}
```
`fx`, `fy`, `cx`, `cy` must all be given together, or all omitted (in which case a
hardcoded default is used, which almost certainly does not match your camera).

**Response:** `202 {"job_id": "..."}` · `400` invalid project name, or intrinsics given partially/non-numeric

---

### `POST /api/projects/<project>/process`

Convenience endpoint for the "detect everything" flow: runs `vision` → `sam3`
(text-loop) → `sam3d` in sequence under one `job_id`. **Not recommended for scenes
with many objects** — SAM3D processes objects one at a time with no partial output
until every object is done, so a ~20-object room can take hours. See
[`select`](#post-apiprojectsprojectselect) for the VR flow instead.

**Response:** `202 {"job_id": "..."}` · `400` invalid project name

---

### `POST /api/projects/<project>/select`

**The VR "point at an object" flow.** Skips `vision` (Qwen) entirely. Runs `sam3` in
point mode, then `sam3d`, on just the one selected object — under one `job_id`.

**Request:** `application/json`
```json
{
  "point": [1120.0, 750.0],
  "box_half_size": 90.0,
  "fx": 591.0125, "fy": 590.16775, "cx": 322.525, "cy": 244.11084
}
```
`point` is required. `box_half_size` and the four intrinsics fields are optional, with
the same semantics/validation as the `sam3`/`sam3d` endpoints above.

**Response:** `202 {"job_id": "..."}` · `400` missing/malformed `point`, or intrinsics given partially · `404` project not found

---

### `GET /api/jobs/<job_id>`

Poll job status.

**Response:** `200`
```json
{
  "job_id": "74f336813a1f",
  "project": "living_room_01",
  "kind": "select",
  "status": "running",
  "stage": "sam3d",
  "error": null,
  "created_at": "2026-07-01T05:31:26.989976Z",
  "started_at": "2026-07-01T05:31:26.990218Z",
  "finished_at": null
}
```
| Field | Values |
|---|---|
| `kind` | `vision` \| `sam3` \| `sam3d` \| `process` \| `select` — `process`/`select` for multi-stage jobs |
| `status` | `queued` → `running` → `done` \| `error` |
| `stage` | which sub-stage is currently running (for `process`/`select`), else `null` |
| `error` | failure reason string, e.g. `"Stage 'sam3d' failed"` or `"Pipeline already running for this project"`, else `null` |

`404` if `job_id` doesn't exist (job IDs are in-memory only — lost on server restart).

---

### `GET /api/projects/<project>/objects`

Object manifest: description + mesh/mask URLs for everything currently reconstructed.
Reflects whatever mesh files exist on disk right now, regardless of job status — safe
to call while a job is still running (will just show fewer objects than the eventual
total).

**Response:** `200`
```json
{
  "project_id": "living_room_01",
  "object_count": 1,
  "objects": [
    {
      "index": 0,
      "description": "beige fabric sofa",
      "mesh_url": "/project/living_room_01/meshes/transformedobject_0.glb",
      "mask_url": "/project/living_room_01/masks/0.png"
    }
  ]
}
```
`description` is `null` if `objects.csv` doesn't exist or has fewer entries than the
mesh index (always the case for `select`-flow objects, since `vision`/Qwen never ran).

`404` if the project doesn't exist.

---

### `GET /project/<project>/meshes/<filename>` and `GET /project/<path>`

Static file serving for anything under a project's directory — the URLs returned by
`objects` above, plus `input.png`, `objects.csv`, `masks/<i>.png`, `out/*`, etc. No
`Content-Type` negotiation beyond what Flask infers from the extension.

## Object placement for Unity

`meshes/transformedobject_{i}.glb` has the object's reconstructed pose (rotation,
translation, scale) **baked directly into its vertices** — it is not a separate pose
you read from the API and apply yourself. Concretely:

- **Without depth** (no `depth.png`/intrinsics): SAM3D does monocular generative
  reconstruction. The baked pose is the model's best single-image guess — not
  metrically grounded to the real camera. Treat the mesh as shape-only; compute
  placement in Unity by other means (e.g. the pointing ray's hit distance).
- **With depth + correct intrinsics**: the pose is genuinely camera-relative (real
  scale, real distance from the camera that captured `input.png`/`depth.png`). If
  Unity knows that camera's world pose (position + rotation) at capture time — e.g.
  from Quest 3's Passthrough Camera API extrinsics — instantiating the mesh at that
  world transform should place it correctly in physical space.

Axis-convention correctness (glTF is Y-up/right-handed; Unity is Y-up/left-handed)
has not been empirically verified end-to-end in Unity — check this before trusting
placement, don't assume it's correct.

## Example: VR point-select flow

```bash
# 1. Upload the passthrough screenshot (+ depth, for metric placement)
curl -X POST http://<host>:5000/api/projects \
  -F "image=@screenshot.png" -F "depth=@depth.png" -F "project=living_room_01"

# 2. User points at an object; Unity computes the pixel + real camera intrinsics
curl -X POST http://<host>:5000/api/projects/living_room_01/select \
  -H "Content-Type: application/json" \
  -d '{"point": [1120, 750], "fx": 611.2, "fy": 611.2, "cx": 640.0, "cy": 480.0}'
# -> {"job_id": "74f336813a1f"}

# 3. Poll until done
curl http://<host>:5000/api/jobs/74f336813a1f

# 4. Fetch the mesh
curl http://<host>:5000/api/projects/living_room_01/objects
# -> download the mesh_url, e.g. /project/living_room_01/meshes/transformedobject_0.glb
```
