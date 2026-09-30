# Unity Editor setup (manual steps)

The scripts under `Assets/Scripts` and the `Packages/manifest.json` entries in this
commit were written outside the Unity Editor (no display/Editor available in that
environment), so nothing has been compiled or run yet. `com.unity.cloud.gltfast` and
`com.unity.nuget.newtonsoft-json` will resolve automatically from Unity's package
registry next time the project is opened. Everything below needs a human at the
Unity Editor, because Meta's own packages are not resolvable by editing
`manifest.json` alone -- see why in step 1.

This project is pinned to **Unity 6000.0.58f1**. That's a deliberate choice, not the
default: Meta XR SDK 203.0.0 does not compile on Unity 6000.5.x (it calls
`Object.GetInstanceID()`, which 6.5 turned into a hard `CS0619` compile error in favor
of `GetEntityId()` -- Meta hasn't shipped a fix as of this writing) and has other
regressions on 6000.4.4+. **6000.0.58f1 + Meta XR SDK 203.0.0 is the confirmed-working
combination** -- don't upgrade the Editor version on this project without checking
Meta's compatibility notes first.

## 1. Install Meta XR Core SDK + MRUK

Meta distributes these via the Unity Asset Store or as manual tarball downloads, not
via a public scoped UPM registry -- there's no `manifest.json` edit that installs them
non-interactively.

1. Open this project in Unity **6000.0.58f1** (already set in ProjectSettings -- Hub
   should offer this version directly).
2. Unity Asset Store → sign in → install:
   - **Meta XR Core SDK** (203.0.0)
   - **Meta XR Mixed Reality Utility Kit (MRUK)** (203.0.0) -- required for the
     `PassthroughCameraAccess` and `EnvironmentRaycastManager` components the new
     scripts reference.
3. If prompted to update **OVRPlugin**, restart the Editor.
4. Menu **Meta → Tools → Project Setup Tool** -- fix every issue it flags (this
   configures Graphics API = Vulkan, Stereo Rendering = Multiview, Android manifest
   permissions, etc.). Also available via **Meta → Tools → Building Blocks**.

## 2. Enable passthrough + camera access in the scene

1. **Meta → Tools → Building Blocks** → add **Camera Rig** and **Passthrough**
   building blocks to your scene.
2. Select the **Camera Rig** object → on **OVR Manager**, enable **Enabled
   Passthrough Camera Access**. While you're on **OVR Manager**, also enable
   **Requires System Keyboard** -- without this, Quest never renders the OS
   keyboard overlay at all when a `TMP_InputField` is selected (no error, it just
   silently does nothing when clicked -- this is what made the config panel's
   host prefix/suffix fields untypeable until this was found). This is unrelated
   to any per-field script; it's a one-time, whole-app manifest declaration.
3. Add a **PassthroughCameraAccess** component to a GameObject in the scene (the
   Camera Rig is the usual place). Set `CameraPosition` (Left/Right) and
   `RequestedResolution` -- pick a concrete resolution (e.g. 1280x960), don't leave it
   at auto/largest (see "Incorrect Resolution Handling" in Meta's PCA docs).
4. Add an **EnvironmentRaycastManager** component (MRUK) -- used by
   `DepthFrameCapture` and `PointSelectController` for both the sparse depth grid and
   converting the user's pointing ray into a world hit point.
5. Confirm the Android manifest has `horizonos.permission.HEADSET_CAMERA` (Project
   Setup Tool / **Meta → Tools → Update AndroidManifest.xml** should add this
   automatically -- double check after any manifest regeneration).
6. **Add `PassthroughPermissionRequester` to a GameObject that's always active from
   startup** (the Camera Rig is fine). This is easy to miss and the most likely reason
   `GetTexture()` silently never returns real data: `PassthroughCameraAccess` only
   *checks* whether the camera permission is already granted -- it never actually
   triggers the Android permission dialog (confirmed by reading the installed package
   source). Declaring the permission in the manifest is necessary but not sufficient.
   Without something calling `OVRPermissionsRequester.Request(...)` at runtime, the
   permission is simply never granted, and the headset just shows whatever it would
   show anyway (default skybox/passthrough) as if nothing changed -- no crash, no
   obvious error, just silently no data. If you already had a permission request flow
   elsewhere (e.g. OVRManager's "Permission Requests On Startup"), don't request this
   permission from two places -- pick one.

   To confirm this was actually the problem on a build that still doesn't work, check
   `adb logcat -s Unity` for `"PassthroughCameraAccess doesn't have the required
   camera permission... Waiting for permission before enabling the camera..."` -- if
   you see that line repeating, the permission truly was never granted.

## 3. Wire up the new scripts

All under `Assets/Scripts/`:

| Script | Where to put it |
|---|---|
| `Capture/PassthroughPermissionRequester.cs` | On the Camera Rig (see step 2.6 above -- do this one first). |
| `Capture/PassthroughFrameCapture.cs` | On the Camera Rig (or anywhere); assign its `PassthroughCameraAccess` field. |
| `Capture/DepthFrameCapture.cs` | Same object; assign `PassthroughCameraAccess` + `EnvironmentRaycastManager`. |
| `Mesh/MeshInstantiator.cs` | Anywhere (no scene dependencies). |
| `Interaction/PointSelectController.cs` | Anywhere; assign all the above plus `EnvironmentRaycastManager`/`PassthroughCameraAccess`. |
| `Interaction/ControllerSelectTrigger.cs` | Anywhere; assign `_pointSelectController`, `_rayOrigin` (a controller transform -- ideally the same one your `RayInteractor` points from), `_controller` (which hand), `_selectButton` (defaults to the index trigger). This is the actual input binding that calls `OnPointerSelect(Ray)` -- confirmed by grepping the whole project that nothing called it before this script existed. |
| `Pipeline/*` | No scene wiring needed; `PipelineApiClient` reads `Pipeline/PipelineConfig` automatically. |

**Reference fields are easy to leave unassigned and get no error until runtime** (confirmed the hard way): `PassthroughFrameCapture._cameraAccess`, `DepthFrameCapture._cameraAccess`/`_raycastManager`, and `PointSelectController._cameraAccess`/`_raycastManager` all fail silently/gracefully with a one-line `Debug.LogError`/`LogWarning` if left as "None" rather than throwing -- easy to miss since nothing crashes, the flow just quietly does nothing. Double check all five are assigned, and that an `EnvironmentRaycastManager` component actually exists somewhere in the scene (it's not added by any Building Block -- has to be added manually, step 2.4 above).

**Verified against the actual installed package source** (com.meta.xr.mrutilitykit
201.0.0): `PassthroughCameraAccess` and `EnvironmentRaycastManager`/
`EnvironmentRaycastHit` live in namespace `Meta.XR`, not `Meta.XR.MRUtilityKit` as
first guessed from Meta's public docs (fixed in all three scripts that use them).
`PassthroughCameraAccess.Intrinsics.FocalLength`/`.PrincipalPoint` and
`EnvironmentRaycastManager.Raycast()`'s bool-return-means-status-is-Hit semantics
matched what was assumed. One real bug the guess got wrong:
`PassthroughCameraAccess.Timestamp` is a `DateTime`, not a `float`/`double` -- fixed by
changing `CapturedFrame.TimestampSeconds` to `CapturedFrame.CaptureTimestamp`
(`DateTime`).

## 4. First-run config

On first Play (Editor or device), `PipelineConfig` writes a template JSON file to
`Application.persistentDataPath/pipeline-config.json` and logs the exact path to the
Console. The very first time, before the in-VR panel below exists in a build on your
headset, you have to seed/edit this file the slow way (adb pull/edit/push -- see the
comments in `PipelineConfig.cs` for exact commands). After that, use the panel.

Leave `apiTokens` empty -- the live server has no authentication (see
`docs/pipeline-api.md` "Security"); the field exists only in case that changes later.
When the server does start checking tokens, `apiTokens` is a *list* of candidate tokens
(one per machine/headset); the client tries each, sticks with the first the server
accepts, and caches it in `apiToken`. Don't hand-edit `apiToken` -- it's auto-filled.

### Setting the server address from inside the app (recommended -- the address changes often)

`Assets/Scripts/UI/PipelineConfigPanel.cs` lets you type the server's LAN address
in-headset and save it, no PC/adb round-trip required. It reads/writes the same
`PipelineConfig.Instance` that `PipelineApiClient` uses, and `PipelineApiClient` now
reads `baseUrl`/token live on every request (not a copy taken at construction) --
so saving here takes effect immediately, even for a `PipelineApiClient` some other
component already created earlier in the session.

There is no "Panel" Building Block in what's installed here -- a world-space Canvas
that's actually clickable in VR uses the Interaction SDK's own canvas-interaction
components instead (`com.meta.xr.sdk.interaction`), which pair naturally with the
`RayInteractor` your controllers already have from the Controller Tracking Building
Block. (An earlier version of this doc described `OVRRaycaster`/`OVRInputModule`
instead -- that classic Core SDK path also works, but this project has since moved to
the Interaction SDK path below; don't mix the two on the same Canvas, they conflict.)

1. **GameObject → UI → Canvas** to create the panel (this also creates an
   **EventSystem**). Select the Canvas, set **Render Mode** to **World Space**, and
   position/scale/rotate it to sit in front of where the user stands (e.g. position
   `(0, 1.4, 1)`, Rect Transform scale around `0.001` per axis -- Canvas units are
   pixels).
2. Leave the Canvas's default **Graphic Raycaster** as-is (don't swap it for
   `OVR Raycaster` -- the Interaction SDK's `Pointable Canvas` requires the stock
   one and asserts against anything else).
3. On the **EventSystem**: remove **Standalone Input Module**, add
   **Pointable Canvas Module** instead.
4. On the Canvas, add **Pointable Canvas** (Add Component → search "Pointable
   Canvas"). Set its **Canvas** field to the Canvas component on the same object.
5. Give the Canvas a raycast surface: add a child GameObject named `Surface`, Rect
   Transform stretched to fill the Canvas (anchors `0,0`–`1,1`, size delta `0,0`).
   Add to it: **Plane Surface**, **Bounds Clipper**, **Rect Transform Bounds Clipper
   Driver** (its `Bounds Clipper` field auto-fills on add), and **Clipped Plane
   Surface** (set its `Plane Surface` field to the Plane Surface above, and add the
   Bounds Clipper to its `Clippers` list).
6. Back on the Canvas, add **Ray Interactable**. Set `Surface` → the Clipped Plane
   Surface from step 5, `Select Surface` → the Plane Surface from step 5,
   `Pointable Element` → the Pointable Canvas from step 4. (A `Ray Interactable` with
   an unassigned `Surface` throws a `NullReferenceException` every frame in
   `InteractorGroup.Drive()` -- confirmed the hard way. Don't leave it unset, and
   don't end up with two of these targeting the same Canvas.)
7. Add one `TMP_InputField` (the server host, e.g. `192.168.1.45` -- no scheme, no
   port) and two `Button`s (Save, Test Connection) as children of the Canvas, plus a
   `TMP_Text` for status. If this is the first TextMeshPro component in the project,
   accept the "Import TMP Essentials" prompt.

   **Also enable OVR Manager → Requires System Keyboard** on your Camera Rig, or the
   Quest OS will never render a keyboard when the field is selected -- no error, it
   just silently does nothing when clicked. This bit us once; it's a whole-app,
   one-time manifest declaration, unrelated to any per-field script.

   There's no port field and no API token field on the canvas -- typing a token via VR
   keyboard isn't worth building for even though address typing now works fine. The
   port is fixed at `PipelineConfigPanel.DefaultPort` (`5000`, per
   `docs/pipeline-api.md`) since this is one known server; change that constant and
   rebuild if it's ever different. `PipelineConfigPanel` has a `_staticApiTokens`
   Inspector list instead of typed-in tokens: fill it in the Editor (one entry per
   headset), and it's applied automatically on every Save. The client tries each and
   sticks with whichever the server accepts, so you can roll machines out one at a time.

   **Security, for "one server + a few known headsets on a trusted LAN":** the live
   server has no authentication at all (see `docs/pipeline-api.md` "Security") -- so
   leaving `_staticApiTokens` empty is fine functionally, since the header does nothing
   server-side yet. If you want it to do something, add a matching check to the
   separate `pipeline-controller` repo (out of scope here per CLAUDE.md's
   "Non-goals") -- see the Flask sketch in `PipelineConfigPanel.cs`'s class doc
   comment, which validates the `X-API-Token` header against a whitelist of tokens.
   Give each headset its own token in `_staticApiTokens`, and add that token to the
   server's whitelist when you're ready to let that machine in -- the headset starts
   working on its next request, no rebuild. Even then, this only stops other devices on
   the LAN from casually hitting the API; it's plaintext HTTP, so it doesn't defend
   against someone actually sniffing traffic on that network.
8. Add `PipelineConfigPanel` to the Canvas (or any child), assign the host input
   field (`_hostInput`) and the status text.
9. Button wiring (Inspector → Button → On Click ()):
   - Save button → `PipelineConfigPanel.OnSavePressed`
   - Test Connection button → `PipelineConfigPanel.OnTestConnectionPressed` (saves
     first, then does a live reachability check and reports it in the status text --
     a plain 404 back from the server still counts as "reachable", only a dead
     connection/timeout counts as failure)
10. Toggle the Canvas's active state however fits your app (a menu button, a
    controller chord, whatever) -- this script doesn't assume anything about how it's
    opened.

## 4b. Smoke-testing the pipeline + mesh loading from a PC (no headset)

`Assets/Scripts/Testing/PipelineSmokeTest.cs` exercises the API client and glTFast
mesh loading end to end -- upload → `/select` → poll → `/objects` → download →
instantiate -- entirely in the Editor on a normal PC. It deliberately doesn't touch
`PassthroughFrameCapture`/`DepthFrameCapture`/`Meta.XR` at all, so it's the fastest way
to find out whether Unity can talk to the pipeline server and load its meshes, before
ever putting the headset on.

1. Add a `PipelineSmokeTest` component to any GameObject in a scene.
2. Assign `_testImage` to a real photo with a clear object in it (its import settings
   need **Read/Write Enabled** checked, or `EncodeToPNG` throws).
3. Set `_testPointNormalized` to roughly where that object is (0,0 = bottom-left, 1,1
   = top-right of the image).
4. Add a `MeshInstantiator` component somewhere and assign it too.
5. Make sure `pipeline-config.json` (step 4 above) points at the real server.
6. Enter Play mode → right-click the component's header in the Inspector → **Run
   Pipeline Smoke Test**. Watch the Console for `[SmokeTest]` lines; on success the
   mesh appears in the Scene at `_placementPosition`.

This only proves the plumbing and mesh format work -- no depth/intrinsics are sent
(it's a plain photo), so don't read anything into where/how the mesh is scaled or
rotated from this test specifically. That's what step 5 below is for, on a real
device capture.

## 5. What to check on the very first real mesh (per CLAUDE.md)

These are called out in the code comments too, but worth restating as a checklist for
your first end-to-end test:

- [ ] Does a `/select` request actually make it through the whole pipeline to a
      downloadable mesh? (Per CLAUDE.md, this has never been tested end-to-end.)
- [ ] Is the placed mesh's position/orientation correct, or does it need the
      `MeshInstantiator._axisCorrectionEulerDegrees` fix (glTF vs Unity handedness)?
- [ ] Is the depth grid's Y-axis in the right orientation (see the two `NOTE
      (verify-on-first-real-test)` comments in `DepthFrameCapture.cs` and
      `PointSelectController.cs`)?
- [ ] How long did SAM3D reconstruction actually take? Tune
      `PointSelectController._pollIntervalSeconds` and your "processing" UX
      accordingly.
