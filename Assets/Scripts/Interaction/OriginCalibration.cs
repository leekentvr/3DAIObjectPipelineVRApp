using System;
using System.Collections;
using Meta.XR.MRUtilityKit;
using UnityEngine;

namespace Interaction
{
    /// <summary>
    /// Manual alignment origin. Stand at the SAME physical spot facing the SAME direction every
    /// session (e.g. a taped mark on the floor), press "Set origin here", and everything placed
    /// or saved from then on is stored relative to that point and heading -- so it lines up again
    /// next session, after a recenter, or after the headset changes hands, provided the origin is
    /// set again at the same spot.
    ///
    /// - Setting the origin first recenters the headset (OVRDisplay.RecenterPose), then locks the
    ///   origin to the headset's floor position and flat facing direction.
    /// - The origin is pinned with an OVRSpatialAnchor. The runtime keeps an anchor locked to the
    ///   real world when tracking is corrected or the tracking space shifts (a recenter, or the
    ///   headset re-localizing under heavy load), so the origin -- and every object placed
    ///   relative to it -- stays put instead of drifting away from the room. Without the anchor
    ///   (permission missing / creation failed) objects sit at fixed Unity coordinates and DO drift
    ///   when that happens.
    /// - A marker (ring + forward arrow) shows the origin. Until it is set, the marker follows
    ///   the headset (amber) as a preview of where it WOULD be set; once set it stays put (green).
    /// - The origin is invalidated when the headset is recentered by the user or taken off, and
    ///   new captures / room loads are blocked until it is set again (see TryRequire).
    ///
    /// Created automatically at startup if the scene has none.
    /// </summary>
    public class OriginCalibration : MonoBehaviour
    {
        public static OriginCalibration Instance { get; private set; }

        [Tooltip("Recenter the headset when setting the origin, so the tracking origin is reset " +
                 "at the calibration point too.")]
        [SerializeField] private bool _recenterHeadset = true;

        [Tooltip("MRUK's world lock rewrites the tracking space every frame to keep the room scan " +
                 "fixed. That fights the manual origin (and recentering) and can slowly shift " +
                 "everything relative to you, so it is switched off while this component runs.")]
        [SerializeField] private bool _disableMrukWorldLock = true;

        [Tooltip("Material for the marker lines. Leave empty to use Sprites/Default.")]
        [SerializeField] private Material _lineMaterial;

        [SerializeField] private float _ringRadius = 0.22f;
        [SerializeField] private float _arrowLength = 0.65f;
        [SerializeField] private float _lineWidth = 0.014f;
        [SerializeField] private Color _previewColor = new Color(1f, 0.72f, 0.15f, 0.9f);
        [SerializeField] private Color _lockedColor = new Color(0.2f, 0.92f, 0.6f, 1f);

        /// <summary>(isCalibrated, message) whenever the state or status text changes.</summary>
        public event Action<bool, string> OnStateChanged;

        public bool IsCalibrated { get; private set; }

        /// <summary>True when the origin is pinned to the real world by a spatial anchor.</summary>
        public bool IsAnchored => _anchor != null && _anchor.Created;
        public string StatusMessage { get; private set; } = "Origin not set";

        /// <summary>The origin frame: position on the floor, forward = the calibrated heading.
        /// Only meaningful while IsCalibrated.</summary>
        public Transform Frame => _frame;

        private Transform _frame;
        private OVRSpatialAnchor _anchor;
        private OVRCameraRig _rig;
        private readonly System.Collections.Generic.List<LineRenderer> _lines =
            new System.Collections.Generic.List<LineRenderer>();
        private bool _busy;
        private float _ignoreRecenterUntil;
        private bool _displaySubscribed;
        private Camera _cam;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void EnsureExists()
        {
            if (Instance != null || FindAnyObjectByType<OriginCalibration>() != null) return;
            new GameObject("OriginCalibration").AddComponent<OriginCalibration>();
        }

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(this); return; }
            Instance = this;

