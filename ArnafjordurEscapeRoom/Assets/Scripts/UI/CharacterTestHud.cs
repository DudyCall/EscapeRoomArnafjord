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
        private Image _useBackground;
        private Text _useLabel;
        private Image _itemBackground;
        private Text _itemLabel;
        private Text _statusLabel;
        private PickupItem _lastNearestItem;
        private bool _wasUsingItem;
        private CharacterDrunk.DrunkStage _lastDrunkStage;
        private int _lastSipsDrunk;
        private bool _wasIncapacitated;
        private bool _wasNearBeerCrate;
        private FishingRod.RodState _lastRodState;
        private int _lastFishCaught;
        private bool _wasNearSeaMine;
        private bool _wasInspectingSeaMine;
        private bool _wasNearIgniteWood;
        private bool _wasNearStackTarget;
        private WoodBonfire _lastNearBonfire;
        private int _lastBonfireWoodCount;
        private bool _lastBonfireLit;
        private int _lastBonfireSeconds;
        private string _lastPocketSummary = string.Empty;
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

            WoodBonfire.AnyChanged += RefreshView;
        }

        private void OnDisable()
        {
            if (party != null)
            {
                party.StateChanged -= RefreshView;
            }

            WoodBonfire.AnyChanged -= RefreshView;
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

            WoodBonfire.AnyChanged -= RefreshView;
            WoodBonfire.AnyChanged += RefreshView;

            RefreshView();
        }

        private void Update()
        {
            if (!_built || party == null)
            {
                return;
            }

            bool usingItem = party.ActiveItemHolder != null && party.ActiveItemHolder.IsUsingItem;
            CharacterDrunk drunk = party.ActiveCharacter != null ? party.ActiveCharacter.GetComponent<CharacterDrunk>() : null;
            CharacterDrunk.DrunkStage stage = drunk != null ? drunk.Stage : CharacterDrunk.DrunkStage.Sober;
            int sipsDrunk = drunk != null ? drunk.SipsDrunk : 0;
            bool incapacitated = drunk != null && drunk.IsIncapacitated;
            PickupItem heldItem = party.ActiveHeldItem;
            FishingRod heldRod = heldItem != null ? heldItem.GetComponent<FishingRod>() : null;
            FishingRod.RodState rodState = heldRod != null ? heldRod.State : FishingRod.RodState.Idle;
            int fishCaught = heldRod != null ? heldRod.FishCaughtCount : 0;
            SeaMineBomb bomb = SeaMineBomb.Active;
            bool nearSeaMine = bomb != null && party.ActiveCharacter != null && bomb.IsCharacterInReach(party.ActiveCharacter);
            bool inspectingSeaMine = bomb != null && bomb.IsActiveCharacterInspecting;

            Vector3 activePos = party.ActiveCharacter != null ? party.ActiveCharacter.transform.position : Vector3.zero;
            bool holdingLighter = heldItem != null && heldItem.GetComponent<Lighter>() != null;
            Firewood heldWood = heldItem != null ? heldItem.GetComponent<Firewood>() : null;
            bool nearIgnite = holdingLighter && party.ActiveCharacter != null
                && WoodBonfire.TryFindIgnitionTarget(activePos, Lighter.DefaultIgnitionReach, out _, out _);
            bool nearStack = heldWood != null && party.ActiveCharacter != null
                && WoodBonfire.TryFindStackTarget(activePos + party.ActiveCharacter.transform.forward * 0.55f, activePos, heldWood, 1.85f, 2.3f, out _, out _);
            WoodBonfire nearBonfire = party.ActiveCharacter != null ? WoodBonfire.FindNearestInRange(activePos, 3.2f) : null;
            int bonfireWoodCount = nearBonfire != null ? nearBonfire.WoodCount : 0;
            bool bonfireLit = nearBonfire != null && nearBonfire.IsLit;
            int bonfireSeconds = nearBonfire != null ? Mathf.CeilToInt(nearBonfire.RemainingSeconds) : 0;
            string pocketSummary = GetPocketSummary(party.ActiveCharacter);

            if (usingItem != _wasUsingItem
                || stage != _lastDrunkStage
                || sipsDrunk != _lastSipsDrunk
                || incapacitated != _wasIncapacitated
                || rodState != _lastRodState
                || fishCaught != _lastFishCaught
                || nearSeaMine != _wasNearSeaMine
                || inspectingSeaMine != _wasInspectingSeaMine
                || nearIgnite != _wasNearIgniteWood
                || nearStack != _wasNearStackTarget
                || nearBonfire != _lastNearBonfire
                || bonfireWoodCount != _lastBonfireWoodCount
                || bonfireLit != _lastBonfireLit
                || bonfireSeconds != _lastBonfireSeconds
                || pocketSummary != _lastPocketSummary)
            {
                RefreshView();
                return;
            }

            if (heldItem != null)
            {
                return;
            }

            bool nearCrate = party.ActiveCharacter != null && BeerCrate.FindReachable(party.ActiveCharacter.transform.position) != null;
            PickupItem nearest = party.FindNearestAvailableItem(6f);
            if (nearest != _lastNearestItem || nearCrate != _wasNearBeerCrate)
            {
                RefreshView();
            }
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
            GameObject bar = CreatePanel("CompanionBar", root, new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-20f, -20f), new Vector2(792f, 54f), Palette.UiPanelSlate);
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

            Button useBtn = CreateButton("Btn_UseItem", bar.transform, "Use Item [U]", out _useBackground, out _useLabel);
            useBtn.onClick.AddListener(() => party?.UseActiveItem());

            Button itemBtn = CreateButton("Btn_Item", bar.transform, "No Item [G]", out _itemBackground, out _itemLabel);
            itemBtn.onClick.AddListener(() => party?.ToggleCarryItem());
        }

        private void BuildControlsFooter(RectTransform root)
        {
            GameObject footer = CreatePanel("ControlsFooter", root, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 18f), new Vector2(1460f, 54f), Palette.UiPanelSlate);

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

            PickupItem held = party != null ? party.ActiveHeldItem : null;
            PickupItem nearby = held == null && party != null ? party.FindNearestAvailableItem(6f) : null;
            bool nearCrate = held == null && party != null && party.ActiveCharacter != null
                && BeerCrate.FindReachable(party.ActiveCharacter.transform.position) != null;
            _lastNearestItem = nearby;
            _wasNearBeerCrate = nearCrate;
            _wasUsingItem = party != null && party.ActiveItemHolder != null && party.ActiveItemHolder.IsUsingItem;

            CharacterDrunk drunk = party != null && party.ActiveCharacter != null ? party.ActiveCharacter.GetComponent<CharacterDrunk>() : null;
            _lastDrunkStage = drunk != null ? drunk.Stage : CharacterDrunk.DrunkStage.Sober;
            _lastSipsDrunk = drunk != null ? drunk.SipsDrunk : 0;
            _wasIncapacitated = drunk != null && drunk.IsIncapacitated;
            FishingRod heldRod = held != null ? held.GetComponent<FishingRod>() : null;
            _lastRodState = heldRod != null ? heldRod.State : FishingRod.RodState.Idle;
            _lastFishCaught = heldRod != null ? heldRod.FishCaughtCount : 0;

            Vector3 activePos = party != null && party.ActiveCharacter != null ? party.ActiveCharacter.transform.position : Vector3.zero;
            Lighter heldLighter = held != null ? held.GetComponent<Lighter>() : null;
            Firewood heldWood = held != null ? held.GetComponent<Firewood>() : null;

            WoodBonfire igniteBonfire = null;
            Firewood igniteLoose = null;
            _wasNearIgniteWood = heldLighter != null && party != null && party.ActiveCharacter != null
                && WoodBonfire.TryFindIgnitionTarget(activePos, Lighter.DefaultIgnitionReach, out igniteBonfire, out igniteLoose);

            WoodBonfire stackBonfire = null;
            Firewood stackLoose = null;
            _wasNearStackTarget = heldWood != null && party != null && party.ActiveCharacter != null
                && WoodBonfire.TryFindStackTarget(activePos + party.ActiveCharacter.transform.forward * 0.55f, activePos, heldWood, 1.85f, 2.3f, out stackBonfire, out stackLoose);

            _lastNearBonfire = party != null && party.ActiveCharacter != null ? WoodBonfire.FindNearestInRange(activePos, 3.2f) : null;
            _lastBonfireWoodCount = _lastNearBonfire != null ? _lastNearBonfire.WoodCount : 0;
            _lastBonfireLit = _lastNearBonfire != null && _lastNearBonfire.IsLit;
            _lastBonfireSeconds = _lastNearBonfire != null ? Mathf.CeilToInt(_lastNearBonfire.RemainingSeconds) : 0;

            if (held != null)
            {
                if (heldRod != null)
                {
                    _useLabel.text = $"{heldRod.ActionLabel} {held.DisplayName} [U]";
                    _useBackground.color = heldRod.State == FishingRod.RodState.Bite ? Palette.UiAccentAmber : Palette.UiConfirmGreen;
                }
                else if (_wasNearIgniteWood)
                {
                    _useLabel.text = igniteBonfire != null ? "Light Bonfire [U]" : "Light Wood [U]";
                    _useBackground.color = Palette.UiAccentAmber;
                }
                else if (_wasNearStackTarget)
                {
                    _useLabel.text = stackBonfire != null ? "Add to Bonfire [U]" : "Stack Wood [U]";
                    _useBackground.color = Palette.UiConfirmGreen;
                }
                else
                {
                    bool emptyBottle = held.TryGetComponent(out BeerBottle bottle) && bottle.IsEmpty;
                    _useLabel.text = _wasUsingItem
                        ? "Drinking... [U]"
                        : (emptyBottle ? "Toss Empty Beer [U]" : $"{held.UseVerb} {held.DisplayName} [U]");
                    _useBackground.color = Palette.UiConfirmGreen;
                }

                _useLabel.color = Palette.DeepFjordSlate;
                _useLabel.fontStyle = FontStyle.Bold;

                if (_wasNearStackTarget)
                {
                    _itemLabel.text = stackBonfire != null ? "Add to Bonfire [G]" : "Stack on Wood [G]";
                    _itemBackground.color = Palette.UiConfirmGreen;
                }
                else
                {
                    _itemLabel.text = $"Drop {held.DisplayName} [G]";
                    _itemBackground.color = Palette.UiAccentAmber;
                }

                _itemLabel.color = Palette.DeepFjordSlate;
                _itemLabel.fontStyle = FontStyle.Bold;
            }
            else if (nearby != null)
            {
                _useLabel.text = nearCrate ? "Take Beer [U]" : "Use Item [U]";
                _useBackground.color = nearCrate ? Palette.UiConfirmGreen : Palette.WetBasalt;
                _useLabel.color = nearCrate ? Palette.DeepFjordSlate : Palette.UiTextParchment;
                _useLabel.fontStyle = nearCrate ? FontStyle.Bold : FontStyle.Normal;

                _itemLabel.text = $"Take {nearby.DisplayName} [G]";
                _itemBackground.color = Palette.UiConfirmGreen;
                _itemLabel.color = Palette.DeepFjordSlate;
                _itemLabel.fontStyle = FontStyle.Bold;
            }
            else
            {
                _useLabel.text = nearCrate ? "Take Beer [U]" : "Use Item [U]";
                _useBackground.color = nearCrate ? Palette.UiConfirmGreen : Palette.WetBasalt;
                _useLabel.color = nearCrate ? Palette.DeepFjordSlate : Palette.UiTextParchment;
                _useLabel.fontStyle = nearCrate ? FontStyle.Bold : FontStyle.Normal;

                _itemLabel.text = "No Item [G]";
                _itemBackground.color = Palette.WetBasalt;
                _itemLabel.color = Palette.UiTextParchment;
                _itemLabel.fontStyle = FontStyle.Normal;
            }

            string activeName = party != null && party.ActiveCharacter != null ? party.ActiveCharacter.DisplayName : "None";
            string viewMode = firstPerson ? "1st Person" : "3rd Person";
            string heldName = held != null ? held.DisplayName : "None";
            string fishSegment = heldRod != null && heldRod.FishCaughtCount > 0 ? $"  |  Fish: {heldRod.FishCaughtCount}" : string.Empty;
            string drunkStatus = drunk != null ? drunk.StatusLabel : "Sober";

            SeaMineBomb bomb = SeaMineBomb.Active;
            _wasNearSeaMine = bomb != null && party != null && party.ActiveCharacter != null && bomb.IsCharacterInReach(party.ActiveCharacter);
            _wasInspectingSeaMine = bomb != null && bomb.IsActiveCharacterInspecting;

            string inspectHint = _wasInspectingSeaMine
                ? "  •  Esc: Stand Up  •  1-3/Tab: Switch Player"
                : (_wasNearSeaMine ? "  •  E / Click Mine: Inspect Bomb" : string.Empty);

            string fireSegment = string.Empty;
            if (_lastNearBonfire != null)
            {
                string duration = WoodBonfire.FormatDuration(_lastNearBonfire.RemainingSeconds);
                string state = _lastNearBonfire.IsLit ? $"{duration} left" : $"{duration} fuel (Unlit)";
                fireSegment = $"  |  Bonfire ({_lastNearBonfire.WoodCount} wood): {state}";
            }

            _lastPocketSummary = GetPocketSummary(party != null ? party.ActiveCharacter : null);
            string pocketSegment = string.IsNullOrEmpty(_lastPocketSummary) ? string.Empty : $"  |  Pocket: {_lastPocketSummary}";

            _statusLabel.text =
                $"Active: {activeName}  |  View: {viewMode}  |  Companions: {mode}  |  Held: {heldName}{pocketSegment}{fishSegment}{fireSegment}  |  Drunk: {drunkStatus}   —   " +
                "WASD/Arrows: Move  •  Shift: Run  •  Click: Move  •  Right-Drag/Q-E: Look  •  Scroll: Zoom  •  V: 1st/3rd  •  1-3/Tab: Switch  •  F/R: Follow/Roam  •  U/Space: Use  •  G / Click Item: Drop/Take  •  I: Pockets" +
                inspectHint;
        }

        private static string GetPocketSummary(CharacterMotor active)
        {
            if (active == null || !active.TryGetComponent(out CharacterPockets pockets) || pockets.Items.Count == 0)
            {
                return string.Empty;
            }

            var names = new System.Collections.Generic.List<string>();
            for (int i = 0; i < pockets.Items.Count; i++)
            {
                PickupItem it = pockets.Items[i];
                if (it != null && !it.gameObject.activeSelf && it.transform.parent == pockets.transform)
                {
                    names.Add(it.DisplayName);
                }
            }

            return names.Count > 0 ? string.Join(", ", names) : string.Empty;
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
