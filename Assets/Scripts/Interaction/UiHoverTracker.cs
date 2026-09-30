using UnityEngine;
using UnityEngine.EventSystems;

namespace Interaction
{
    /// <summary>
    /// Tracks whether a VR ray pointer is currently over this UI element, so world-space
    /// input (ControllerSelectTrigger) can ignore trigger presses that are really UI
    /// clicks -- otherwise clicking Save/Test on the config panel also fires a full
    /// capture+upload (POST /api/projects), because the same trigger press drives both the
    /// UI and the point-select flow.
    ///
    /// Put this on the config panel's background graphic (a full-canvas Image with Raycast
    /// Target enabled), or on each interactive element. Meta's PointableCanvasModule routes
    /// ray interactions through Unity's EventSystem as ordinary UGUI pointer events, so
    /// IPointerEnter/ExitHandler fire here just like with a mouse -- this is SDK-agnostic
    /// (only UnityEngine.EventSystems).
    /// </summary>
    public class UiHoverTracker : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
    {
        private static int s_hoverCount;

        /// <summary>True while any tracked UI element is under a pointer.</summary>
        public static bool IsPointerOverUi => s_hoverCount > 0;

        // Guard so each element contributes at most one to the count, and so a disable
        // while hovered doesn't leak a permanent +1 (OnPointerExit wouldn't fire then).
        private bool _counted;

        public void OnPointerEnter(PointerEventData eventData)
        {
            if (_counted) return;
            _counted = true;
            s_hoverCount++;
        }

        public void OnPointerExit(PointerEventData eventData) => Clear();

        private void OnDisable() => Clear();

        private void Clear()
        {
            if (!_counted) return;
            _counted = false;
            if (s_hoverCount > 0) s_hoverCount--;
        }
    }
}
