using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Oculus.Interaction;
using UnityEngine;

namespace Interaction
{
    /// <summary>
    /// Makes a runtime-instantiated reconstruction mesh grabbable with the Meta Interaction
    /// SDK (near/direct grab), so you can hand-adjust its position, rotation and scale to
    /// align it with the real object, and records BOTH the initial (pipeline-placed) transform
    /// and the hand-adjusted one for comparison.
    ///
    /// Attach nothing by hand -- MeshInstantiator calls <see cref="Initialize"/> on the placed
    /// root right after placement (guarded by MeshInstantiator._makeGrabbable). It then:
    ///   * adds a BoxCollider per child mesh (sized from each mesh's local bounds),
    ///   * adds a kinematic Rigidbody,
    ///   * adds a Grabbable (MaxGrabPoints = 2 -> one hand moves/rotates, two hands also scale;
    ///     the Grabbable auto-creates a GrabFreeTransformer for both),
    ///   * adds a GrabInteractable wired to that Rigidbody + Grabbable.
    ///
    /// SCENE-SIDE REQUIREMENT (must be done once in the Editor, can't be added per-object at
    /// runtime): the hands/controllers need a grab INTERACTOR to do the grabbing. Add Meta's
    /// "Grab" interaction to your hands/controllers -- via Building Blocks (Meta -> Tools ->
    /// Building Blocks -> add "Grab" / "Controller Grab" / "Hand Grab"), or the OVRInteraction +
    /// OVRControllerGrab / OVRHandGrab rig. Without a GrabInteractor in the scene, these objects
    /// have everything needed to BE grabbed but nothing ever grabs them. (Controllers use
    /// GrabInteractor; hand-tracking uses HandGrabInteractor -- add whichever input you use.)
    ///
    /// On every full release (all grab points let go), the initial vs adjusted transforms and
    /// their delta are written as JSON to Application.persistentDataPath/pose-comparisons/ and
    /// logged. adb pull that folder to collect correction data -- useful ground truth for the
    /// SAM3D rotation work (compare the pipeline's baked pose against where a human actually
    /// had to put the object).
    /// </summary>
    public class GrabbableReconstruction : MonoBehaviour
    {
        [Header("Two-hand scaling")]
        [Tooltip("Allow two-hand grab to scale the object. The Interaction SDK's " +
                 "auto-generated transformer LOCKS scale to 1x by default, so without this " +
                 "the object only moves/rotates. Enabling it injects a transformer whose " +
                 "scale range is opened up (relative to the placed scale).")]
        [SerializeField] private bool _allowTwoHandScale = true;

        [Tooltip("Smallest allowed scale as a fraction of the object's placed scale " +
                 "(e.g. 0.1 = can shrink to 1/10th).")]
        [SerializeField, Range(0.001f, 1f)] private float _minScaleFactor = 0.1f;

        [Tooltip("Largest allowed scale as a multiple of the object's placed scale " +
                 "(e.g. 10 = can grow to 10x).")]
        [SerializeField, Range(1f, 100f)] private float _maxScaleFactor = 10f;

        /// <summary>Fired on every full release with the just-written comparison.</summary>
        public event Action<PoseComparison> OnComparisonRecorded;

        /// <summary>All comparisons recorded for this object this session (most recent last).</summary>
        public IReadOnlyList<PoseComparison> Comparisons => _comparisons;

        private readonly List<PoseComparison> _comparisons = new List<PoseComparison>();

        private string _label = "object";
        private Pose _initialWorldPose;
        private Vector3 _initialLossyScale;
        private bool _wasGrabbed;
        private Grabbable _grabbable;

