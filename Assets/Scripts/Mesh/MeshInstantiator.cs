using System.Threading.Tasks;
using GLTFast;
using GLTFast.Logging;
using GLTFast.Materials;
using UnityEngine;

namespace Pipeline
{
    /// <summary>
    /// Downloads and instantiates a reconstructed object mesh (a transformedobject_N.glb
    /// per docs/pipeline-api.md) using glTFast, then places the result at a given world
    /// pose.
    ///
    /// WHY THE FALLBACK MATERIAL (the fix for "grey in the Editor, invisible in VR"):
    /// SAM3D's meshes ship with NO materials (verified: the .glb has materials=0 and its
    /// primitive's material is null). So glTFast falls back to its *default* material,
    /// whose shader is a URP ShaderGraph. Those graphs are compiled on-demand in the
    /// Editor but STRIPPED from a Quest/Android build unless you preload them -- when they
    /// are missing, glTFast's ShaderGraphMaterialGenerator.GenerateDefaultMaterial throws a
    /// NullReferenceException mid-instantiation, so the object is never created and nothing
    /// appears in VR. Rather than fight build-time shader stripping, we hand glTFast our
    /// own default material built from URP's *Unlit* shader, which is always present in a
    /// URP build (it's referenced by the pipeline itself). glTFast then never touches the
    /// stripped shadergraph, so the mesh instantiates and renders on device.
    ///
    /// IMPORTANT -- unverified per CLAUDE.md's "Known open risks": glTF is Y-up/
    /// right-handed, Unity is Y-up/left-handed. glTFast converts the mesh geometry
    /// automatically, but whether the camera-relative pose baked into the mesh lines up
    /// with Unity's axes once instantiated at the real captured-camera transform hasn't
    /// been checked against a real mesh yet. If the object appears mirrored, rotated ~180
    /// degrees, or offset, that's this assumption breaking -- _axisCorrectionEulerDegrees
    /// is the single place to correct it.
    /// </summary>
    public class MeshInstantiator : MonoBehaviour
    {
        [Tooltip("Extra local rotation applied on top of the requested world pose. Leave " +
                 "at zero until axis convention is verified against a real reconstructed mesh.")]
        [SerializeField] private Vector3 _axisCorrectionEulerDegrees = Vector3.zero;

        [Tooltip("Material used for meshes that carry no material of their own (all SAM3D " +
                 "meshes). If left empty, one is created at runtime from URP's Unlit shader. " +
                 "Assign your own here (e.g. a vertex-color or lit URP material) to control " +
                 "how reconstructed objects look.")]
        [SerializeField] private Material _fallbackMaterial;

        [Tooltip("Tint multiplied onto the auto-created fallback material (only used when " +
                 "no material is assigned above). White shows the mesh's baked vertex " +
                 "colors true; any other color tints them.")]
        [SerializeField] private Color _fallbackColor = Color.white;

        [Header("Raycast-anchored placement")]
        [Tooltip("Manual uniform scale applied to anchored placements, until the server's " +
                 "metric path is fixed. 1 = use the mesh's own baked scale.")]
        [SerializeField] private float _uniformScaleMultiplier = 1f;

        [Header("Manual alignment")]
        [Tooltip("Make each placed object grabbable (Meta Interaction SDK near-grab) so you " +
                 "can hand-adjust its position/rotation/scale, and log the initial vs " +
                 "hand-placed transforms for comparison. Requires a GrabInteractor/" +
                 "HandGrabInteractor on your hands in the scene -- see GrabbableReconstruction " +
                 "class doc. The object's placed pose (below) is recorded as the 'initial'.")]
        [SerializeField] private bool _makeGrabbable = true;

        /// <summary>How the anchored object lines up with the target world point.</summary>
        public enum PlacementAnchor
        {
            /// <summary>The object's bounding-box center sits at the target point.</summary>
            BoundsCenter,
            /// <summary>The object's bounding-box base sits at the target point (good for
            /// something resting on the floor/table you pointed at).</summary>
            BoundsBottom
        }

        private Material _runtimeFallback;

        /// <summary>Loads + instantiates the glb and places the root's ORIGIN at the given
        /// world pose. Note: transformedobject_N.glb bakes a camera-relative translation
        /// into its vertices, so the mesh may sit far from wherever you put the origin --
        /// prefer LoadAndAnchorAsync when you want the object itself to land at a point.</summary>
        public async Task<GameObject> LoadAndPlaceAsync(
            byte[] glbBytes,
            Vector3 worldPosition,
            Quaternion worldRotation,
            string name = "ReconstructedObject")
        {
            GameObject root = await LoadInternalAsync(glbBytes, name);
            if (root == null) return null;

            root.transform.SetPositionAndRotation(
                worldPosition, worldRotation * Quaternion.Euler(_axisCorrectionEulerDegrees));

            LogPlacementDiagnostics(root);
            SetupGrabbable(root, name);
            return root;
        }

