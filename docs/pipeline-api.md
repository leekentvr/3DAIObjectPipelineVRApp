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

Every `/api/*` request requires an `X-API-Token` header matching a shared-secret
token. The token is auto-generated on first server startup into
`pipeline-controller/.api_token` (gitignored — never commit it) and printed to the
server console; reused across restarts unless that file is deleted, or set explicitly
via the `PIPELINE_API_TOKEN` environment variable (e.g. to pin the same value the
Unity client is configured with). Requests without a valid token get `401`.

```bash
curl -H "X-API-Token: $(cat pipeline-controller/.api_token)" http://<host>:5000/api/projects/<id>/objects
```

**This auth is scoped to `/api/*` only.** The browser UI, Socket.IO events, and the
`/project/<path>` static file routes (which serve the raw photos/meshes — including
the URLs returned by `objects` below) remain unauthenticated, same as before. Anyone
on the network who knows or guesses a project name can still read that project's
files directly. Treat this as a trusted local/LAN setup only — do not expose this
port to the open internet.

Project names ARE validated server-side (`^[A-Za-z0-9_-]{1,100}$`) before touching
the filesystem or subprocess commands, so untrusted project name input can't do path
traversal or command injection.

## Async job model

Every stage-trigger endpoint (`vision`, `sam3`, `sam3d`, `process`, `select`) starts
work in a background thread and returns immediately with a `job_id`. Poll
[`GET /api/jobs/<job_id>`](#get-apijobsjob_id) for status. Pipeline stages are slow —
SAM3D reconstruction alone can take minutes *per object* — so don't block waiting for
the initial POST to return anything but the job_id.

Only one pipeline stage runs at a time, globally, across *all* projects — this
includes stages triggered from the browser UI. If a stage is already running, a new
job is created but immediately fails with `"Pipeline already running for this
project"` — the caller must wait and retry rather than assume queuing. A `.lock` left behind by a crashed server is cleared automatically at startup.

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

SAM3D's raw reconstruction is routinely 50k-1M+ triangles per object — far too heavy
for a VR headset to hold framerate with more than 1-2 objects loaded at once.
`transformedobject_{i}` (what Unity/VR actually load) is decimated to ~3,000 faces via
`run_sam3d.py --target-faces` (default `3000`) before export — 10-20 concurrent objects
(Quest 3's target) stays under ~60k total triangles. `pretransformobject_{i}` and
`scene_combined.glb` are left at full source resolution.

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

**The VR "point at an object" flow.** Runs three stages under one `job_id`:

1. `name` — draws a bright crosshair marker on the *full* scene image at the clicked
   pixel and asks Qwen what object is there (e.g. "black fabric keyboard"). Decoupled
   from segmentation entirely — it doesn't crop to any mask first, so it isn't
   affected by mask quality and gets full scene context.
2. `sam3_refine` — segments using that name as a genuine SAM3 *text* prompt (the same
   mode the full-scene `vision`→`sam3` flow uses), which finds the complete semantic
   object. This deliberately does NOT use SAM3's box-exemplar point mode — a box
   exemplar matches a visual patch, not "segment what's inside this box," so a tight
   box over part of a composite/textured object (e.g. a keyboard's keys) can
   under-segment, missing the rest of the object. Picks whichever candidate mask
   contains the clicked pixel.
3. `sam3d` — reconstructs from that mask.

**Request:** `application/json`
```json
{
  "point": [1120.0, 750.0],
  "fx": 591.0125, "fy": 590.16775, "cx": 322.525, "cy": 244.11084
}
```
`point` is required. The four intrinsics fields are optional, with the same
semantics/validation as the `sam3d` endpoint above.

**Response:** `202 {"job_id": "..."}` · `400` missing/malformed `point`, or intrinsics given partially · `404` project not found

Unlike other stages, naming and segmentation here have no fallback to degrade to — if
`name` can't identify anything, or `sam3_refine` finds no matching mask for that name,
the job fails outright (`status: "error"`) rather than running `sam3d` on nothing.

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
| `stage` | which sub-stage is currently running (`vision`, `sam3`, `name`, `sam3_refine`, or `sam3d` — `name`/`sam3_refine` only appear for `select` jobs), else `null` |
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
      "project": "living_room_01",
      "index": 0,
      "description": "beige fabric sofa",
      "mesh_url": "/project/living_room_01/meshes/transformedobject_0.glb",
      "full_mesh_url": "/project/living_room_01/meshes/transformedobject_0_full.glb",
      "lod_url": "/api/projects/living_room_01/objects/0/lod",
      "mask_url": "/project/living_room_01/masks/0.png",
      "placement": null
    }
  ]
}
```
| Field | Meaning |
|---|---|
| `mesh_url` | default decimated mesh (`--target-faces`, 3000 by default) |
| `full_mesh_url` | full-resolution mesh; may 404 for projects reconstructed before full-res saving |
| `lod_url` | base of the [LoD endpoint](#get-apiprojectsprojectobjectsindexlod) |
| `placement` | saved [placement](#getpost-apiprojectsprojectobjectsindexplacement), or `null` if never placed |

`description` comes from whichever ran for that object: the `select` flow's per-object
crosshair-marker naming (`object_names.json`, keyed by mesh index) takes priority when
present, falling back to the full-scene `vision`/Qwen listing (`objects.csv`, matched
positionally by index). `null` if neither is available for that index — e.g. a
`process`/`vision` object beyond how many descriptions Qwen actually returned. (A
`select`-flow object always has a name if it exists at all — see
[`select`](#post-apiprojectsprojectselect) above, naming isn't best-effort there.)

`404` if the project doesn't exist.

---

### `GET /api/projects/<project>/objects/<index>/lod`

On-demand level of detail: returns object `<index>`'s mesh decimated to `ratio` of its
full-resolution face count, without re-running reconstruction.

| Query | Description |
|---|---|
| `ratio` | float in `(0, 1]`, default `0.5`. `>= 0.999` returns the full-res mesh unchanged. |

**Response:** `200` a `.glb` (`model/gltf-binary`). Derived from
`transformedobject_<i>_full.glb`, falling back to the default decimated mesh for older
projects (LoD is then relative to that already-reduced mesh). Results are cached as
`transformedobject_<i>_lod<ratio>.glb`, so repeat requests are served from disk.
First request for a new ratio runs a decimation subprocess (up to 180 s timeout).

`400` invalid `project` or `ratio` · `404` project/object not found · `500` decimation
failed (`detail` has the tail of the subprocess output) · `504` timed out.

---

### `GET /api/rooms`

Best-effort list of rooms, derived by stripping a trailing `_<yyyyMMdd_HHmmss[_fff]>`
suffix from project directory names.

**Response:** `200`
```json
{ "rooms": [ { "room": "ToyodaLab", "capture_count": 14 } ] }
```

---

### `GET /api/rooms/<room>/objects`

Every object across all captures in a room: the project named `<room>` plus every
`<room>_<timestamp>` project (underscore delimiter, so `Lab` doesn't match `Lab2`).
Each entry has the same shape as in the per-project manifest, including its own
`project` (needed for LoD requests) and `placement`.

**Response:** `200`
```json
{ "room": "ToyodaLab", "project_count": 14, "object_count": 14, "objects": [ ... ] }
```

`400` invalid room name.

---

### `GET|POST /api/projects/<project>/objects/<index>/placement`

Persist / read where an object was placed in the real room, so a room reload can restore
it. Stored as `placement_<index>.json` in the project directory.

**POST** — `application/json`:
```json
{
  "position": [x, y, z],
  "rotation": [x, y, z, w],
  "scale": [x, y, z],
  "frame": "mruk_room",
  "room_uuid": "<MRUK room anchor UUID>"
}
```
`position`, `rotation` (quaternion) and `scale` are required numeric arrays. `frame` and
`room_uuid` are optional but must be given together (strings, max 64 chars). With
`frame: "mruk_room"`, position/rotation are **local to that MRUK room**, which the headset
persists, so they realign across sessions and tracking-origin resets. Without `frame` they
are raw Unity tracking-space coordinates and only line up within a session.

**Response:** `201` `{"ok": true, "placement": {...}}` · `400` invalid body · `404` project
not found.

**GET** — `200` the stored JSON, `404` if none saved, `500` if the file is corrupt.

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
TOKEN=$(cat pipeline-controller/.api_token)

# 1. Upload the passthrough screenshot (+ depth, for metric placement)
curl -X POST http://<host>:5000/api/projects -H "X-API-Token: $TOKEN" \
  -F "image=@screenshot.png" -F "depth=@depth.png" -F "project=living_room_01"

# 2. User points at an object; Unity computes the pixel + real camera intrinsics
curl -X POST http://<host>:5000/api/projects/living_room_01/select -H "X-API-Token: $TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"point": [1120, 750], "fx": 611.2, "fy": 611.2, "cx": 640.0, "cy": 480.0}'
# -> {"job_id": "74f336813a1f"}

# 3. Poll until done
curl http://<host>:5000/api/jobs/74f336813a1f -H "X-API-Token: $TOKEN"

# 4. Fetch the mesh
curl http://<host>:5000/api/projects/living_room_01/objects -H "X-API-Token: $TOKEN"
# -> download the mesh_url, e.g. /project/living_room_01/meshes/transformedobject_0.glb
```
