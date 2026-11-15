using System;
using Arnafjordur.Characters;
using Arnafjordur.Core;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace Arnafjordur.UI
{
    /// <summary>
    /// Coastal-palette UI for the washed-up Atom Bomb Sea Mine: mounts a 3D world-space countdown display
    /// directly on the bomb, shows a proximity prompt nearby, and opens the first-person 6-module decipher panel.
    /// </summary>
    public sealed class SeaMineBombUi : MonoBehaviour
    {
        private static readonly string[] QuickLetters =
        {
            "A", "E", "I", "K", "L", "N", "O",
            "R", "S", "T", "U", "Æ", "Ø", "Å",
        };

        [SerializeField] private SeaMineBomb bomb;
        [SerializeField] private PartyController party;

        private Font _uiFont;
        private bool _built;

        // World-space timer mounted directly on the sea mine
        private GameObject _worldTimerCanvasGo;
        private SeaMineBomb _worldTimerBomb;
        private Image _worldTimerBg;
        private Text _worldTimerLabel;

        // Proximity inspect prompt
        private GameObject _inspectPromptRoot;
        private Text _inspectPromptLabel;

        // First-person decipher overlay
        private GameObject _panelRoot;
        private Image _panelHeaderBg;
        private Text _panelTimerLabel;
        private Text _feedbackLabel;

        // Module 1: 4-letter word
        private Image _wordStatusBg;
        private Text _wordStatusLabel;
        private readonly Image[] _slotBgs = new Image[4];
        private readonly Text[] _slotLabels = new Text[4];
        private Button _submitWordButton;
        private Image _submitWordBg;
        private Text _submitWordLabel;

        // Module 2: 10 on/off contacts
        private Image _contactsStatusBg;
        private Text _contactsStatusLabel;
        private readonly Image[] _contactBgs = new Image[SeaMineBomb.ContactCount];
        private readonly Text[] _contactLabels = new Text[SeaMineBomb.ContactCount];
        private Button _submitContactsButton;
        private Image _submitContactsBg;
        private Text _submitContactsLabel;

        // Module 3: wires
        private Image _wiresStatusBg;
        private Text _wiresStatusLabel;
        private readonly UiWire[] _wires = new UiWire[SeaMineBomb.WireCount];
        private Text _wiresHint;

        // Modules 4-6 placeholders
        private readonly Image[] _placeholderStatusBgs = new Image[3];
        private readonly Text[] _placeholderStatusLabels = new Text[3];

        // Footer actions
        private Button _deactivateButton;
        private Image _deactivateBg;
        private Text _deactivateLabel;

        private int _selectedWordSlot;
        private Keyboard _boundKeyboard;
        private SeaMineBomb _subscribedBomb;

        /// <summary>Wires scene references from editor builders or runtime setup.</summary>
        public void Configure(SeaMineBomb seaMine, PartyController partyController = null)
        {
            BindBombEvents(false);
            bomb = seaMine;
            party = partyController;
            if (isActiveAndEnabled)
            {
                BindBombEvents(true);
                RefreshView();
            }
        }

        private void OnEnable()
        {
            ResolveRefs();
            BindBombEvents(true);
            BindKeyboardInput(true);
        }

        private void OnDisable()
        {
            BindBombEvents(false);
            BindKeyboardInput(false);
        }

        private void OnDestroy()
        {
            if (_worldTimerCanvasGo != null)
            {
                Destroy(_worldTimerCanvasGo);
                _worldTimerCanvasGo = null;
                _worldTimerBomb = null;
            }
        }

        private void Start()
        {
            ResolveRefs();
            BuildUiIfNeeded();
            BindBombEvents(true);
            BindKeyboardInput(true);
            RefreshView();
        }

        private void Update()
        {
            ResolveRefs();
            if (!_built)
            {
                BuildUiIfNeeded();
            }

            if (_subscribedBomb != bomb)
            {
                BindBombEvents(true);
            }

            if (_boundKeyboard != Keyboard.current)
            {
                BindKeyboardInput(true);
            }

            HandleSlotArrowKeys();
            RefreshView();
        }

        private void ResolveRefs()
        {
            if (bomb == null)
            {
                bomb = SeaMineBomb.Active != null ? SeaMineBomb.Active : FindAnyObjectByType<SeaMineBomb>();
            }

            if (party == null)
            {
                party = FindAnyObjectByType<PartyController>();
            }
        }

        private void BindBombEvents(bool subscribe)
        {
            if (_subscribedBomb != null)
            {
                _subscribedBomb.StateChanged -= OnBombStateChanged;
                _subscribedBomb = null;
            }

            if (subscribe && bomb != null)
            {
                bomb.StateChanged += OnBombStateChanged;
                _subscribedBomb = bomb;
            }
        }

        private void BindKeyboardInput(bool subscribe)
        {
            if (_boundKeyboard != null)
            {
                _boundKeyboard.onTextInput -= OnTextInput;
                _boundKeyboard = null;
            }

            if (subscribe && Keyboard.current != null)
            {
                Keyboard.current.onTextInput += OnTextInput;
                _boundKeyboard = Keyboard.current;
            }
        }

        private void OnBombStateChanged()
        {
            RefreshView();
        }

        private void HandleSlotArrowKeys()
        {
            if (bomb == null || !bomb.IsActiveCharacterInspecting || bomb.IsWordSolved)
            {
                return;
            }

            Keyboard kb = Keyboard.current;
            if (kb == null)
            {
                return;
            }

            if (kb.leftArrowKey.wasPressedThisFrame)
            {
                _selectedWordSlot = Mathf.Max(0, _selectedWordSlot - 1);
            }
            else if (kb.rightArrowKey.wasPressedThisFrame)
            {
                _selectedWordSlot = Mathf.Min(3, _selectedWordSlot + 1);
            }
            else if (kb.upArrowKey.wasPressedThisFrame)
            {
                bomb.CycleWordLetter(_selectedWordSlot, 1);
            }
            else if (kb.downArrowKey.wasPressedThisFrame)
            {
                bomb.CycleWordLetter(_selectedWordSlot, -1);
            }
        }

        private void OnTextInput(char ch)
        {
            if (bomb == null || !bomb.IsActiveCharacterInspecting || bomb.IsWordSolved || bomb.IsDisarmed || bomb.IsExploded)
            {
                return;
            }

            if (ch == '\b')
            {
                _selectedWordSlot = Mathf.Max(0, _selectedWordSlot - 1);
                bomb.SetWordLetter(_selectedWordSlot, 'A');
                return;
            }

            if (ch == '\r' || ch == '\n')
            {
                bomb.TrySubmitWord();
                return;
            }

            char upper = char.ToUpperInvariant(ch);
            if (SeaMineBomb.Alphabet.IndexOf(upper) >= 0)
            {
                bomb.SetWordLetter(_selectedWordSlot, upper);
                _selectedWordSlot = Mathf.Min(3, _selectedWordSlot + 1);
            }
        }

        private void BuildUiIfNeeded()
        {
            if (_built)
            {
                return;
            }

            _uiFont = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

            GameObject canvasGo = new GameObject(
                "SeaMineBombUi_Canvas",
                typeof(RectTransform),
                typeof(Canvas),
                typeof(CanvasScaler),
                typeof(GraphicRaycaster));
            canvasGo.transform.SetParent(transform, false);

            Canvas canvas = canvasGo.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 15;

            CanvasScaler scaler = canvasGo.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;

            RectTransform root = canvasGo.GetComponent<RectTransform>();
            BuildInspectPrompt(root);
            BuildDecipherOverlay(root);
            BuildWorldTimerOnBomb();

            _built = true;
        }

        private void BuildInspectPrompt(RectTransform root)
        {
            GameObject promptGo = CreatePanel(
                "BombInspectPrompt",
                root,
                new Vector2(0.5f, 1f),
                new Vector2(0.5f, 1f),
                new Vector2(0f, -84f),
                new Vector2(360f, 42f),
                Palette.UiAccentAmber);
            _inspectPromptRoot = promptGo;

            Button promptBtn = promptGo.AddComponent<Button>();
            promptBtn.onClick.AddListener(() =>
            {
                if (bomb != null && party != null && party.ActiveCharacter != null)
                {
                    bomb.OnInteractRequested(party.ActiveCharacter);
                }
            });

            _inspectPromptLabel = CreateText("InspectPromptLabel", promptGo.transform, 17, TextAnchor.MiddleCenter, Palette.DeepFjordSlate, FontStyle.Bold);
            _inspectPromptLabel.text = "[E / Click] Inspect Atom Bomb";
            StretchFull(_inspectPromptLabel.rectTransform, 8f, 4f);
        }

        private void BuildWorldTimerOnBomb()
        {
            if (bomb == null)
            {
                return;
            }

            if (_worldTimerCanvasGo != null && _worldTimerBomb == bomb)
            {
                return;
            }

            if (_worldTimerCanvasGo != null)
            {
                Destroy(_worldTimerCanvasGo);
                _worldTimerCanvasGo = null;
            }

            Transform panelParent = bomb.ControlPanelTransform != null ? bomb.ControlPanelTransform : bomb.transform;
            GameObject worldCanvasGo = new GameObject(
                "BombWorldTimer_Canvas",
                typeof(RectTransform),
                typeof(Canvas),
                typeof(CanvasRenderer));
            worldCanvasGo.transform.SetParent(panelParent, false);

            Canvas worldCanvas = worldCanvasGo.GetComponent<Canvas>();
            worldCanvas.renderMode = RenderMode.WorldSpace;
            worldCanvas.sortingOrder = 5;

            RectTransform rt = worldCanvasGo.GetComponent<RectTransform>();
            rt.localPosition = bomb.ControlPanelTransform != null
                ? new Vector3(0.145f, 0.065f, 0.044f)
                : new Vector3(0f, 1.25f, 0f);
            rt.localRotation = Quaternion.Euler(0f, 180f, 0f);
            rt.localScale = new Vector3(0.001f, 0.001f, 0.001f);
            rt.sizeDelta = new Vector2(150f, 64f);

            GameObject bgGo = CreatePanel(
                "WorldTimerBg",
                rt,
                new Vector2(0.5f, 0.5f),
                new Vector2(0.5f, 0.5f),
                Vector2.zero,
                new Vector2(150f, 64f),
                Palette.UiPanelSlate);
            _worldTimerBg = bgGo.GetComponent<Image>();

            _worldTimerLabel = CreateText("WorldTimerText", bgGo.transform, 28, TextAnchor.MiddleCenter, Palette.UiAccentAmber, FontStyle.Bold);
            _worldTimerLabel.horizontalOverflow = HorizontalWrapMode.Overflow;
            _worldTimerLabel.verticalOverflow = VerticalWrapMode.Overflow;
            StretchFull(_worldTimerLabel.rectTransform, 4f, 4f);

            _worldTimerCanvasGo = worldCanvasGo;
            _worldTimerBomb = bomb;
        }

        private void BuildDecipherOverlay(RectTransform root)
        {
            _panelRoot = CreatePanel(
                "BombDecipherOverlay",
                root,
                new Vector2(0.5f, 0.5f),
                new Vector2(0.5f, 0.5f),
                new Vector2(0f, -10f),
                new Vector2(1280f, 740f),
                Palette.UiPanelSlate);

            RectTransform panelRt = _panelRoot.GetComponent<RectTransform>();

            // Header bar
            GameObject header = CreatePanel(
                "Header",
                panelRt,
                new Vector2(0.5f, 1f),
                new Vector2(0.5f, 1f),
                new Vector2(0f, -14f),
                new Vector2(1252f, 76f),
                Palette.DeepFjordSlate);
            _panelHeaderBg = header.GetComponent<Image>();

            Text title = CreateText("HeaderTitle", header.transform, 21, TextAnchor.UpperLeft, Palette.UiTextParchment, FontStyle.Bold);
            title.text = "MK-VI ATOMIC SEA MINE — 6 DECIPHER MODULES";
            SetRect(title.rectTransform, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(16f, 13f), new Vector2(560f, 28f));

            Text subtitle = CreateText("HeaderSubtitle", header.transform, 14, TextAnchor.LowerLeft, Palette.SeaFoam, FontStyle.Normal);
            subtitle.text = "1st Person View  •  Wrong word or contact check deducts 1 minute";
            SetRect(subtitle.rectTransform, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(16f, -13f), new Vector2(560f, 24f));

            _panelTimerLabel = CreateText("PanelTimer", header.transform, 26, TextAnchor.UpperCenter, Palette.UiAccentAmber, FontStyle.Bold);
            SetRect(_panelTimerLabel.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(75f, 14f), new Vector2(360f, 32f));

            _feedbackLabel = CreateText("PanelFeedback", header.transform, 15, TextAnchor.LowerCenter, Palette.UiTextParchment, FontStyle.Bold);
            SetRect(_feedbackLabel.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(75f, -14f), new Vector2(440f, 26f));

            Button standUpBtn = CreateButton("Btn_StandUp", header.transform, "Stand Up [Esc]", out Image standUpBg, out Text standUpLbl, 16);
            standUpBg.color = Palette.WetBasalt;
            standUpLbl.color = Palette.UiTextParchment;
            standUpLbl.fontStyle = FontStyle.Bold;
            SetRect(standUpBtn.GetComponent<RectTransform>(), new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(-14f, 0f), new Vector2(172f, 46f));
            standUpBtn.onClick.AddListener(() => bomb?.StopInspection());

            // 6-Module Grid Body (3x2)
            GameObject gridHost = CreatePanel(
                "ModulesGrid",
                panelRt,
                new Vector2(0.5f, 0.5f),
                new Vector2(0.5f, 0.5f),
                new Vector2(0f, -6f),
                new Vector2(1252f, 550f),
                new Color(0f, 0f, 0f, 0f));

            GridLayoutGroup grid = gridHost.AddComponent<GridLayoutGroup>();
            grid.cellSize = new Vector2(408f, 266f);
            grid.spacing = new Vector2(14f, 14f);
            grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
            grid.constraintCount = 3;
            grid.childAlignment = TextAnchor.MiddleCenter;

            RectTransform gridRt = gridHost.GetComponent<RectTransform>();
            BuildWordModuleBay(gridRt);
            BuildContactsModuleBay(gridRt);
            BuildWiresModuleBay(gridRt);
            BuildPlaceholderModuleBay(gridRt, 3, "4. DECIPHER IV", "Pending Clue — Slot 4 of 6\n(Future cipher / lock mechanism)");
            BuildPlaceholderModuleBay(gridRt, 4, "5. DECIPHER V", "Pending Clue — Slot 5 of 6\n(Future cipher / lock mechanism)");
            BuildPlaceholderModuleBay(gridRt, 5, "6. DECIPHER VI", "Pending Clue — Slot 6 of 6\n(Future cipher / lock mechanism)");

            // Footer bar
            GameObject footer = CreatePanel(
                "Footer",
                panelRt,
                new Vector2(0.5f, 0f),
                new Vector2(0.5f, 0f),
                new Vector2(0f, 14f),
                new Vector2(1252f, 68f),
                Palette.DeepFjordSlate);

            Text tip = CreateText("FooterTip", footer.transform, 15, TextAnchor.MiddleLeft, Palette.SeaFoam, FontStyle.Normal);
            tip.text = "Tip: Press [1] [2] [3] or [Tab] to switch player — other players see you bending down over the mine.";
            SetRect(tip.rectTransform, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(16f, 0f), new Vector2(730f, 44f));

            Button resetBtn = CreateButton("Btn_ResetBomb", footer.transform, "Reset Test", out Image resetBg, out Text resetLbl, 15);
            resetBg.color = Palette.WetBasalt;
            resetLbl.color = Palette.UiTextParchment;
            SetRect(resetBtn.GetComponent<RectTransform>(), new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(-336f, 0f), new Vector2(130f, 44f));
            resetBtn.onClick.AddListener(() => bomb?.ResetBomb());

            _deactivateButton = CreateButton("Btn_Deactivate", footer.transform, "LOCKED (SOLVE 1 & 2)", out _deactivateBg, out _deactivateLabel, 17);
            SetRect(_deactivateButton.GetComponent<RectTransform>(), new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(-14f, 0f), new Vector2(308f, 46f));
            _deactivateButton.onClick.AddListener(() => bomb?.TryDeactivate());
        }

        private void BuildWordModuleBay(RectTransform gridParent)
        {
            GameObject bay = CreateModuleBayFrame(gridParent, "Module_1_Word", "1. 4-LETTER WORD", out _wordStatusBg, out _wordStatusLabel);
            RectTransform bayRt = bay.GetComponent<RectTransform>();

            // 4 tumbler columns
            for (int i = 0; i < 4; i++)
            {
                int slotIdx = i;
                float x = -129f + i * 86f;

                Button upBtn = CreateButton($"Slot{i}_Up", bayRt, "▲", out Image upBg, out Text upLbl, 13);
                upBg.color = Palette.WetBasalt;
                upLbl.color = Palette.UiTextParchment;
                SetRect(upBtn.GetComponent<RectTransform>(), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(x, 60f), new Vector2(70f, 22f));
                upBtn.onClick.AddListener(() =>
                {
                    _selectedWordSlot = slotIdx;
                    bomb?.CycleWordLetter(slotIdx, 1);
                });

                Button letterBtn = CreateButton($"Slot{i}_Val", bayRt, "A", out _slotBgs[i], out _slotLabels[i], 26);
                _slotLabels[i].fontStyle = FontStyle.Bold;
                SetRect(letterBtn.GetComponent<RectTransform>(), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(x, 26f), new Vector2(70f, 42f));
                letterBtn.onClick.AddListener(() => _selectedWordSlot = slotIdx);

                Button downBtn = CreateButton($"Slot{i}_Down", bayRt, "▼", out Image downBg, out Text downLbl, 13);
                downBg.color = Palette.WetBasalt;
                downLbl.color = Palette.UiTextParchment;
                SetRect(downBtn.GetComponent<RectTransform>(), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(x, -8f), new Vector2(70f, 22f));
                downBtn.onClick.AddListener(() =>
                {
                    _selectedWordSlot = slotIdx;
                    bomb?.CycleWordLetter(slotIdx, -1);
                });
            }

            // Quick keypad (2 rows x 7 buttons) for typing Ø and letters with mouse/touch
            for (int k = 0; k < QuickLetters.Length; k++)
            {
                int row = k / 7;
                int col = k % 7;
                string glyph = QuickLetters[k];
                char ch = glyph[0];
                float x = -156f + col * 52f;
                float y = -40f - row * 28f;

                Button keyBtn = CreateButton($"Key_{glyph}", bayRt, glyph, out Image keyBg, out Text keyLbl, 14);
                keyBg.color = Palette.WetBasalt;
                keyLbl.color = Palette.UiTextParchment;
                keyLbl.fontStyle = FontStyle.Bold;
                SetRect(keyBtn.GetComponent<RectTransform>(), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(x, y), new Vector2(46f, 24f));
                keyBtn.onClick.AddListener(() =>
                {
                    if (bomb == null || bomb.IsWordSolved) return;
                    bomb.SetWordLetter(_selectedWordSlot, ch);
                    _selectedWordSlot = Mathf.Min(3, _selectedWordSlot + 1);
                });
            }

            _submitWordButton = CreateButton("Btn_SubmitWord", bayRt, "SUBMIT WORD (-1m if wrong)", out _submitWordBg, out _submitWordLabel, 15);
            _submitWordLabel.fontStyle = FontStyle.Bold;
            SetRect(_submitWordButton.GetComponent<RectTransform>(), new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 12f), new Vector2(368f, 34f));
            _submitWordButton.onClick.AddListener(() => bomb?.TrySubmitWord());
        }

        private void BuildContactsModuleBay(RectTransform gridParent)
        {
            GameObject bay = CreateModuleBayFrame(gridParent, "Module_2_Contacts", "2. 10 ON/OFF CONTACTS", out _contactsStatusBg, out _contactsStatusLabel);
            RectTransform bayRt = bay.GetComponent<RectTransform>();

            for (int i = 0; i < SeaMineBomb.ContactCount; i++)
            {
                int contactIdx = i;
                float x = -171f + i * 38f;
                Button swBtn = CreateButton($"Contact_{i + 1}", bayRt, $"C{i + 1}\nOFF", out _contactBgs[i], out _contactLabels[i], 14);
                _contactLabels[i].fontStyle = FontStyle.Bold;
                SetRect(swBtn.GetComponent<RectTransform>(), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(x, 22f), new Vector2(34f, 92f));
                swBtn.onClick.AddListener(() => bomb?.ToggleContact(contactIdx));
            }

            Text hint = CreateText("ContactsHint", bayRt, 13, TextAnchor.MiddleCenter, Palette.SeaFoam, FontStyle.Normal);
            hint.text = "Click switches to toggle ON / OFF\nC1 is on the left";
            SetRect(hint.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, -52f), new Vector2(380f, 38f));

            _submitContactsButton = CreateButton("Btn_SubmitContacts", bayRt, "CHECK CONTACTS (-1m if wrong)", out _submitContactsBg, out _submitContactsLabel, 15);
            _submitContactsLabel.fontStyle = FontStyle.Bold;
            SetRect(_submitContactsButton.GetComponent<RectTransform>(), new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 12f), new Vector2(368f, 34f));
            _submitContactsButton.onClick.AddListener(() => bomb?.TrySubmitContacts());
        }

        /// <summary>Module 3: eight coloured wires running top to bottom; click one while holding wire cutters to cut it.</summary>
        private void BuildWiresModuleBay(RectTransform gridParent)
        {
            GameObject bay = CreateModuleBayFrame(gridParent, "Module_3_Wires", "3. CUT THE RIGHT WIRE", out _wiresStatusBg, out _wiresStatusLabel);
            RectTransform bayRt = bay.GetComponent<RectTransform>();

            // recessed backing plate behind the wiring
            GameObject plate = CreatePanel("WirePlate", bayRt, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 16f), new Vector2(384f, 138f), Color.Lerp(Palette.DeepFjordSlate, Color.black, 0.35f));
            plate.GetComponent<Image>().raycastTarget = false;

            const float topY = 72f, bottomY = -40f;
            Color brass = Color.Lerp(Palette.LanternAmber, Palette.HarbourConcrete, 0.35f);
            Color brassDark = Color.Lerp(brass, Color.black, 0.45f);

            for (int i = 0; i < SeaMineBomb.WireCount; i++)
            {
                int wireIdx = i;
                float x = -161f + i * 46f;

                // the wire itself: a wide hit area so it's easy to click, drawn as a shaded curved cable
                var wireGo = new GameObject($"Wire_{i + 1}", typeof(RectTransform), typeof(CanvasRenderer), typeof(UiWire));
                var wireRt = wireGo.GetComponent<RectTransform>();
                wireRt.SetParent(bayRt, false);
                SetRect(wireRt, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(x, (topY + bottomY) * 0.5f), new Vector2(40f, topY - bottomY));
                _wires[i] = wireGo.GetComponent<UiWire>();
                _wires[i].Configure(Palette.BombWires[i], 6f + (i % 3) * 2f, i * 1.7f);
                Button btn = wireGo.AddComponent<Button>();
                btn.targetGraphic = _wires[i];
                ColorBlock colors = btn.colors;
                colors.normalColor = new Color(0.88f, 0.88f, 0.88f, 1f);
                colors.highlightedColor = Color.white;
                colors.pressedColor = new Color(0.7f, 0.7f, 0.7f, 1f);
                colors.disabledColor = new Color(0.88f, 0.88f, 0.88f, 1f);
                btn.colors = colors;
                btn.onClick.AddListener(() => bomb?.TryCutWire(wireIdx));
            }

            // terminal strips with a brass screw clamping each wire end (drawn over the wire ends)
            foreach (float y in new[] { topY, bottomY })
            {
                GameObject block = CreatePanel("Terminal", bayRt, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, y), new Vector2(376f, 18f), Palette.WetBasalt);
                block.GetComponent<Image>().raycastTarget = false;
                GameObject lip = CreatePanel("TerminalLip", block.GetComponent<RectTransform>(), new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), Vector2.zero, new Vector2(376f, 3f), Color.Lerp(Palette.WetBasalt, Color.white, 0.25f));
                lip.GetComponent<Image>().raycastTarget = false;
                for (int i = 0; i < SeaMineBomb.WireCount; i++)
                {
                    float x = -161f + i * 46f;
                    GameObject screw = CreatePanel("Screw", bayRt, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(x, y), new Vector2(13f, 13f), brassDark);
                    screw.GetComponent<Image>().raycastTarget = false;
                    GameObject face = CreatePanel("Face", screw.GetComponent<RectTransform>(), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(-0.5f, 0.5f), new Vector2(10f, 10f), brass);
                    face.GetComponent<Image>().raycastTarget = false;
                    GameObject slot = CreatePanel("Slot", face.GetComponent<RectTransform>(), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(8f, 2f), brassDark);
                    slot.GetComponent<Image>().raycastTarget = false;
                    slot.transform.localRotation = Quaternion.Euler(0f, 0f, 35f + i * 23f);
                }
            }

            _wiresHint = CreateText("WiresHint", bayRt, 13, TextAnchor.MiddleCenter, Palette.SeaFoam, FontStyle.Normal);
            SetRect(_wiresHint.rectTransform, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 14f), new Vector2(380f, 56f));
        }

        private void BuildPlaceholderModuleBay(RectTransform gridParent, int moduleIndex, string titleText, string bodyText)
        {
            int idx = moduleIndex - 3;
            GameObject bay = CreateModuleBayFrame(
                gridParent,
                $"Module_{moduleIndex + 1}_Placeholder",
                titleText,
                out _placeholderStatusBgs[idx],
                out _placeholderStatusLabels[idx]);
            RectTransform bayRt = bay.GetComponent<RectTransform>();

            Text body = CreateText("PlaceholderBody", bayRt, 15, TextAnchor.MiddleCenter, Palette.SeaFoam, FontStyle.Normal);
            body.text = bodyText;
            SetRect(body.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, -12f), new Vector2(360f, 120f));
        }

        private GameObject CreateModuleBayFrame(
            RectTransform parent,
            string name,
            string titleText,
            out Image statusBg,
            out Text statusLabel)
        {
            GameObject bay = CreatePanel(name, parent, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(408f, 266f), Palette.DeepFjordSlate);
            RectTransform rt = bay.GetComponent<RectTransform>();

            Text title = CreateText("Title", rt, 17, TextAnchor.MiddleLeft, Palette.UiTextParchment, FontStyle.Bold);
            title.text = titleText;
            SetRect(title.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(14f, -10f), new Vector2(260f, 28f));

            GameObject pill = CreatePanel("StatusPill", rt, new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-12f, -11f), new Vector2(104f, 26f), Palette.WetBasalt);
            statusBg = pill.GetComponent<Image>();
            statusLabel = CreateText("StatusLabel", pill.transform, 13, TextAnchor.MiddleCenter, Palette.UiTextParchment, FontStyle.Bold);
            StretchFull(statusLabel.rectTransform, 4f, 2f);

            return bay;
        }

        private void RefreshView()
        {
            if (!_built)
            {
                return;
            }

            if (bomb == null)
            {
                if (_worldTimerCanvasGo != null) _worldTimerCanvasGo.SetActive(false);
                if (_inspectPromptRoot != null) _inspectPromptRoot.SetActive(false);
                if (_panelRoot != null) _panelRoot.SetActive(false);
                return;
            }

            if (_worldTimerCanvasGo == null || _worldTimerBomb != bomb)
            {
                BuildWorldTimerOnBomb();
            }

            string formattedTime = SeaMineBomb.FormatTime(bomb.RemainingSeconds);
            bool penaltyFlash = bomb.PenaltyFlashTimer > 0f;

            if (_worldTimerCanvasGo != null)
            {
                _worldTimerCanvasGo.SetActive(true);
                if (bomb.IsDisarmed)
                {
                    _worldTimerBg.color = Palette.UiConfirmGreen;
                    _worldTimerLabel.color = Palette.DeepFjordSlate;
                    _worldTimerLabel.text = "DISARMED";
                }
                else if (bomb.IsExploded)
                {
                    _worldTimerBg.color = Palette.BoathouseRust;
                    _worldTimerLabel.color = Palette.UiTextParchment;
                    _worldTimerLabel.text = "00:00:00";
                }
                else
                {
                    _worldTimerBg.color = penaltyFlash ? Palette.BoathouseRust : Palette.UiPanelSlate;
                    _worldTimerLabel.color = penaltyFlash ? Palette.UiTextParchment : Palette.UiAccentAmber;
                    _worldTimerLabel.text = formattedTime;
                }
            }

            bool inspecting = bomb.IsActiveCharacterInspecting;
            bool inReach = !inspecting && party != null && party.ActiveCharacter != null && bomb.IsCharacterInReach(party.ActiveCharacter);
            _inspectPromptRoot.SetActive(inReach);

            _panelRoot.SetActive(inspecting);
            if (!inspecting)
            {
                return;
            }

            _panelHeaderBg.color = penaltyFlash ? Palette.BoathouseRust : Palette.DeepFjordSlate;
            _panelTimerLabel.text = bomb.IsDisarmed
                ? "DISARMED"
                : (bomb.IsExploded ? "00:00:00" : $"TIMER: {formattedTime}");
            _panelTimerLabel.color = bomb.IsDisarmed
                ? Palette.UiConfirmGreen
                : (penaltyFlash || bomb.IsExploded ? Palette.UiTextParchment : Palette.UiAccentAmber);

            _feedbackLabel.text = bomb.LastFeedbackMessage;
            _feedbackLabel.color = bomb.IsDisarmed
                ? Palette.UiConfirmGreen
                : (bomb.LastFeedbackWasPenalty ? Palette.UiTextParchment : Palette.UiAccentAmber);

            // Module 1
            _wordStatusBg.color = bomb.IsWordSolved ? Palette.UiConfirmGreen : Palette.UiAccentAmber;
            _wordStatusLabel.color = Palette.DeepFjordSlate;
            _wordStatusLabel.text = bomb.IsWordSolved ? "SOLVED" : "ARMED";

            for (int i = 0; i < 4; i++)
            {
                _slotLabels[i].text = bomb.GetWordLetter(i).ToString();
                if (bomb.IsWordSolved)
                {
                    _slotBgs[i].color = Color.Lerp(Palette.DeepFjordSlate, Palette.UiConfirmGreen, 0.28f);
                    _slotLabels[i].color = new Color(0.28f, 0.98f, 0.46f, 1f);
                }
                else if (bomb.WordPenaltyFlashTimer > 0f)
                {
                    bool flashOn = Mathf.Repeat(bomb.WordPenaltyFlashTimer, 0.26f) > 0.09f;
                    _slotBgs[i].color = flashOn ? new Color(0.72f, 0.14f, 0.12f, 1f) : Palette.DeepFjordSlate;
                    _slotLabels[i].color = flashOn ? new Color(1.0f, 0.24f, 0.20f, 1f) : new Color(0.95f, 0.40f, 0.36f, 1f);
                }
                else if (i == _selectedWordSlot)
                {
                    _slotBgs[i].color = Palette.UiAccentAmber;
                    _slotLabels[i].color = Palette.DeepFjordSlate;
                }
                else
                {
                    _slotBgs[i].color = Palette.WetBasalt;
                    _slotLabels[i].color = Palette.UiTextParchment;
                }
            }

            _submitWordButton.interactable = !bomb.IsWordSolved && !bomb.IsDisarmed && !bomb.IsExploded;
            _submitWordBg.color = bomb.IsWordSolved ? Palette.UiConfirmGreen : Palette.UiAccentAmber;
            _submitWordLabel.color = Palette.DeepFjordSlate;
            _submitWordLabel.text = bomb.IsWordSolved ? "WORD SOLVED (TØNT)" : "SUBMIT WORD (-1m if wrong)";

            // Module 2
            _contactsStatusBg.color = bomb.IsContactsSolved ? Palette.UiConfirmGreen : Palette.UiAccentAmber;
            _contactsStatusLabel.color = Palette.DeepFjordSlate;
            _contactsStatusLabel.text = bomb.IsContactsSolved ? "SOLVED" : "ARMED";

            for (int i = 0; i < SeaMineBomb.ContactCount; i++)
            {
                bool on = bomb.GetContact(i);
                _contactLabels[i].text = $"C{i + 1}\n{(on ? "ON" : "OFF")}";
                if (bomb.IsContactsSolved)
                {
                    _contactBgs[i].color = on ? Palette.UiConfirmGreen : Palette.WetBasalt;
                    _contactLabels[i].color = on ? Palette.DeepFjordSlate : Palette.UiTextParchment;
                }
                else
                {
                    _contactBgs[i].color = on ? Palette.UiAccentAmber : Palette.WetBasalt;
                    _contactLabels[i].color = on ? Palette.DeepFjordSlate : Palette.UiTextParchment;
                }
            }

            _submitContactsButton.interactable = !bomb.IsContactsSolved && !bomb.IsDisarmed && !bomb.IsExploded;
            _submitContactsBg.color = bomb.IsContactsSolved ? Palette.UiConfirmGreen : Palette.UiAccentAmber;
            _submitContactsLabel.color = Palette.DeepFjordSlate;
            _submitContactsLabel.text = bomb.IsContactsSolved ? "CONTACTS LOCKED" : "CHECK CONTACTS (-1m if wrong)";

            // Module 3
            bool hasCutters = bomb.InspectorHasWireCutters;
            _wiresStatusBg.color = bomb.IsWiresSolved ? Palette.UiConfirmGreen : (bomb.IsExploded ? Palette.BoathouseRust : Palette.UiAccentAmber);
            _wiresStatusLabel.color = bomb.IsExploded && !bomb.IsWiresSolved ? Palette.UiTextParchment : Palette.DeepFjordSlate;
            _wiresStatusLabel.text = bomb.IsWiresSolved ? "SOLVED" : (bomb.IsExploded ? "DETONATED" : "ARMED");
            for (int i = 0; i < SeaMineBomb.WireCount; i++)
            {
                bool cut = bomb.IsWireCut(i);
                _wires[i].SetCut(cut);
            }

            _wiresHint.text = bomb.IsWiresSolved
                ? "Circuit dead. Module 3 is safe."
                : (hasCutters
                    ? "Click a wire to cut it.\nOnly ONE is right — a wrong wire detonates the mine!"
                    : "Hold WIRE CUTTERS to cut a wire.\nOnly ONE is right — a wrong wire detonates the mine!");
            _wiresHint.color = hasCutters || bomb.IsWiresSolved ? Palette.SeaFoam : Palette.UiAccentAmber;

            // Modules 4-6 placeholders
            for (int i = 0; i < _placeholderStatusBgs.Length; i++)
            {
                _placeholderStatusBgs[i].color = bomb.IsDisarmed ? Palette.UiConfirmGreen : Palette.WetBasalt;
                _placeholderStatusLabels[i].color = bomb.IsDisarmed ? Palette.DeepFjordSlate : Palette.SeaFoam;
                _placeholderStatusLabels[i].text = bomb.IsDisarmed ? "CLEARED" : "LATER";
            }

            // Deactivate button
            _deactivateButton.interactable = bomb.CanDeactivate;
            if (bomb.IsDisarmed)
            {
                _deactivateBg.color = Palette.UiConfirmGreen;
                _deactivateLabel.color = Palette.DeepFjordSlate;
                _deactivateLabel.text = "BOMB DEACTIVATED";
            }
            else if (bomb.IsExploded)
            {
                _deactivateBg.color = Palette.BoathouseRust;
                _deactivateLabel.color = Palette.UiTextParchment;
                _deactivateLabel.text = "DETONATED";
            }
            else if (bomb.CanDeactivate)
            {
                _deactivateBg.color = Palette.UiConfirmGreen;
                _deactivateLabel.color = Palette.DeepFjordSlate;
                _deactivateLabel.text = "DEACTIVATE BOMB NOW!";
            }
            else
            {
                _deactivateBg.color = Palette.WetBasalt;
                _deactivateLabel.color = Palette.UiTextParchment;
                _deactivateLabel.text = "LOCKED (SOLVE 1 & 2)";
            }
        }

        private GameObject CreatePanel(
            string name,
            RectTransform parent,
            Vector2 anchor,
            Vector2 pivot,
            Vector2 anchoredPos,
            Vector2 size,
            Color color)
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

        private Button CreateButton(
            string name,
            Transform parent,
            string caption,
            out Image background,
            out Text label,
            int fontSize)
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
            colors.disabledColor = new Color(0.75f, 0.75f, 0.75f, 0.8f);
            button.colors = colors;

            label = CreateText($"{name}_Label", go.transform, fontSize, TextAnchor.MiddleCenter, Palette.UiTextParchment, FontStyle.Normal);
            label.text = caption;
            StretchFull(label.rectTransform, 4f, 2f);

            return button;
        }

        private Text CreateText(
            string name,
            Transform parent,
            int fontSize,
            TextAnchor alignment,
            Color color,
            FontStyle style)
        {
            GameObject go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            go.transform.SetParent(parent, false);

            Text text = go.GetComponent<Text>();
            text.font = _uiFont;
            text.fontSize = fontSize;
            text.fontStyle = style;
            text.alignment = alignment;
            text.color = color;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Truncate;
            text.raycastTarget = false;
            return text;
        }

        private static void SetRect(
            RectTransform rt,
            Vector2 anchorMin,
            Vector2 anchorMax,
            Vector2 pivot,
            Vector2 anchoredPos,
            Vector2 size)
        {
            rt.anchorMin = anchorMin;
            rt.anchorMax = anchorMax;
            rt.pivot = pivot;
            rt.anchoredPosition = anchoredPos;
            rt.sizeDelta = size;
        }

        private static void StretchFull(RectTransform rt, float padX, float padY)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(padX, padY);
            rt.offsetMax = new Vector2(-padX, -padY);
        }
    }
}
