# Rotation, rendering & colour: findings and fixes

Investigation across both repos (`3DAIObjectPipeline` server side and the `VR23D`
Unity app) plus the upstream `facebookresearch/sam-3d-objects` issues/paper. Two
concrete server-side rotation bugs were found and fixed; the Quest rendering and
washed-out colour issues are diagnosed with fixes below.

## 1. Where rotation errors are induced

Rotation passes through five stages. Each can corrupt it:

1. **SAM3D monocular pose prediction.** From a single image the model predicts
   `instance_quaternion_l2c` ("local to camera") in PyTorch3D camera convention
   (+X left, +Y up, +Z into the scene, right-handed). For a single image this is
   fundamentally a guess — the upstream repo has three open, unanswered issues
   about it ([#71](https://github.com/facebookresearch/sam-3d-objects/issues/71),
   [#56](https://github.com/facebookresearch/sam-3d-objects/issues/56),
   [#78](https://github.com/facebookresearch/sam-3d-objects/issues/78)).

2. **Depth → pointmap conversion (BUG, fixed).** `run_sam3d.py` back-projected
   `depth.png` in raw OpenCV/image convention (+X right, +Y down, +Z forward) and
   passed it straight to the model, which expects the pointmap in PyTorch3D
   convention. Meta's own `notebook/demo_aligned_pointmap.ipynb` does
   `np.stack([-X, -Y, Z])` ("Convert to right-handed PyTorch3D coordinates"); the
   pipeline omitted the `-X, -Y`. A missing X/Y flip is a 180° rotation about Z:
   it leaves depth-derived scale/translation about right but **systematically
   corrupts rotation** — precisely the symptom in upstream issue #78. **This is
   the most likely primary cause of "rotation is all wrong" when depth is
   uploaded.**

3. **Pose → mesh vertex bake (BUG, fixed).** `transform_like_notebook` applied the
   pose to Y-up glTF vertices using a 90°-about-X matrix mislabeled "Y-up/Z-up",
   reverse-engineered from one cached object. The correct PyTorch3D-camera → glTF
   conversion (both right-handed, +Y up) is negate X and Z (a proper 180°-about-Y
   rotation, self-inverse). Applied in both `transform_like_notebook` and
   `make_scene_untextured_mesh`.

4. **glTFast import into Unity.** glTFast converts right-handed glTF to Unity's
   left-handed space automatically; it's reliable and not the suspected culprit,
   but it's the reason you can't eyeball the raw quaternion.

5. **Unity placement override.** `PointSelectController` originally discarded the
   baked rotation entirely and yawed the object to face the camera. Now
   toggleable via `_trustBakedRotation` (default true).

These stack. Fixing #2 and #3 (both done, server side) should be tested before
touching anything else, with `_trustBakedRotation = true` on the Unity side.

## 2. What Meta / the community are doing

Nothing merged yet. The three issues above have no maintainer fix. But Meta's
own design (per the SAM 3D paper, arXiv:2511.16624) already contains the real
answer, and it's **disabled in this pipeline**:

- **Render-and-compare "layout post-optimization".** The model's pose is only a
  *proposal*; Meta then renders the object under that pose, compares mask/pixels
  against the input, and back-propagates to refine R/t/s, accepting the result
  only if mask IoU improves. Code is present
  (`run_post_optimization` / `run_post_optimization_GS` in
  `inference_pipeline_pointmap.py`) but gated behind `with_layout_postprocess`,
  which `notebook/inference.py` hard-codes to **False**. So the single mechanism
  Meta built to make pose accurate is switched off here.
- **Pointmap initialization** (the depth path) is the other half — and it was
  being fed a mirrored pointmap (bug #2).

Recommendation: after the two axis fixes, try enabling `with_layout_postprocess=True`
in `notebook/inference.py`'s `__call__`. Caveats: it needs correct intrinsics,
it's much slower (render-and-compare per object), and it needs either the GS or
mesh output present. This is the highest-leverage accuracy improvement available
without training anything.

## 3. First-principles depth-based pose (if the above still isn't enough)

You already capture depth + intrinsics on the Quest, so you can recover pose
independently of SAM3D's guess:

1. **Position** — already solved the robust way: `EnvironmentRaycast` hit point
   from Unity's own tracking (`PointSelectController` anchors to it). Keep this.
2. **Scale** — back-project the object's masked depth pixels to a metric point
   cloud (you have fx/fy/cx/cy). The cloud's real-world bounding-box extent gives
   metric scale directly; divide by the mesh's canonical extent for the factor.
   This is far more reliable than SAM3D's monocular scale.
3. **Rotation** — two practical options:
   - *Gravity + surface align:* take the support plane normal from the depth
     cloud (RANSAC plane fit, or reuse the `EnvironmentRaycast` surface normal)
     as "up", and the horizontal direction from camera-to-object as yaw. Good
     enough for objects that rest on surfaces (most of them).
   - *ICP:* coarse-align the reconstructed mesh to the masked depth point cloud
     (open3d point-to-plane ICP), initialized from the plane-align guess. This is
     essentially a local version of what Meta's render-and-compare does, but
     geometric and CPU-cheap.

   The masked depth cloud is the key asset — it's real metric geometry of the
   exact object, so any of these beats a single-image rotation prediction.

## 4. Quest 3 rendering difficulty

Triangle count is **not** the bottleneck: `run_sam3d.py` decimates the per-object
`transformedobject_i.glb` (the file Unity loads) to a default `--target-faces 3000`,
and the pipeline-controller doesn't override it. 3000 triangles is trivial for
Quest 3 — further decimation gives diminishing returns and costs visual quality.

**Verified against the project — already correct, NOT the problem:**

- **Multiview stereo is on.** OpenXR `m_renderMode: 1` (Single Pass Instanced)
  for both build targets; `m_StereoRenderingPath: 2` (Instanced).
- **Vulkan is the primary Android API** (GLES3 fallback).
- **The Mobile URP asset is the one active on Quest** (Android default quality
  level 0 → `Mobile_RPAsset`): HDR off, RenderScale 0.8, MSAA 4x, LDR grading —
  all sane for Quest.
- **Fallback material is correctly assigned** — `MeshInstantiator._fallbackMaterial`
  → `Assets/Shaders/VertexColourMaterial.mat` → the `VR23D/VertexColorUnlit`
  shader. So the shader is NOT stripped from the build; shader stripping is ruled
  out.
- **Main-light shadows are effectively free here** — the RP asset enables them,
  but there are zero realtime Light components in the scene and the meshes render
  unlit, so there's nothing to cast/sample.

**The real remaining levers:**

1. **No cap on placed objects (most likely cause of session-long slowdown).**
   `PointSelectController` creates a new root GameObject on every `/select` and
   never destroys previous ones (`OnObjectPlaced` only notifies the status UI).
   They accumulate unbounded; combined with the shader's `Cull Off` (double-sided
   overdraw) this degrades framerate the longer the app runs. Fix: cap to N
   objects (destroy oldest) or replace-on-select.
2. **Instantiation hitches vs. steady framerate.** glTFast instantiate + GPU
   upload happens at placement; it's async but large meshes can still stall for a
   frame or two. Confirm whether the badness is a one-time hitch at placement or
   sustained low framerate before optimizing anything geometric.
3. **`Cull Off` overdraw.** Fine for one object; switch to `Cull Back` once
   winding is confirmed consistent if you place many.

## 5. Washed-out colour

Root cause identified: the project is in **Linear** colour space
(`ProjectSettings m_ActiveColorSpace = 1`), and SAM3D's vertex colours are sRGB
display values baked into glTF `COLOR_0` — which glTF declares as *linear*, so
glTFast uploads them unconverted. The camera then applies linear→sRGB on output,
brightening an already-sRGB value once too often: the pale, low-contrast look.

Fix applied in `Assets/Shaders/VertexColorUnlit.shader`: a `_VertexColorIsSRGB`
toggle (default on) that does `SRGBToLinear()` on the vertex colour in the
fragment shader, so the output encode cancels back to the true colour. If after
assigning the material the objects look too *dark* instead, glTFast was already
converting — turn the toggle off. (Requires assigning the material per §4.1.)

## Files changed

- `sam-3d-objects/notebook/run_sam3d.py` — pointmap X/Y negation (§1.2) and
  PyTorch3D→glTF export conversion (§1.3). Pushed to branch
  `fix/rotation-pytorch3d-to-gltf`.
- `Assets/Shaders/VertexColorUnlit.shader` — sRGB→linear vertex-colour toggle (§5).
- `Assets/Scripts/Interaction/PointSelectController.cs` — `_trustBakedRotation`
  toggle (from earlier).

## Suggested test order

1. Pull the branch on the server; re-run a `/select` **with depth + real Quest
   intrinsics** on an asymmetric object (mug, shoe, arrow).
2. Unity: assign the VertexColorUnlit material to `_fallbackMaterial`; keep
   `_trustBakedRotation = true`.
3. Check rotation first (should be close now), then colour (toggle sRGB if
   needed), then framerate.
4. If rotation is close but not exact, tune `MeshInstantiator._axisCorrectionEulerDegrees`.
5. If still materially off, enable `with_layout_postprocess=True` (§2).
