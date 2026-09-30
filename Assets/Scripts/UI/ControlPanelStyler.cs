using Interaction;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace UI
{
    /// <summary>
    /// Restyles and re-lays-out the in-VR control panel at startup: dark card, section headers,
    /// consistent rounded buttons/inputs, and a new "Room" text box next to "Load room".
    ///
    /// It REUSES the scene's existing controls (btnSave, btnTest, btnLoadRoom, btnLODup/down,
    /// txtServerIP, txtStatus, txtLODStatus, ProcessingOverlay) -- so all their OnClick /
    /// serialized wiring keeps working -- and only changes their transforms and looks. The
    /// canvas itself is sized in the scene (480 x 600 units at 0.0008 scale, with the
    /// Interaction SDK bounds clipper matched to it).
    ///
    /// Installed automatically after the scene loads; nothing to wire. If a named control is
    /// missing it is skipped rather than failing.
    /// </summary>
    public static class ControlPanelStyler
    {
        // Palette
        private static readonly Color Border = new Color(0.20f, 0.26f, 0.37f, 1f);
        private static readonly Color Card = new Color(0.07f, 0.085f, 0.12f, 0.98f);
        private static readonly Color Field = new Color(0.12f, 0.145f, 0.20f, 1f);
        private static readonly Color Accent = new Color(0.20f, 0.49f, 0.96f, 1f);
        private static readonly Color Secondary = new Color(0.19f, 0.24f, 0.33f, 1f);
        private static readonly Color TextPrimary = new Color(0.94f, 0.96f, 0.99f, 1f);
        private static readonly Color TextMuted = new Color(0.58f, 0.66f, 0.78f, 1f);
        private static readonly Color Divider = new Color(1f, 1f, 1f, 0.09f);

        private const float W = 480f;
        private const float Pad = 24f;
        private const float Inner = W - 2f * Pad;

        private static Sprite s_rounded;
        private static TMP_FontAsset s_font;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Install()
        {
            var panel = Object.FindAnyObjectByType<PipelineConfigPanel>(FindObjectsInactive.Include);
            if (panel == null) return;
            Canvas canvas = panel.GetComponentInParent<Canvas>(true);
            if (canvas == null) return;
            var root = canvas.transform as RectTransform;
            if (root == null) return;

            // Safety net even for an already-baked scene: every control must carry a UiHoverTracker so
            // clicking the panel never also fires a capture (see EnsureHoverTracker).
            foreach (Selectable sel in root.GetComponentsInChildren<Selectable>(true))
                EnsureHoverTracker(sel.gameObject);
            Transform existingBorder = root.Find("ControlPanelBorder");
            if (existingBorder != null) EnsureHoverTracker(existingBorder.gameObject);

            if (root.Find("ControlPanelBackground") != null) return; // already styled

            try
            {
                Build(root, null);
            }
            catch (System.Exception e)
            {
                // Never let cosmetic work break the app; the original controls still function.
                Debug.LogWarning($"[ControlPanelStyler] Restyle failed, leaving the panel as-is: {e}");
            }
        }

        /// <summary>Restyles/lays out the panel under <paramref name="root"/> (the world-space canvas).
        /// Called at runtime by Install, and in the editor by ControlPanelBaker (which passes a saved
        /// rounded-corner sprite so the result can be stored in the scene).</summary>
        public static void Build(RectTransform root, Sprite rounded)
        {
            var btnSave = Find<Button>(root, "btnSave");
            var btnTest = Find<Button>(root, "btnTest");
            var btnLoad = Find<Button>(root, "btnLoadRoom");
            var btnLodA = Find<Button>(root, "btnLODup");
            var btnLodB = Find<Button>(root, "btnLODdown");
            var hostField = Find<TMP_InputField>(root, "txtServerIP");
            var txtStatus = Find<TMP_Text>(root, "txtStatus");
            var txtLod = Find<TMP_Text>(root, "txtLODStatus");
            var overlay = root.Find("ProcessingOverlay") as RectTransform;

            s_font = (txtStatus != null ? txtStatus.font : null) ?? TMP_Settings.defaultFontAsset;
            s_rounded = rounded != null ? rounded : MakeRoundedSprite();

            // ---- background: 2-unit border, then the card
            var border = MakeImage(root, "ControlPanelBorder", Border, sliced: true);
            Stretch(border.rectTransform, 0f);
            border.transform.SetAsFirstSibling();
            border.raycastTarget = true;
            var card = MakeImage(root, "ControlPanelBackground", Card, sliced: true);
            Stretch(card.rectTransform, 2f);
            card.transform.SetSiblingIndex(1);
            card.raycastTarget = false;

            // ---- header (title bar: stays visible when minimised)
            Label(root, "Title", "Pipeline Control", 28, FontStyles.Bold, TextPrimary, Pad, 18, 250, 44);
            Button recenterBtn = MakeButton(root, "btnRecenter", "Recenter", Secondary, W - Pad - 44f - 8f - 104f, 20, 104, 40, 18);
            Button minimiseBtn = MakeButton(root, "btnMinimise", "–", Secondary, W - Pad - 44f, 20, 44, 40, 28);
            Rule(root, "Rule0", 70);

            // ---- server section
            Label(root, "SecServer", "SERVER", 16, FontStyles.Bold, TextMuted, Pad, 84, Inner, 22);
            if (hostField != null) StyleField(hostField, "Server IP / host", Pad, 110, Inner, 52);
            if (btnSave != null) StyleButton(btnSave, "Save", Accent, Pad, 172, (Inner - 12f) / 2f, 48);
            if (btnTest != null) StyleButton(btnTest, "Test connection", Secondary, Pad + (Inner - 12f) / 2f + 12f, 172, (Inner - 12f) / 2f, 48);
            if (txtStatus != null)
            {
                Place(txtStatus.rectTransform, Pad, 226, Inner, 40);
                StyleText(txtStatus, 16, FontStyles.Normal, TextMuted, TextAlignmentOptions.TopLeft);
            }
            Rule(root, "Rule1", 278);

            // ---- room section
            Label(root, "SecRoom", "ROOM", 16, FontStyles.Bold, TextMuted, Pad, 292, Inner, 22);
            TMP_InputField roomField = null;
            if (hostField != null)
            {
                roomField = Object.Instantiate(hostField, root);
                roomField.name = "txtRoom";
                if (roomField.GetComponent<VrTextInput>() == null) roomField.gameObject.AddComponent<VrTextInput>();
                StyleField(roomField, "Room name, e.g. ToyodaLab2", Pad, 318, Inner, 52);
            }
            if (btnLoad != null) StyleButton(btnLoad, "Load room", Accent, Pad, 378, Inner, 52);
            var roomStatus = Label(root, "RoomStatus", "Loads everything at the lowest detail first.", 16,
                                   FontStyles.Normal, TextMuted, Pad, 436, Inner, 26);
            Rule(root, "Rule2", 474);

            // ---- level of detail
            Label(root, "SecLod", "LEVEL OF DETAIL", 16, FontStyles.Bold, TextMuted, Pad, 490, 200, 22);
            Button scopeBtn = MakeButton(root, "btnLodScope", "Applies to: all", Secondary, W - Pad - 236f, 482, 236, 34, 16);
            // Coarser on the left, Finer on the right, whichever scene button is wired to which.
            Button coarser = null, finer = null;
            foreach (Button b in new[] { btnLodA, btnLodB })
            {
                if (b == null) continue;
                string method = b.onClick.GetPersistentEventCount() > 0 ? b.onClick.GetPersistentMethodName(0) : "";
                if (method == "Coarser") coarser = b; else if (method == "Finer") finer = b;
            }
            if (coarser == null || finer == null) { coarser = btnLodB; finer = btnLodA; }
            float lodW = 130f;
            if (coarser != null) StyleButton(coarser, "−  Coarser", Secondary, Pad, 526, lodW, 52);
            if (finer != null) StyleButton(finer, "Finer  +", Secondary, W - Pad - lodW, 526, lodW, 52);
            if (txtLod != null)
            {
                Place(txtLod.rectTransform, Pad + lodW + 8f, 526, Inner - 2f * lodW - 16f, 52);
                StyleText(txtLod, 18, FontStyles.Bold, TextPrimary, TextAlignmentOptions.Center);
            }

            // ---- processing overlay: dim the whole card while a capture is being processed
            if (overlay != null)
            {
                Stretch(overlay, 0f);
                var dim = MakeImage(overlay, "Dim", new Color(0.02f, 0.03f, 0.05f, 0.82f), sliced: true);
                Stretch(dim.rectTransform, 2f);
                dim.raycastTarget = false;
                dim.transform.SetAsFirstSibling();
                overlay.SetAsLastSibling();
            }

            // ---- room text box + LoD scope toggle: real components so the scene keeps the wiring
            if (roomField != null)
            {
                var binder = root.gameObject.AddComponent<RoomInputBinder>();
                binder.Configure(roomField, roomStatus, btnLoad,
                                 Object.FindAnyObjectByType<RoomLoader>(),
                                 Object.FindAnyObjectByType<PointSelectController>());
            }
            var scopeComp = root.gameObject.AddComponent<LodScopeButton>();
            scopeComp.Configure(Object.FindAnyObjectByType<LodTuner>(), scopeBtn.GetComponentInChildren<TMP_Text>(), scopeBtn);
            AddClick(scopeBtn, scopeComp.Toggle);

            // ---- window behaviour: everything except the background, title bar and overlay goes in a
            // Body container that minimising hides.
            var bodyGo = new GameObject("Body", typeof(RectTransform));
            var body = (RectTransform)bodyGo.transform;
            body.SetParent(root, false);
            Stretch(body, 0f);
            var keep = new System.Collections.Generic.HashSet<Transform>
            {
                border.transform, card.transform, body,
                root.Find("Title"), root.Find("Rule0"),
                root.Find("Surface"), // Interaction SDK surface: must stay active or pointer input dies
                recenterBtn.transform, minimiseBtn.transform, overlay,
            };
            var toMove = new System.Collections.Generic.List<Transform>();
            foreach (Transform child in root)
                if (!keep.Contains(child)) toMove.Add(child);
            foreach (Transform child in toMove) child.SetParent(body, false);

            body.SetSiblingIndex(2);
            if (overlay != null) overlay.SetAsLastSibling();

            CanvasGroup overlayGroup = null;
            if (overlay != null)
            {
                overlayGroup = overlay.GetComponent<CanvasGroup>();
                if (overlayGroup == null) overlayGroup = overlay.gameObject.AddComponent<CanvasGroup>();
            }

            var window = root.gameObject.GetComponent<ControlPanelWindow>();
            if (window == null) window = root.gameObject.AddComponent<ControlPanelWindow>();
            window.Configure(root, bodyGo, overlayGroup, minimiseBtn.GetComponentInChildren<TMP_Text>(),
                             W, root.sizeDelta.y, 78f);
            AddClick(minimiseBtn, window.ToggleMinimised);
            AddClick(recenterBtn, window.Recenter);

            // ControllerSelectTrigger ignores trigger presses while the ray is over a tracked UI
            // element (UiHoverTracker). Every control -- including the ones created here -- and
            // the card itself must carry one, or clicking the panel also starts a capture.
            foreach (Selectable sel in root.GetComponentsInChildren<Selectable>(true))
                EnsureHoverTracker(sel.gameObject);
            EnsureHoverTracker(border.gameObject);
        }

        private static void EnsureHoverTracker(GameObject go)
        {
            if (go.GetComponent<UiHoverTracker>() == null) go.AddComponent<UiHoverTracker>();
        }

        /// <summary>Button click hookup. In the editor (outside Play mode) it adds a PERSISTENT listener so
        /// the wiring is saved in the scene; otherwise a normal runtime listener.</summary>
        private static void AddClick(Button b, UnityEngine.Events.UnityAction action)
        {
#if UNITY_EDITOR
            if (!Application.isPlaying)
            {
                UnityEditor.Events.UnityEventTools.AddPersistentListener(b.onClick, action);
                return;
            }
#endif
            b.onClick.AddListener(action);
        }

        // ------------------------------------------------------------------ styling helpers

        private static T Find<T>(RectTransform root, string name) where T : Component
        {
            Transform t = root.Find(name);
            return t != null ? t.GetComponent<T>() : null;
        }

        private static void Place(RectTransform rt, float x, float y, float w, float h)
        {
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.sizeDelta = new Vector2(w, h);
            rt.anchoredPosition = new Vector2(x, -y);
            rt.localScale = Vector3.one;
            rt.localRotation = Quaternion.identity;
            var p = rt.localPosition; p.z = 0f; rt.localPosition = p;
        }

        private static void Stretch(RectTransform rt, float inset)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.offsetMin = new Vector2(inset, inset);
            rt.offsetMax = new Vector2(-inset, -inset);
            rt.localScale = Vector3.one;
            var p = rt.localPosition; p.z = 0f; rt.localPosition = p;
        }

        private static Image MakeImage(Transform parent, string name, Color color, bool sliced)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            go.transform.SetParent(parent, false);
            var img = go.GetComponent<Image>();
            img.color = color;
            if (sliced)
            {
                img.sprite = s_rounded;
                img.type = Image.Type.Sliced;
            }
            return img;
        }

        private static TMP_Text Label(Transform parent, string name, string text, float size, FontStyles style,
                                      Color color, float x, float y, float w, float h)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
            go.transform.SetParent(parent, false);
            var t = go.GetComponent<TextMeshProUGUI>();
            t.text = text;
            t.raycastTarget = false;
            StyleText(t, size, style, color, TextAlignmentOptions.TopLeft);
            Place(t.rectTransform, x, y, w, h);
            return t;
        }

        private static void Rule(Transform parent, string name, float y)
        {
            var img = MakeImage(parent, name, Divider, sliced: false);
            img.raycastTarget = false;
            Place(img.rectTransform, Pad, y, Inner, 1.5f);
        }

        private static void StyleText(TMP_Text t, float size, FontStyles style, Color color, TextAlignmentOptions align)
        {
            if (s_font != null) t.font = s_font;
            t.fontSize = size;
            t.enableAutoSizing = false;
            t.fontStyle = style;
            t.color = color;
            t.alignment = align;
            t.textWrappingMode = TextWrappingModes.Normal;
            t.overflowMode = TextOverflowModes.Ellipsis;
        }

        private static Button MakeButton(Transform parent, string name, string text, Color color,
                                         float x, float y, float w, float h, float fontSize)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Button));
            go.transform.SetParent(parent, false);
            var textGo = new GameObject("Text (TMP)", typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
            textGo.transform.SetParent(go.transform, false);
            textGo.GetComponent<TextMeshProUGUI>().raycastTarget = false;
            var btn = go.GetComponent<Button>();
            btn.targetGraphic = go.GetComponent<Image>();
            StyleButton(btn, text, color, x, y, w, h, fontSize);
            return btn;
        }

        private static void StyleButton(Button b, string text, Color color, float x, float y, float w, float h,
                                        float fontSize = 22f)
        {
            Place((RectTransform)b.transform, x, y, w, h);

            var img = b.GetComponent<Image>();
            if (img != null)
            {
                img.sprite = s_rounded;
                img.type = Image.Type.Sliced;
                img.color = Color.white; // tint comes from the ColorBlock below
                b.targetGraphic = img;
            }

            b.transition = Selectable.Transition.ColorTint;
            var cb = b.colors;
            cb.normalColor = color;
            cb.highlightedColor = Color.Lerp(color, Color.white, 0.18f);
            cb.pressedColor = Color.Lerp(color, Color.black, 0.25f);
            cb.selectedColor = color;
            cb.disabledColor = new Color(color.r * 0.5f, color.g * 0.5f, color.b * 0.5f, 0.6f);
            cb.colorMultiplier = 1f;
            cb.fadeDuration = 0.08f;
            b.colors = cb;

            var t = b.GetComponentInChildren<TMP_Text>();
            if (t != null)
            {
                t.text = text;
                StyleText(t, fontSize, FontStyles.Bold, TextPrimary, TextAlignmentOptions.Center);
                Stretch(t.rectTransform, 6f);
                t.raycastTarget = false;
            }
        }

        private static void StyleField(TMP_InputField f, string placeholder, float x, float y, float w, float h)
        {
            Place((RectTransform)f.transform, x, y, w, h);

            var img = f.GetComponent<Image>();
            if (img != null)
            {
                img.sprite = s_rounded;
                img.type = Image.Type.Sliced;
                img.color = Field;
            }

            if (f.textViewport != null) { Stretch(f.textViewport, 0f); f.textViewport.offsetMin = new Vector2(16f, 6f); f.textViewport.offsetMax = new Vector2(-16f, -6f); }
            if (f.textComponent != null)
            {
                StyleText(f.textComponent, 24, FontStyles.Normal, TextPrimary, TextAlignmentOptions.MidlineLeft);
                f.textComponent.overflowMode = TextOverflowModes.Masking;
                f.textComponent.textWrappingMode = TextWrappingModes.NoWrap;
            }
            if (f.placeholder is TMP_Text ph)
            {
                ph.text = placeholder;
                StyleText(ph, 24, FontStyles.Italic, new Color(TextMuted.r, TextMuted.g, TextMuted.b, 0.7f), TextAlignmentOptions.MidlineLeft);
                ph.textWrappingMode = TextWrappingModes.NoWrap;
            }
            f.caretColor = TextPrimary;
            f.customCaretColor = true;
            f.caretWidth = 3;
            f.selectionColor = new Color(Accent.r, Accent.g, Accent.b, 0.4f);
            f.pointSize = 24;
        }

        public const int RoundedSize = 64;
        public const float RoundedRadius = 16f;

        /// <summary>Procedural anti-aliased rounded rectangle, 9-sliced by Image (radius ~ 16 canvas units).</summary>
        public static Texture2D MakeRoundedTexture()
        {
            const int size = RoundedSize;
            const float radius = RoundedRadius;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };
            var px = new Color32[size * size];
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                // distance outside the rounded rect's inner box (0 inside)
                float cx = Mathf.Clamp(x + 0.5f, radius, size - radius);
                float cy = Mathf.Clamp(y + 0.5f, radius, size - radius);
                float d = Mathf.Sqrt((x + 0.5f - cx) * (x + 0.5f - cx) + (y + 0.5f - cy) * (y + 0.5f - cy));
                float a = Mathf.Clamp01(radius - d + 0.5f);
                px[y * size + x] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(a * 255f));
            }
            tex.SetPixels32(px);
            tex.Apply(false, false);
            return tex;
        }

        // Runtime fallback when no saved sprite asset is supplied.
        private static Sprite MakeRoundedSprite()
        {
            Texture2D tex = MakeRoundedTexture();
            tex.hideFlags = HideFlags.HideAndDontSave;
            float r = RoundedRadius;
            var sprite = Sprite.Create(tex, new Rect(0, 0, RoundedSize, RoundedSize), new Vector2(0.5f, 0.5f), 100f, 0,
                                       SpriteMeshType.FullRect, new Vector4(r, r, r, r));
            sprite.hideFlags = HideFlags.HideAndDontSave;
            return sprite;
        }
    }
}
