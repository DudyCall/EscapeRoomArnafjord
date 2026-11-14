using System;
using Arnafjordur.Characters;
using Arnafjordur.Core;
using UnityEngine;
using UnityEngine.UI;

namespace Arnafjordur.UI
{
    /// <summary>
    /// Minimal coastal-palette HUD for switching active characters, toggling companion Follow/Roam modes,
    /// and showing controls in the flat movement test scene.
    /// </summary>
    public sealed class CharacterTestHud : MonoBehaviour
    {
        [SerializeField] private PartyController party;

        private Button[] _characterButtons = Array.Empty<Button>();
        private Image[] _characterBackgrounds = Array.Empty<Image>();
        private Text[] _characterLabels = Array.Empty<Text>();
        private Image _followBackground;
        private Text _followLabel;
        private Image _roamBackground;
        private Text _roamLabel;
        private Image _viewBackground;
        private Text _viewLabel;
        private Text _statusLabel;
        private Font _uiFont;
        private bool _built;

        /// <summary>Assigns the party controller driving this HUD.</summary>
        public void Configure(PartyController controller)
        {
            if (party != null)
            {
                party.StateChanged -= RefreshView;
            }

            party = controller;
            if (party != null && isActiveAndEnabled)
            {
                party.StateChanged += RefreshView;
                RefreshView();
            }
        }

        private void OnEnable()
        {
            if (party != null)
            {
                party.StateChanged += RefreshView;
            }
        }

        private void OnDisable()
        {
            if (party != null)
            {
                party.StateChanged -= RefreshView;
            }
        }

        private void Start()
        {
            if (party == null)
            {
                party = FindAnyObjectByType<PartyController>();
            }

            BuildUiIfNeeded();
            if (party != null)
            {
                party.StateChanged -= RefreshView;
                party.StateChanged += RefreshView;
            }

            RefreshView();
        }

        private void BuildUiIfNeeded()
        {
            if (_built)
            {
                return;
            }

            _uiFont = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

            GameObject canvasGo = new GameObject("CharacterTestHud_Canvas", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvasGo.transform.SetParent(transform, false);

            Canvas canvas = canvasGo.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 10;

            CanvasScaler scaler = canvasGo.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;

            RectTransform root = canvasGo.GetComponent<RectTransform>();
            BuildCharacterBar(root);
            BuildCompanionBar(root);
            BuildControlsFooter(root);

            _built = true;
        }

        private void BuildCharacterBar(RectTransform root)
        {
            GameObject bar = CreatePanel("CharacterBar", root, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(20f, -20f), new Vector2(468f, 54f), Palette.UiPanelSlate);
            HorizontalLayoutGroup layout = bar.AddComponent<HorizontalLayoutGroup>();
            layout.padding = new RectOffset(8, 8, 8, 8);
            layout.spacing = 8f;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = true;

            int count = party != null && party.Characters != null && party.Characters.Length > 0 ? party.Characters.Length : 3;
            _characterButtons = new Button[count];
            _characterBackgrounds = new Image[count];
            _characterLabels = new Text[count];

            string[] fallbackNames = { "Olavur", "Leivur", "Andrias" };
            for (int i = 0; i < count; i++)
            {
                int capturedIndex = i;
                string charName = party != null && party.Characters != null && i < party.Characters.Length && party.Characters[i] != null
                    ? party.Characters[i].DisplayName
                    : (i < fallbackNames.Length ? fallbackNames[i] : $"Hero {i + 1}");

                Button btn = CreateButton($"Btn_{charName}", bar.transform, $"[{i + 1}] {charName}", out Image bg, out Text lbl);
                btn.onClick.AddListener(() => party?.SelectCharacter(capturedIndex));
                _characterButtons[i] = btn;
                _characterBackgrounds[i] = bg;
                _characterLabels[i] = lbl;
            }
        }

        private void BuildCompanionBar(RectTransform root)
        {
            GameObject bar = CreatePanel("CompanionBar", root, new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-20f, -20f), new Vector2(456f, 54f), Palette.UiPanelSlate);
            HorizontalLayoutGroup layout = bar.AddComponent<HorizontalLayoutGroup>();
            layout.padding = new RectOffset(8, 8, 8, 8);
            layout.spacing = 8f;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = true;

            Button followBtn = CreateButton("Btn_Follow", bar.transform, "Follow [F]", out _followBackground, out _followLabel);
            followBtn.onClick.AddListener(() => party?.ToggleFollow());

            Button roamBtn = CreateButton("Btn_Roam", bar.transform, "Roam [R]", out _roamBackground, out _roamLabel);
            roamBtn.onClick.AddListener(() => party?.ToggleRoam());

            Button viewBtn = CreateButton("Btn_FirstPerson", bar.transform, "1st Person [V]", out _viewBackground, out _viewLabel);
            viewBtn.onClick.AddListener(() => party?.ToggleFirstPerson());
        }