        /// <summary>Call once, right after the mesh is placed at its initial pose.</summary>
        public void Initialize(string label)
        {
            _label = Sanitize(string.IsNullOrWhiteSpace(label) ? "object" : label);
            _initialWorldPose = new Pose(transform.position, transform.rotation);
            _initialLossyScale = transform.lossyScale;

            AddColliders();

            var rb = gameObject.GetComponent<Rigidbody>();
            if (rb == null) rb = gameObject.AddComponent<Rigidbody>();
            rb.isKinematic = true;   // manual alignment, no physics/gravity pulling it around
            rb.useGravity = false;

            _grabbable = gameObject.AddComponent<Grabbable>();
            _grabbable.MaxGrabPoints = 2; // 1 grab = move+rotate, 2 grabs = also scale
            _grabbable.InjectOptionalRigidbody(rb);

            // The Grabbable auto-generates a GrabFreeTransformer whose default scale
            // constraints lock every axis to exactly 1x (ConstrainAxis = true, range
            // {Min:1, Max:1}) -- that's why two-hand grab won't resize. Build our own
            // GrabFreeTransformer with an opened-up (relative) scale range and inject it
            // as both the one- and two-grab transformer, matching what the auto path does
            // except for the scale lock.
            if (_allowTwoHandScale)
            {
                float min = Mathf.Min(_minScaleFactor, _maxScaleFactor);
                float max = Mathf.Max(_minScaleFactor, _maxScaleFactor);
                var scaleConstraints = new TransformerUtils.ScaleConstraints
                {
                    ConstraintsAreRelative = true,
                    XAxis = OpenAxis(min, max),
                    YAxis = OpenAxis(min, max),
                    ZAxis = OpenAxis(min, max),
                };

                var transformer = gameObject.AddComponent<GrabFreeTransformer>();
                transformer.InjectOptionalScaleConstraints(scaleConstraints);
                _grabbable.InjectOptionalOneGrabTransformer(transformer);
                _grabbable.InjectOptionalTwoGrabTransformer(transformer);
            }

            var interactable = gameObject.AddComponent<GrabInteractable>();
            interactable.InjectAllGrabInteractable(rb);
            interactable.InjectOptionalPointableElement(_grabbable);

            // Injection above completes before any of these components' Start() runs (Start is
            // deferred to the next frame), so the SDK sees a fully-configured object.

            _grabbable.WhenPointerEventRaised += HandlePointerEvent;

            Debug.Log($"[Grab] '{_label}' is now grabbable. Initial pose pos={_initialWorldPose.position} " +
                      $"rot={_initialWorldPose.rotation.eulerAngles} scale={_initialLossyScale}. " +
                      "Needs a GrabInteractor/HandGrabInteractor on your hands to actually grab (see class doc).");
        }

        private void OnDestroy()
        {
            if (_grabbable != null) _grabbable.WhenPointerEventRaised -= HandlePointerEvent;
        }

        private void HandlePointerEvent(PointerEvent evt)
        {
            // Track grab state via the element's live selecting-points count so two-handed
            // grabs (two Select events) only count as "released" once BOTH hands let go.
            int selecting = _grabbable != null ? _grabbable.SelectingPointsCount : 0;

            if (evt.Type == PointerEventType.Select && selecting > 0)
            {
                _wasGrabbed = true;
            }
            else if ((evt.Type == PointerEventType.Unselect || evt.Type == PointerEventType.Cancel)
                     && _wasGrabbed && selecting == 0)
            {
                _wasGrabbed = false;
                RecordComparison();
            }
        }

        private void RecordComparison()
        {
            var adjustedPose = new Pose(transform.position, transform.rotation);
            Vector3 adjustedScale = transform.lossyScale;

            Quaternion rotDelta = Quaternion.Inverse(_initialWorldPose.rotation) * adjustedPose.rotation;
            rotDelta.ToAngleAxis(out float rotDeltaAngle, out _);
            if (rotDeltaAngle > 180f) rotDeltaAngle = 360f - rotDeltaAngle; // report shortest turn

            var cmp = new PoseComparison
            {
                label = _label,
                timestampUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
                initial = TransformRecord.From(_initialWorldPose.position, _initialWorldPose.rotation, _initialLossyScale),
                adjusted = TransformRecord.From(adjustedPose.position, adjustedPose.rotation, adjustedScale),
                positionDelta = ToArray(adjustedPose.position - _initialWorldPose.position),
                positionDeltaMeters = Vector3.Distance(adjustedPose.position, _initialWorldPose.position),
                rotationDeltaEuler = ToArray(rotDelta.eulerAngles),
                rotationDeltaDegrees = rotDeltaAngle,
                scaleRatio = ToArray(SafeRatio(adjustedScale, _initialLossyScale)),
            };

            _comparisons.Add(cmp);
            WriteJson(cmp);

            Debug.Log($"[Grab] '{_label}' released. Δpos={cmp.positionDeltaMeters:F3} m, " +
                      $"Δrot={cmp.rotationDeltaDegrees:F1}°, scale×=({cmp.scaleRatio[0]:F2},{cmp.scaleRatio[1]:F2},{cmp.scaleRatio[2]:F2}). " +
                      "Full record written to persistentDataPath/pose-comparisons/.");

            OnComparisonRecorded?.Invoke(cmp);
        }

