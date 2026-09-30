using System.Reflection;
using TMPro;
using UnityEngine;

namespace UI
{
    /// <summary>
    /// Window behaviour for the in-VR control panel: minimise it to just its title bar and bring
    /// it back, and recenter it in front of the headset if it's out of view.
    ///
    /// Minimising hides the body, shrinks the canvas to the title-bar height (keeping the top
    /// edge where it was), and shrinks the Interaction SDK's bounds clipper to match so clicks
    /// only register on what's visible. Set up by ControlPanelStyler; nothing to wire by hand.
    /// </summary>
    public class ControlPanelWindow : MonoBehaviour
    {
        private RectTransform _root;
        private GameObject _body;
        private CanvasGroup _overlayGroup;
        private TMP_Text _minimiseLabel;
        private float _expandedHeight;
        private float _minimisedHeight;
        private float _width;
        private bool _minimised;

        public bool IsMinimised => _minimised;

        public void Init(RectTransform root, GameObject body, CanvasGroup overlayGroup, TMP_Text minimiseLabel,
                         float width, float expandedHeight, float minimisedHeight)
        {
            _root = root;
            _body = body;
            _overlayGroup = overlayGroup;
            _minimiseLabel = minimiseLabel;
            _width = width;
            _expandedHeight = expandedHeight;
            _minimisedHeight = minimisedHeight;
            Apply(false);
        }

        /// <summary>Wire to the minimise / restore button.</summary>
        public void ToggleMinimised() => Apply(!_minimised);

        public void SetMinimised(bool minimised) => Apply(minimised);

        private void Apply(bool minimised)
        {
            bool changed = minimised != _minimised;
            float oldHeight = _root.sizeDelta.y;
            float newHeight = minimised ? _minimisedHeight : _expandedHeight;
            _minimised = minimised;

            if (_body != null) _body.SetActive(!minimised);
            if (_overlayGroup != null)
            {
                // The processing overlay dims the body; hide it while minimised.
                _overlayGroup.alpha = minimised ? 0f : 1f;
                _overlayGroup.blocksRaycasts = false;
                _overlayGroup.interactable = false;
            }
            if (_minimiseLabel != null) _minimiseLabel.text = minimised ? "+" : "–";

            _root.sizeDelta = new Vector2(_width, newHeight);
            SetClipperSize(new Vector3(_width, newHeight, 0.01f));

            if (changed)
            {
                // The canvas pivots on its centre, so resizing moves the top edge; shift it back
                // so the title bar stays exactly where it was.
                float shift = (oldHeight - newHeight) * 0.5f * _root.lossyScale.y;
                _root.position += _root.up * shift;
            }
        }

        /// <summary>Puts the panel about a metre in front of the headset, upright, facing the user.</summary>
        public void Recenter()
        {
            Camera cam = Camera.main;
            if (cam == null) return;

            Vector3 forward = cam.transform.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 1e-4f) forward = cam.transform.up * -1f; // looking straight up/down
            forward.Normalize();

            // Top edge slightly below eye level so the title bar is comfortable to look at.
            float halfHeight = _root.sizeDelta.y * 0.5f * _root.lossyScale.y;
            Vector3 pos = cam.transform.position + forward * 0.95f;
            pos.y = cam.transform.position.y - 0.05f - halfHeight + 0.18f;
            _root.SetPositionAndRotation(pos, Quaternion.LookRotation(forward, Vector3.up));
        }

        // Interaction SDK's BoundsClipper lives in a package assembly; reach it by name so this
        // script doesn't need a hard reference to it. Its Size is what limits pointer hits.
        private void SetClipperSize(Vector3 size)
        {
            foreach (Component c in _root.GetComponentsInChildren<Component>(true))
            {
                if (c == null || c.GetType().Name != "BoundsClipper") continue;
                PropertyInfo prop = c.GetType().GetProperty("Size", BindingFlags.Public | BindingFlags.Instance);
                if (prop != null && prop.CanWrite) prop.SetValue(c, size);
            }
        }
    }
}
