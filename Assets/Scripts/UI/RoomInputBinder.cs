using Interaction;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace UI
{
    /// <summary>
    /// Connects the panel's "Room" text box to the app: the typed room name becomes
    /// RoomLoader.Room (what "Load room" loads) and PointSelectController.ProjectName (what new
    /// captures are named), is remembered on the headset between runs, and is restricted to the
    /// characters the server accepts. Also shows the room loader's progress text and disables the
    /// Load button while a load is running.
    /// </summary>
    public class RoomInputBinder : MonoBehaviour
    {
        private const string PrefsKey = "pipeline.room";

        [SerializeField] private TMP_InputField _field;
        [SerializeField] private TMP_Text _status;
        [SerializeField] private Button _loadButton;
        [SerializeField] private RoomLoader _loader;
        [SerializeField] private PointSelectController _controller;

        public void Configure(TMP_InputField field, TMP_Text status, Button loadButton,
                              RoomLoader loader, PointSelectController controller)
        {
            _field = field;
            _status = status;
            _loadButton = loadButton;
            _loader = loader;
            _controller = controller;

            // AddComponent already ran OnEnable with no fields set; hook up now (play mode only).
            if (Application.isPlaying && isActiveAndEnabled) { OnDisable(); OnEnable(); }
        }

        private void Awake()
        {
            if (_loader == null) _loader = FindAnyObjectByType<RoomLoader>();
            if (_controller == null) _controller = FindAnyObjectByType<PointSelectController>();
        }

        private void Start()
        {
            if (_field == null || _loader == null) return;

            string initial = Sanitize(PlayerPrefs.GetString(PrefsKey, ""));
            if (string.IsNullOrEmpty(initial)) initial = Sanitize(_loader.Room);
            if (string.IsNullOrEmpty(initial)) initial = "ToyodaLab";
            Apply(initial);
            _field.SetTextWithoutNotify(initial);
        }

        private void OnEnable()
        {
            if (_field != null)
            {
                _field.onEndEdit.AddListener(OnEdited);
                _field.onDeselect.AddListener(OnEdited);
                _field.onValueChanged.AddListener(OnTyping);
            }
            if (_loader == null) _loader = FindAnyObjectByType<RoomLoader>();
            if (_loader != null) _loader.OnStatusChanged += OnLoaderStatus;
        }

        private void OnDisable()
        {
            if (_field != null)
            {
                _field.onEndEdit.RemoveListener(OnEdited);
                _field.onDeselect.RemoveListener(OnEdited);
                _field.onValueChanged.RemoveListener(OnTyping);
            }
            if (_loader != null) _loader.OnStatusChanged -= OnLoaderStatus;
        }

        private void Update()
        {
            // Lock Load while a load runs (a second press would be ignored anyway).
            if (_loader != null && _loadButton != null && _loadButton.interactable == _loader.IsLoading)
                _loadButton.interactable = !_loader.IsLoading;
        }

        private void OnLoaderStatus(string message)
        {
            if (_status != null) _status.text = message;
        }

        // Live: keep the loader in sync as the system keyboard mirrors text into the field.
        private void OnTyping(string text)
        {
            string clean = Sanitize(text);
            if (!string.IsNullOrEmpty(clean)) Apply(clean);
        }

        private void OnEdited(string text)
        {
            string clean = Sanitize(text);
            if (string.IsNullOrEmpty(clean)) return; // keep the previous valid room
            if (clean != text) _field.SetTextWithoutNotify(clean);
            Apply(clean);
            PlayerPrefs.SetString(PrefsKey, clean);
            PlayerPrefs.Save();
            if (_status != null) _status.text = $"Room set to '{clean}'.";
        }

        private void Apply(string room)
        {
            if (_loader != null) _loader.Room = room;
            // Captures are stored as <base>_<timestamp>; keep new captures in the same room.
            if (_controller != null) _controller.ProjectName = room;
        }

        /// <summary>Server-side project names allow only letters, digits, '-' and '_'.</summary>
        private static string Sanitize(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new System.Text.StringBuilder(s.Length);
            foreach (char c in s.Trim())
                if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_' || c == '-')
                    sb.Append(c);
            return sb.Length > 80 ? sb.ToString(0, 80) : sb.ToString();
        }
    }
}