        /// <summary>
        /// Loads + instantiates the glb and anchors the OBJECT (by its rendered bounds, not
        /// its arbitrary local origin) to a world point -- e.g. an environment-raycast hit.
        /// This is the robust VR placement: it uses Unity's own tracking/scene depth for
        /// position and ignores the server's baked camera-relative translation, which is in
        /// an unresolved coordinate frame. Rotation/scale still come from the caller/mesh
        /// (refine once the metric path is fixed); _uniformScaleMultiplier is a manual knob.
        /// </summary>
        public async Task<GameObject> LoadAndAnchorAsync(
            byte[] glbBytes,
            Vector3 worldAnchor,
            Quaternion worldRotation,
            string name = "ReconstructedObject",
            PlacementAnchor anchor = PlacementAnchor.BoundsBottom)
        {
            GameObject root = await LoadInternalAsync(glbBytes, name);
            if (root == null) return null;

            // Orient + scale FIRST so bounds are measured in final form, then translate so
            // the chosen anchor of those bounds lands exactly on worldAnchor.
            root.transform.rotation = worldRotation * Quaternion.Euler(_axisCorrectionEulerDegrees);
            if (!Mathf.Approximately(_uniformScaleMultiplier, 1f))
            {
                root.transform.localScale *= _uniformScaleMultiplier;
            }

            if (TryGetWorldBounds(root, out Bounds b))
            {
                Vector3 anchorPoint = anchor == PlacementAnchor.BoundsBottom
                    ? new Vector3(b.center.x, b.min.y, b.center.z)
                    : b.center;
                root.transform.position += worldAnchor - anchorPoint;
            }
            else
            {
                root.transform.position = worldAnchor;
            }

            LogPlacementDiagnostics(root);
            SetupGrabbable(root, name);
            return root;
        }

        /// <summary>Adds a GrabbableReconstruction (near-grab + initial/hand-placed transform
        /// logging) to the placed object, recording its current transform as the "initial"
        /// pose. No-op if _makeGrabbable is off. Needs a GrabInteractor in the scene to
        /// actually be grabbable -- see GrabbableReconstruction's class doc.</summary>
        private void SetupGrabbable(GameObject root, string label)
        {
            if (!_makeGrabbable || root == null) return;
            var grab = root.AddComponent<Interaction.GrabbableReconstruction>();
            grab.Initialize(label);
        }

        /// <summary>Loads a glb and places its root at an EXACT world transform (position,
        /// rotation, and local scale copied verbatim -- no anchoring, no axis correction).
        /// Used by the LoD swap to reload the same object at a different detail level while
        /// keeping precisely where the user put it. Optionally re-adds grab support.</summary>
        public async Task<GameObject> InstantiateAtAsync(
            byte[] glbBytes,
            Vector3 position,
            Quaternion rotation,
            Vector3 localScale,
            string name = "ReconstructedObject",
            bool makeGrabbable = false)
        {
            GameObject root = await LoadInternalAsync(glbBytes, name);
            if (root == null) return null;

            root.transform.SetPositionAndRotation(position, rotation);
            root.transform.localScale = localScale;

            LogPlacementDiagnostics(root);
            if (makeGrabbable) SetupGrabbable(root, name);
            return root;
        }

        /// <summary>Shared load + instantiate. Returns the instantiated root (unplaced), or
        /// null on failure (with glTFast's own diagnostics already logged).</summary>
        private async Task<GameObject> LoadInternalAsync(byte[] glbBytes, string name)
        {
            // Collect glTFast's own diagnostics so the exact reason for any failure is
            // visible (replayed via LogAll below), instead of an unattributed console line.
            var logger = new CollectingLogger();

            // Hand glTFast a default material built from a shader that survives build-time
            // stripping (see class doc). This is what makes the object appear in VR.
            var materialGenerator = new FallbackMaterialGenerator(GetFallbackMaterial());

            var gltf = new GltfImport(materialGenerator: materialGenerator, logger: logger);

            bool loaded = await gltf.Load(glbBytes);
            if (!loaded)
            {
                Debug.LogError("[Mesh] glTFast failed to parse/load the downloaded .glb. glTFast said:");
                logger.LogAll();
                gltf.Dispose();
                return null;
            }

            var root = new GameObject(name);
            bool instantiated = await gltf.InstantiateMainSceneAsync(root.transform);
            if (!instantiated)
            {
                Debug.LogError("[Mesh] glTFast failed to instantiate the glTF scene. glTFast said:");
                logger.LogAll();
                Object.Destroy(root);
                gltf.Dispose();
                return null;
            }

            // glTFast owns the meshes/textures it created; without Dispose they leak every time
            // an object is destroyed (room reloads, LoD swaps, the placed-object cap) and
            // eventually exhaust headset memory.
            root.AddComponent<GltfImportHolder>().Import = gltf;

            logger.LogAll();
            return root;
        }

