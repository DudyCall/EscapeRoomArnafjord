using Arnafjordur.Characters;
using Arnafjordur.Core;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace Arnafjordur.UI
{
    /// <summary>
    /// Displays a readable note card on screen when a character uses a <see cref="ClueNote"/> (presses U / Space).
    /// Auto-closes when the note is put away, dropped, or the player switches characters.
    /// </summary>
    public sealed class ClueNoteUi : MonoBehaviour
    {
        private static ClueNoteUi s_instance;

        private PartyController _party;
        private ClueNote _activeNote;
        private CharacterItemHolder _activeHolder;
        private Font _font;
        private GameObject _panelRoot;
        private Text _titleLabel;
        private Text _bodyLabel;
        private bool _built;

        /// <summary>True while a note is currently displayed on screen.</summary>
        public static bool IsAnyOpen => s_instance != null && s_instance._activeNote != null && s_instance._panelRoot != null && s_instance._panelRoot.activeSelf;

        /// <summary>True when <paramref name="note"/> is the currently displayed note.</summary>
        public static bool IsShowing(ClueNote note) => note != null && IsAnyOpen && s_instance._activeNote == note;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStaticInstance()
        {
            s_instance = null;
        }

        /// <summary>Toggles the reading card open or closed for <paramref name="note"/> held by <paramref name="holder"/>.</summary>
        public static void Toggle(ClueNote note, CharacterItemHolder holder)
        {
            if (note == null) return;
            ClueNoteUi ui = EnsureInstance();
            if (ui._activeNote == note && ui._panelRoot != null && ui._panelRoot.activeSelf)
            {
                ui.Hide();
                return;
            }

            ui.Show(note, holder);
        }

        /// <summary>Closes any currently displayed note card.</summary>
        public static void Close()
        {
            if (s_instance != null)
            {
                s_instance.Hide();
            }
        }

        private static ClueNoteUi EnsureInstance()
        {
            if (s_instance != null) return s_instance;
            ClueNoteUi existing = FindAnyObjectByType<ClueNoteUi>();
            if (existing != null)
            {
                s_instance = existing;
                return existing;
            }

            var go = new GameObject("ClueNoteUi");
            s_instance = go.AddComponent<ClueNoteUi>();
            return s_instance;
        }

        private void Awake()
        {
            if (s_instance == null) s_instance = this;
            _party = FindAnyObjectByType<PartyController>();
            BuildUiIfNeeded();
        }

        private void OnDestroy()
        {
            if (s_instance == this) s_instance = null;
        }

        private void Update()
        {
            if (_activeNote == null || _panelRoot == null || !_panelRoot.activeSelf) return;

            if (Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame)
            {
                Hide();
                return;
            }

            if (!_activeNote.gameObject.activeInHierarchy || _activeHolder == null || _activeHolder.HeldItem == null
                || _activeHolder.HeldItem.gameObject != _activeNote.gameObject)
            {
                Hide();
                return;
            }

            if (_party == null) _party = FindAnyObjectByType<PartyController>();
            if (_party != null && _party.ActiveItemHolder != _activeHolder)
            {
                Hide();
            }
        }

        private void Show(ClueNote note, CharacterItemHolder holder)
        {
            BuildUiIfNeeded();
            if (_party == null) _party = FindAnyObjectByType<PartyController>();
            _activeNote = note;
            _activeHolder = holder;
            _titleLabel.text = note.NoteTitle.ToUpperInvariant();
            _bodyLabel.text = $"\"{note.NoteText}\"";
            _panelRoot.SetActive(true);
        }

        private void Hide()
        {
            _activeNote = null;
            _activeHolder = null;
            if (_panelRoot != null) _panelRoot.SetActive(false);
        }

        private void BuildUiIfNeeded()
        {
            if (_built) return;
            _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

            var canvasGo = new GameObject("ClueNoteUi_Canvas", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvasGo.transform.SetParent(transform, false);
            Canvas canvas = canvasGo.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 30;
            CanvasScaler scaler = canvasGo.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;

            var root = (RectTransform)canvasGo.transform;

            _panelRoot = CreateRect("NoteBackdrop", root, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(560f, 270f), Palette.UiPanelSlate);
            RectTransform sheetRt = CreateRect("NotePaper", (RectTransform)_panelRoot.transform, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(536f, 246f), Palette.UiTextParchment).GetComponent<RectTransform>();

            _titleLabel = CreateText("Title", sheetRt, 18, TextAnchor.UpperCenter, Palette.BoathouseRust, FontStyle.Bold);
            SetInset(_titleLabel.rectTransform, 24f, 190f, 24f, 20f);

            _bodyLabel = CreateText("Body", sheetRt, 24, TextAnchor.MiddleCenter, Palette.DeepFjordSlate, FontStyle.Italic);
            SetInset(_bodyLabel.rectTransform, 32f, 64f, 32f, 64f);

            GameObject closeGo = CreateRect("CloseButton", sheetRt, new Vector2(0.5f, 0f), new Vector2(0f, 28f), new Vector2(180f, 38f), Palette.WetBasalt);
            Button closeBtn = closeGo.AddComponent<Button>();
            closeBtn.onClick.AddListener(Hide);
            Text closeTxt = CreateText("CloseLabel", closeGo.GetComponent<RectTransform>(), 15, TextAnchor.MiddleCenter, Palette.UiTextParchment, FontStyle.Bold);
            SetInset(closeTxt.rectTransform, 4f, 4f, 4f, 4f);
            closeTxt.text = "Close [U / Esc]";

            _panelRoot.SetActive(false);
            _built = true;
        }

        private static GameObject CreateRect(string name, RectTransform parent, Vector2 anchor, Vector2 anchoredPos, Vector2 size, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            go.transform.SetParent(parent, false);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = anchor;
            rt.anchoredPosition = anchoredPos;
            rt.sizeDelta = size;
            go.GetComponent<Image>().color = color;
            return go;
        }

        private Text CreateText(string name, RectTransform parent, int size, TextAnchor align, Color color, FontStyle style)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            go.transform.SetParent(parent, false);
            var text = go.GetComponent<Text>();
            text.font = _font;
            text.fontSize = size;
            text.alignment = align;
            text.color = color;
            text.fontStyle = style;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            text.raycastTarget = false;
            return text;
        }

        private static void SetInset(RectTransform rt, float left, float bottom, float right, float top)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(left, bottom);
            rt.offsetMax = new Vector2(-right, -top);
        }
    }
}