        private void WriteJson(PoseComparison cmp)
        {
            try
            {
                string dir = Path.Combine(Application.persistentDataPath, "pose-comparisons");
                Directory.CreateDirectory(dir);
                string stamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff");
                string path = Path.Combine(dir, $"{_label}_{stamp}.json");
                File.WriteAllText(path, JsonUtility.ToJson(cmp, true));
                Debug.Log($"[Grab] Wrote pose comparison to {path}. Pull with: adb pull \"{dir}\" .");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Grab] Failed to write pose comparison JSON: {e.Message}");
            }
        }

        // A BoxCollider per child mesh, sized from that mesh's local-space bounds -- robust to
        // the root's own scale/rotation, and enough surface for a near grab. Falls back to a
        // renderer-bounds box on the root if the object has no MeshFilters (e.g. points-only).
        private void AddColliders()
        {
            var filters = GetComponentsInChildren<MeshFilter>();
            int added = 0;
            foreach (var mf in filters)
            {
                Mesh m = mf.sharedMesh;
                if (m == null) continue;
                var box = mf.gameObject.AddComponent<BoxCollider>();
                box.center = m.bounds.center;
                box.size = m.bounds.size;
                added++;
            }

            if (added == 0)
            {
                var renderers = GetComponentsInChildren<Renderer>();
                if (renderers.Length > 0)
                {
                    Bounds b = renderers[0].bounds;
                    foreach (var r in renderers) b.Encapsulate(r.bounds);
                    var box = gameObject.AddComponent<BoxCollider>();
                    box.center = transform.InverseTransformPoint(b.center);
                    // Approximate local size (ignores rotation; fine as a grab target).
                    Vector3 ls = transform.lossyScale;
                    box.size = new Vector3(
                        SafeDiv(b.size.x, ls.x), SafeDiv(b.size.y, ls.y), SafeDiv(b.size.z, ls.z));
                }
            }
        }

        // A scale axis constrained to a relative [min, max] factor range (vs the locked
        // {1,1} default that prevents resizing).
        private static TransformerUtils.ConstrainedAxis OpenAxis(float min, float max) =>
            new TransformerUtils.ConstrainedAxis
            {
                ConstrainAxis = true,
                AxisRange = new TransformerUtils.FloatRange { Min = min, Max = max },
            };

        private static Vector3 SafeRatio(Vector3 a, Vector3 b) => new Vector3(
            SafeDiv(a.x, b.x), SafeDiv(a.y, b.y), SafeDiv(a.z, b.z));

        private static float SafeDiv(float a, float b) => Mathf.Abs(b) < 1e-6f ? 0f : a / b;

        private static float[] ToArray(Vector3 v) => new[] { v.x, v.y, v.z };

        private static string Sanitize(string s)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s.Replace(' ', '_');
        }

        [Serializable]
        public class TransformRecord
        {
            public float[] position;
            public float[] rotationEuler;
            public float[] rotationQuaternion; // x,y,z,w
            public float[] scale;

            public static TransformRecord From(Vector3 pos, Quaternion rot, Vector3 scale) => new TransformRecord
            {
                position = new[] { pos.x, pos.y, pos.z },
                rotationEuler = new[] { rot.eulerAngles.x, rot.eulerAngles.y, rot.eulerAngles.z },
                rotationQuaternion = new[] { rot.x, rot.y, rot.z, rot.w },
                scale = new[] { scale.x, scale.y, scale.z },
            };
        }

        [Serializable]
        public class PoseComparison
        {
            public string label;
            public string timestampUtc;
            public TransformRecord initial;
            public TransformRecord adjusted;
            public float[] positionDelta;         // adjusted - initial, world meters (x,y,z)
            public float positionDeltaMeters;     // magnitude
            public float[] rotationDeltaEuler;    // initial->adjusted, degrees (x,y,z)
            public float rotationDeltaDegrees;    // single-axis angle of that delta
            public float[] scaleRatio;            // adjusted / initial (x,y,z)
        }
    }
}