        /// <summary>Disposes the GltfImport that created an object when that object is destroyed.</summary>
        private sealed class GltfImportHolder : MonoBehaviour
        {
            public GltfImport Import;

            private void OnDestroy()
            {
                Import?.Dispose();
                Import = null;
            }
        }

        /// <summary>Combined world-space bounds of all renderers under root.</summary>
        private static bool TryGetWorldBounds(GameObject root, out Bounds bounds)
        {
            var renderers = root.GetComponentsInChildren<Renderer>();
            if (renderers.Length == 0)
            {
                bounds = default;
                return false;
            }
            bounds = renderers[0].bounds;
            foreach (Renderer r in renderers) bounds.Encapsulate(r.bounds);
            return true;
        }

        /// <summary>Logs renderer count and world-space bounds so an object that loaded but
        /// isn't visible can be diagnosed: zero renderers = nothing to draw; a huge/tiny
        /// size or a center far from the user = placement/scale, not a material bug.</summary>
        private static void LogPlacementDiagnostics(GameObject root)
        {
            var renderers = root.GetComponentsInChildren<Renderer>();
            if (renderers.Length == 0)
            {
                Debug.LogWarning($"[Mesh] '{root.name}' instantiated but has NO renderers -- " +
                                 "nothing will draw. The glTF may be points-only or empty.");
                return;
            }

            Bounds b = renderers[0].bounds;
            foreach (Renderer r in renderers) b.Encapsulate(r.bounds);

            long verts = 0, tris = 0;
            foreach (MeshFilter mf in root.GetComponentsInChildren<MeshFilter>())
            {
                Mesh m = mf.sharedMesh;
                if (m == null) continue;
                verts += m.vertexCount;
                for (int s = 0; s < m.subMeshCount; s++) tris += (long)(m.GetIndexCount(s) / 3);
            }

            Debug.Log($"[Mesh] '{root.name}': {renderers.Length} renderer(s), " +
                      $"{verts:N0} verts / {tris:N0} tris. " +
                      $"World bounds center={b.center} size={b.size}. " +
                      $"Root pos={root.transform.position} lossyScale={root.transform.lossyScale}. " +
                      "If size is tiny/huge or center is far from the headset, it's placement/" +
                      "scale, not a material issue. A high tri count here is why framerate " +
                      "drops with several objects on-screen -- these meshes need decimating.");
        }

        private Material GetFallbackMaterial()
        {
            if (_fallbackMaterial != null) return _fallbackMaterial;
            if (_runtimeFallback != null) return _runtimeFallback;

            // Prefer our vertex-color shader so SAM3D's baked COLOR_0 shows instead of a
            // flat color. Fall back to URP's own Unlit (always in a URP build) if the
            // shader asset wasn't included in the build -- to guarantee inclusion, assign
            // a Material made from VR23D/VertexColorUnlit to _fallbackMaterial above.
            Shader shader = Shader.Find("VR23D/VertexColorUnlit");
            if (shader == null) shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null) shader = Shader.Find("Unlit/Color");
            if (shader == null) shader = Shader.Find("Sprites/Default");
            if (shader == null)
            {
                Debug.LogError("[Mesh] Could not find a URP/Unlit fallback shader. Assign a " +
                               "Material to MeshInstantiator._fallbackMaterial in the Inspector.");
                return null;
            }

            _runtimeFallback = new Material(shader) { name = "glTF Fallback (runtime)" };
            // URP shaders read _BaseColor; also set legacy .color for non-URP fallbacks.
            if (_runtimeFallback.HasProperty("_BaseColor")) _runtimeFallback.SetColor("_BaseColor", _fallbackColor);
            _runtimeFallback.color = _fallbackColor;
            return _runtimeFallback;
        }
    }

    /// <summary>
    /// Minimal glTFast material generator that returns one fixed Unity material for every
    /// glTF material request -- including the "no material assigned" default path. This
    /// deliberately bypasses glTFast's built-in shadergraph generation, which NREs on a
    /// build where those graphs were stripped. SAM3D meshes carry no materials, so in this
    /// project only GetDefaultMaterial is ever actually hit; GenerateMaterial returns the
    /// same material for completeness/safety.
    /// </summary>
    internal sealed class FallbackMaterialGenerator : IMaterialGenerator
    {
        private readonly UnityEngine.Material _material;

        public FallbackMaterialGenerator(UnityEngine.Material material)
        {
            _material = material;
        }

        public void SetLogger(ICodeLogger logger) { /* no-op: this generator can't fail */ }

        public UnityEngine.Material GetDefaultMaterial(bool pointsSupport = false) => _material;

        public UnityEngine.Material GenerateMaterial(
            GLTFast.Schema.MaterialBase gltfMaterial, IGltfReadable gltf, bool pointsSupport = false)
            => _material;
    }
}
