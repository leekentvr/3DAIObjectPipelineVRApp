using System.Threading.Tasks;
using GLTFast;
using UnityEngine;

namespace Pipeline
{
    /// <summary>
    /// Downloads and instantiates a reconstructed object mesh (a transformedobject_N.glb
    /// per docs/pipeline-api.md) using glTFast, then places the result at a given world
    /// pose.
    ///
    /// IMPORTANT -- unverified per CLAUDE.md's "Known open risks": glTF is Y-up/
    /// right-handed, Unity is Y-up/left-handed. glTFast converts the mesh geometry
    /// itself automatically, but whether the *camera-relative pose baked into the mesh*
    /// lines up with Unity's axes once instantiated at the real captured-camera
    /// transform has not been checked against a real mesh yet. Treat the first
    /// successful load as the test of this, not a given -- if the object appears
    /// mirrored, rotated ~180 degrees, or offset along one axis, that's this assumption
    /// breaking. _axisCorrectionEulerDegrees exists as a single place to apply a fix
    /// once you know what's wrong; don't scatter ad-hoc corrections elsewhere.
    /// </summary>
    public class MeshInstantiator : MonoBehaviour
    {
        [Tooltip("Extra local rotation applied on top of the requested world pose. Leave " +
                 "at zero until axis convention is verified against a real reconstructed mesh.")]
        [SerializeField] private Vector3 _axisCorrectionEulerDegrees = Vector3.zero;

        public async Task<GameObject> LoadAndPlaceAsync(
            byte[] glbBytes,
            Vector3 worldPosition,
            Quaternion worldRotation,
            string name = "ReconstructedObject")
        {
            var gltf = new GltfImport();

            bool loaded = await gltf.Load(glbBytes);
            if (!loaded)
            {
                Debug.LogError("[Mesh] glTFast failed to parse/load the downloaded .glb.");
                return null;
            }

            var root = new GameObject(name);
            bool instantiated = await gltf.InstantiateMainSceneAsync(root.transform);
            if (!instantiated)
            {
                Debug.LogError("[Mesh] glTFast failed to instantiate the glTF scene.");
                Object.Destroy(root);
                return null;
            }

            root.transform.SetPositionAndRotation(worldPosition, worldRotation * Quaternion.Euler(_axisCorrectionEulerDegrees));
            return root;
        }
    }
}