            _frame = new GameObject("OriginFrame").transform;
            BuildMarker();
            SetMarkerColor(_previewColor);
        }

        private void OnEnable()
        {
            OVRManager.HMDUnmounted += OnHeadsetRemoved;
        }

        private void OnDisable()
        {
            OVRManager.HMDUnmounted -= OnHeadsetRemoved;
            if (_displaySubscribed && OVRManager.display != null)
                OVRManager.display.RecenteredPose -= OnRecentered;
            _displaySubscribed = false;
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
            if (_frame != null) Destroy(_frame.gameObject);
        }

        /// <summary>True if a capture / room load may proceed. When no origin is set (and this
        /// component exists) returns false with a user-facing message.</summary>
        public static bool TryRequire(out string message)
        {
            if (Instance == null || Instance.IsCalibrated) { message = null; return true; }
            message = "Set the origin first: stand at your origin point, face the marked direction " +
                      "and press \"Set origin here\".";
            return false;
        }

        /// <summary>Button entry point: recenter (optionally) and lock the origin here.</summary>
        public void Calibrate()
        {
            if (_busy) return;
            StartCoroutine(CalibrateRoutine());
        }

        /// <summary>Forget the current origin (objects stay where they are until it's set again).</summary>
        public void Invalidate(string reason)
        {
            if (!IsCalibrated && StatusMessage == reason) return;
            RemoveAnchor();
            IsCalibrated = false;
            SetMarkerColor(_previewColor);
            SetState(false, reason);
        }

        private IEnumerator CalibrateRoutine()
        {
            _busy = true;
            SetState(IsCalibrated, "Setting origin ...");

            if (_recenterHeadset && OVRManager.display != null)
            {
                _ignoreRecenterUntil = Time.unscaledTime + 1.5f;
                OVRManager.display.RecenterPose();
                // Give tracking a few frames to settle on the new center.
                yield return null;
                yield return null;
                yield return new WaitForSeconds(0.3f);
            }

            if (!TryGetHeadFloorPose(out Vector3 pos, out Quaternion rot))
            {
                _busy = false;
                SetState(IsCalibrated, "Couldn't read the headset pose.");
                yield break;
            }

            // A live anchor owns its transform, so drop the old one before moving the frame.
            if (_anchor != null)
            {
                RemoveAnchor();
                yield return null;
            }

            _frame.SetPositionAndRotation(pos, rot);
            IsCalibrated = true;
            SetMarkerColor(_lockedColor);

            // Pin the origin to the real world so it survives tracking corrections.
            _anchor = _frame.gameObject.AddComponent<OVRSpatialAnchor>();
            float waited = 0f;
            while (_anchor != null && !_anchor.Created && waited < 4f)
            {
                waited += Time.unscaledDeltaTime;
                yield return null;
            }

            bool anchored = IsAnchored;
            if (!anchored)
            {
                RemoveAnchor();
                Debug.LogWarning("[Origin] Spatial anchor could not be created -- objects will not follow " +
                                 "tracking corrections (check the anchor permission / OVR anchor support).");
            }

            _busy = false;
            SetState(true, anchored
                ? "Origin set and anchored -- keep this spot and heading for next time."
                : "Origin set, but NOT anchored: it can drift if tracking shifts.");
        }

        private void RemoveAnchor()
        {
            if (_anchor != null)
            {
                Destroy(_anchor);
                _anchor = null;
            }
        }

        private bool _worldLockHandled;

        private void Update()
        {
            if (_disableMrukWorldLock && !_worldLockHandled && MRUK.Instance != null)
            {
                _worldLockHandled = true;
                if (MRUK.Instance.EnableWorldLock)
                {
                    MRUK.Instance.EnableWorldLock = false;
                    Debug.Log("[Origin] Disabled MRUK world lock -- the manual origin owns alignment.");
                }
            }

            // OVRManager.display doesn't exist until the manager has initialised.
            if (!_displaySubscribed && OVRManager.display != null)
            {
                OVRManager.display.RecenteredPose += OnRecentered;
                _displaySubscribed = true;
            }
        }

        private void LateUpdate()
        {
            // Until set, the marker follows the headset as a live preview of the origin.
            if (!IsCalibrated && !_busy && TryGetHeadFloorPose(out Vector3 pos, out Quaternion rot))
                _frame.SetPositionAndRotation(pos, rot);
        }

        private void OnRecentered()
        {
            if (Time.unscaledTime < _ignoreRecenterUntil) return; // our own recenter
            // An anchored origin keeps its place in the real world through a recenter.
            if (IsCalibrated && !IsAnchored)
                Invalidate("Headset was recentered -- set the origin again at your point.");
        }

        private void OnHeadsetRemoved()
        {
            if (IsCalibrated && !IsAnchored)
                Invalidate("Headset was removed -- set the origin again at your point.");
        }

        private void SetState(bool calibrated, string message)
        {
            StatusMessage = message;
            OnStateChanged?.Invoke(calibrated, message);
        }

        private bool TryGetHeadFloorPose(out Vector3 pos, out Quaternion rot)
        {
            pos = default;
            rot = Quaternion.identity;
            if (_cam == null) _cam = Camera.main;
            if (_cam == null) return false;
            if (_rig == null) _rig = FindAnyObjectByType<OVRCameraRig>();

            Transform head = _cam.transform;
            Vector3 forward = head.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 1e-4f) forward = head.up; // looking straight down/up
            forward.y = 0f;
            if (forward.sqrMagnitude < 1e-4f) return false;

            // Floor height: the tracking space sits on the floor with a floor-level tracking origin;
            // otherwise assume a standing eye height.
            float floorY = _rig != null && _rig.trackingSpace != null
                ? _rig.trackingSpace.position.y
                : head.position.y - 1.6f;

            pos = new Vector3(head.position.x, floorY, head.position.z);
            rot = Quaternion.LookRotation(forward.normalized, Vector3.up);
            return true;
        }

        // ------------------------------------------------------------------ marker

        private void BuildMarker()
        {
            const int ringSegments = 40;
            var ring = new Vector3[ringSegments];
            for (int i = 0; i < ringSegments; i++)
            {
                float a = i * Mathf.PI * 2f / ringSegments;
                ring[i] = new Vector3(Mathf.Sin(a) * _ringRadius, 0.005f, Mathf.Cos(a) * _ringRadius);
            }
            AddLine("Ring", ring, loop: true);

            // Forward arrow (+Z of the frame = the calibrated heading), with a head.
            Vector3 tip = new Vector3(0f, 0.005f, _arrowLength);
            float headLen = 0.14f;
            AddLine("Shaft", new[] { new Vector3(0f, 0.005f, 0f), tip }, loop: false);
            AddLine("HeadL", new[] { tip, tip + Quaternion.Euler(0f, 150f, 0f) * Vector3.forward * headLen }, loop: false);
            AddLine("HeadR", new[] { tip, tip + Quaternion.Euler(0f, -150f, 0f) * Vector3.forward * headLen }, loop: false);
            // Short cross-bar behind the origin so the tail end reads clearly.
            AddLine("Tail", new[] { new Vector3(-0.07f, 0.005f, 0f), new Vector3(0.07f, 0.005f, 0f) }, loop: false);
            // Vertical pole so the origin is visible from further away / above clutter.
            AddLine("Pole", new[] { new Vector3(0f, 0f, 0f), new Vector3(0f, 0.35f, 0f) }, loop: false);
        }

        private void AddLine(string name, Vector3[] points, bool loop)
        {
            var go = new GameObject(name);
            go.transform.SetParent(_frame, false);
            var lr = go.AddComponent<LineRenderer>();
            lr.useWorldSpace = false;
            lr.loop = loop;
            lr.positionCount = points.Length;
            lr.SetPositions(points);
            lr.widthMultiplier = _lineWidth;
            lr.numCapVertices = 4;
            lr.numCornerVertices = 4;
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            lr.receiveShadows = false;
            lr.sharedMaterial = _lineMaterial != null ? _lineMaterial : GetFallbackLineMaterial();
            _lines.Add(lr);
        }

        private static Material s_fallback;

        private static Material GetFallbackLineMaterial()
        {
            if (s_fallback != null) return s_fallback;
            Shader shader = Shader.Find("Sprites/Default");
            if (shader == null) shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null) return null;
            s_fallback = new Material(shader) { name = "OriginMarker (runtime)" };
            return s_fallback;
        }

        private void SetMarkerColor(Color c)
        {
            foreach (LineRenderer lr in _lines)
            {
                if (lr == null) continue;
                lr.startColor = c;
                lr.endColor = c;
            }
        }
    }
}