        private void BuildControlsFooter(RectTransform root)
        {
            GameObject footer = CreatePanel("ControlsFooter", root, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 18f), new Vector2(1180f, 54f), Palette.UiPanelSlate);

            _statusLabel = CreateText("ControlsText", footer.transform, 16, TextAnchor.MiddleCenter, Palette.UiTextParchment);
            RectTransform textRect = _statusLabel.rectTransform;
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = new Vector2(14f, 4f);
            textRect.offsetMax = new Vector2(-14f, -4f);
        }

        private void RefreshView()
        {
            if (!_built)
            {
                return;
            }

            int activeIdx = party != null ? party.ActiveIndex : 0;
            for (int i = 0; i < _characterButtons.Length; i++)
            {
                bool isActive = i == activeIdx;
                _characterBackgrounds[i].color = isActive ? Palette.UiAccentAmber : Palette.WetBasalt;
                _characterLabels[i].color = isActive ? Palette.DeepFjordSlate : Palette.UiTextParchment;
                _characterLabels[i].fontStyle = isActive ? FontStyle.Bold : FontStyle.Normal;
            }

            PartyController.CompanionMode mode = party != null ? party.CurrentCompanionMode : PartyController.CompanionMode.Stay;
            bool followOn = mode == PartyController.CompanionMode.Follow;
            bool roamOn = mode == PartyController.CompanionMode.Roam;
            bool firstPerson = party != null && party.IsFirstPerson;

            _followBackground.color = followOn ? Palette.UiConfirmGreen : Palette.WetBasalt;
            _followLabel.color = followOn ? Palette.DeepFjordSlate : Palette.UiTextParchment;
            _followLabel.fontStyle = followOn ? FontStyle.Bold : FontStyle.Normal;

            _roamBackground.color = roamOn ? Palette.UiConfirmGreen : Palette.WetBasalt;
            _roamLabel.color = roamOn ? Palette.DeepFjordSlate : Palette.UiTextParchment;
            _roamLabel.fontStyle = roamOn ? FontStyle.Bold : FontStyle.Normal;

            _viewBackground.color = firstPerson ? Palette.UiConfirmGreen : Palette.WetBasalt;
            _viewLabel.color = firstPerson ? Palette.DeepFjordSlate : Palette.UiTextParchment;
            _viewLabel.fontStyle = firstPerson ? FontStyle.Bold : FontStyle.Normal;

            string activeName = party != null && party.ActiveCharacter != null ? party.ActiveCharacter.DisplayName : "None";
            string viewMode = firstPerson ? "1st Person" : "3rd Person";
            _statusLabel.text =
                $"Active: {activeName}  |  View: {viewMode}  |  Companions: {mode}   —   " +
                "WASD/Arrows: Move  •  Shift: Run  •  Click: Move  •  Right-Drag/Q-E: Look  •  Scroll: Zoom  •  V: 1st/3rd  •  1-3/Tab: Switch  •  F/R: Follow/Roam";
        }

        private GameObject CreatePanel(string name, RectTransform parent, Vector2 anchor, Vector2 pivot, Vector2 anchoredPos, Vector2 size, Color color)
        {
            GameObject go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            RectTransform rt = go.GetComponent<RectTransform>();
            rt.SetParent(parent, false);
            rt.anchorMin = anchor;
            rt.anchorMax = anchor;
            rt.pivot = pivot;
            rt.anchoredPosition = anchoredPos;
            rt.sizeDelta = size;

            Image img = go.GetComponent<Image>();
            img.color = color;
            return go;
        }

        private Button CreateButton(string name, Transform parent, string caption, out Image background, out Text label)
        {
            GameObject go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Button));
            go.transform.SetParent(parent, false);

            background = go.GetComponent<Image>();
            background.color = Palette.WetBasalt;

            Button button = go.GetComponent<Button>();
            ColorBlock colors = button.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color(1.08f, 1.08f, 1.08f, 1f);
            colors.pressedColor = new Color(0.86f, 0.86f, 0.86f, 1f);
            colors.selectedColor = Color.white;
            button.colors = colors;

            label = CreateText($"{name}_Label", go.transform, 17, TextAnchor.MiddleCenter, Palette.UiTextParchment);
            label.text = caption;
            RectTransform rt = label.rectTransform;
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;

            return button;
        }

        private Text CreateText(string name, Transform parent, int fontSize, TextAnchor alignment, Color color)
        {
            GameObject go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            go.transform.SetParent(parent, false);

            Text text = go.GetComponent<Text>();
            text.font = _uiFont;
            text.fontSize = fontSize;
            text.alignment = alignment;
            text.color = color;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Truncate;
            text.raycastTarget = false;
            return text;
        }
    }
}
