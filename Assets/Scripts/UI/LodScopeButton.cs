using Interaction;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace UI
{
    /// <summary>
    /// "Applies to: all / last placed" toggle for the LoD buttons. Bind the button's OnClick to
    /// Toggle(); the label follows LodTuner's scope (which also changes on its own after a room
    /// load or a new placement).
    /// </summary>
    public class LodScopeButton : MonoBehaviour
    {
        [SerializeField] private LodTuner _tuner;
        [SerializeField] private TMP_Text _label;
        [SerializeField] private Button _button;

        public void Configure(LodTuner tuner, TMP_Text label, Button button)
        {
            _tuner = tuner;
            _label = label;
            _button = button;

            // AddComponent already ran OnEnable with no fields set; hook up now (play mode only).
            if (Application.isPlaying && isActiveAndEnabled) { OnDisable(); OnEnable(); }
        }

        private void OnEnable()
        {
            if (_tuner == null) _tuner = FindAnyObjectByType<LodTuner>();
            if (_button != null) _button.interactable = _tuner != null;
            if (_tuner == null) return;
            _tuner.OnScopeChanged += Refresh;
            Refresh(_tuner.ApplyToAll);
        }

        private void OnDisable()
        {
            if (_tuner != null) _tuner.OnScopeChanged -= Refresh;
        }

        public void Toggle()
        {
            if (_tuner != null) _tuner.ToggleScope();
        }

        private void Refresh(bool all)
        {
            if (_label != null) _label.text = all ? "Applies to: all" : "Applies to: last placed";
        }
    }
}
