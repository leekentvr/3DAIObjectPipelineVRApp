using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;

namespace UI
{
    /// <summary>
    /// Makes a TMP_InputField usable with the Quest system keyboard.
    ///
    /// TMP's built-in mobile keyboard support re-assigns the field's text on every keystroke
    /// and resets the caret to the start, and the system keyboard overlay pauses the app so the
    /// field can't be re-clicked to move it -- typed text ends up at the front of the box.
    /// Instead this opens the keyboard itself via TouchScreenKeyboard, pre-filled with the
    /// field's current text (so the OS keyboard's own caret handles editing), and copies the
    /// result back into the field when the keyboard is dismissed. TMP's own soft keyboard is
    /// disabled so the two don't fight.
    ///
    /// Requires OVR Manager > "Requires System Keyboard" (see docs/unity-setup.md). In the
    /// Editor / on platforms without a touch keyboard this does nothing and the field behaves
    /// normally.
    /// </summary>
    [RequireComponent(typeof(TMP_InputField))]
    public class VrTextInput : MonoBehaviour, IPointerClickHandler
    {
        [SerializeField] private TouchScreenKeyboardType _keyboardType = TouchScreenKeyboardType.URL;
        [SerializeField] private string _placeholder = "";

        private TMP_InputField _field;
        private TouchScreenKeyboard _keyboard;
        private string _textBeforeEdit;

        private void Awake()
        {
            _field = GetComponent<TMP_InputField>();
            if (TouchScreenKeyboard.isSupported)
            {
                _field.shouldHideSoftKeyboard = true;
                _field.shouldHideMobileInput = true;
            }
        }

        private void OnEnable() => _field.onSelect.AddListener(OnFieldSelected);

        private void OnDisable()
        {
            _field.onSelect.RemoveListener(OnFieldSelected);
            CloseKeyboard();
        }

        public void OnPointerClick(PointerEventData eventData) => OpenKeyboard();

        private void OnFieldSelected(string _) => OpenKeyboard();

        private void OpenKeyboard()
        {
            if (!TouchScreenKeyboard.isSupported) return;
            if (_keyboard != null && _keyboard.active) return;

            _textBeforeEdit = _field.text;
            _keyboard = TouchScreenKeyboard.Open(
                _textBeforeEdit, _keyboardType, false, false, false, false, _placeholder);
        }

        private void CloseKeyboard()
        {
            if (_keyboard != null) _keyboard.active = false;
            _keyboard = null;
        }

        private void Update()
        {
            if (_keyboard == null) return;

            switch (_keyboard.status)
            {
                case TouchScreenKeyboard.Status.Visible:
                    // Mirror what's being typed so the field shows it live.
                    if (_keyboard.text != _field.text) _field.text = _keyboard.text;
                    break;

                case TouchScreenKeyboard.Status.Done:
                case TouchScreenKeyboard.Status.LostFocus:
                    _field.text = _keyboard.text;
                    Finish();
                    break;

                case TouchScreenKeyboard.Status.Canceled:
                    _field.text = _textBeforeEdit;
                    Finish();
                    break;
            }
        }

        private void Finish()
        {
            _keyboard = null;
            _field.DeactivateInputField();
        }
    }
}
