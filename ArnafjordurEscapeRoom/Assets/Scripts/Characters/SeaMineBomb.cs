using System;
using System.Collections.Generic;
using Arnafjordur.Core;
using Arnafjordur.Intro;
using UnityEngine;
using UnityEngine.Rendering;

namespace Arnafjordur.Characters
{
    /// <summary>
    /// Washed-up atomic sea mine on the Árnafjørður beach: runs the 1-hour countdown timer,
    /// holds the 6 decipher modules (Module 1 word "TØNT", Module 2 10 on/off contacts = binary 946, Module 3 cut the one
    /// right wire of 8 with <see cref="WireCutters"/> — a wrong wire detonates; Module 4 three right <see cref="BombKey"/>s turned in 3 keyholes; Modules 5–6 placeholders),
    /// deducts 1 minute on wrong submissions, and drives the inspecting
    /// character's bending-down pose and first-person decipher camera.
    /// </summary>
    [DefaultExecutionOrder(-10)]
    public sealed class SeaMineBomb : MonoBehaviour
    {
        public const float DefaultDurationSeconds = 3600f;
        public const float WrongAttemptPenaltySeconds = 60f;
        public const string TargetWord = "TØNT";
        public const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZÆØÅÐ";
        public const int ModuleCount = 6;
        public const int ContactCount = 10;
        public const int WireCount = 8;
        public const int KeySlotCount = 3;
        public const float KeyRejectFlashSeconds = 1.2f;

        /// <summary>Module 2 answer: 1110110010, the number 946 in binary (C1 = most significant bit).</summary>
        public static readonly bool[] DefaultTargetContacts =
        {
            true, true, true, false, true, true, false, false, true, false,
        };

        private struct BoneOverride
        {
            public Transform Bone;
            public Quaternion PreLocalRot;
            public Quaternion PostLocalRot;
            public bool TrackPos;
            public Vector3 PreLocalPos;
            public Vector3 PostLocalPos;
        }

        private sealed class CharacterBones
        {
            public Transform Hips;
            public Transform Spine;
            public Transform Chest;
            public Transform Neck;
            public Transform Head;
            public Transform LeftUpperLeg;
            public Transform LeftLowerLeg;
            public Transform LeftFoot;
            public Transform RightUpperLeg;
            public Transform RightLowerLeg;
            public Transform RightFoot;
            public Transform LeftUpperArm;
            public Transform LeftLowerArm;
            public Transform LeftHand;
            public Transform RightUpperArm;
            public Transform RightLowerArm;
            public Transform RightHand;
        }

        [Header("Geometry & Visuals")]
        [SerializeField] private Transform controlPanelRoot;
        [SerializeField] private Transform[] contactSwitchLevers = Array.Empty<Transform>();
        [SerializeField] private Renderer[] moduleStatusLights = Array.Empty<Renderer>();
        [SerializeField] private Renderer armedBeaconRenderer;
        [SerializeField] private Material ledRedMaterial;
        [SerializeField] private Material ledGreenMaterial;
        [SerializeField] private Material ledAmberMaterial;
        [SerializeField, Tooltip("Module 4 keyhole rims on the 3D panel: dark until a key turns (green) or is refused (red flash).")]
        private Renderer[] keyholeLights = System.Array.Empty<Renderer>();
        [SerializeField] private Material keyholeOffMaterial;
        [SerializeField, Tooltip("Turned-key pieces shown in each 3D keyhole once a right key is in.")]
        private GameObject[] keyholeKeys = System.Array.Empty<GameObject>();

        /// <summary>Module 3 answer: cut 3 wires in order — White (4), Blue (1), Red (0).</summary>
        public static readonly int[] DefaultWireSequence = { 4, 1, 0 };

        [Header("Module 3: Wires")]
        [SerializeField, Tooltip("Ordered wire indices (into Palette.BombWireNames) that disarm Module 3. Any wrong wire or order detonates the mine.")]
        private int[] correctWireSequence = { 4, 1, 0 };

        [Header("Interaction")]
        [SerializeField] private Vector3 inspectStandOffsetLocal = new Vector3(-1.32f, 0f, 0f);
        [SerializeField] private float interactReach = 2.5f;

        private readonly char[] _wordLetters = { 'A', 'A', 'A', 'A' };
        private readonly bool[] _contacts = new bool[ContactCount];
        private readonly bool[] _targetContacts = (bool[])DefaultTargetContacts.Clone();
        private readonly bool[] _wiresCut = new bool[WireCount];
        private readonly List<PickupItem> _insertedKeys = new List<PickupItem>(KeySlotCount);
        private readonly List<BoneOverride> _boneOverrides = new List<BoneOverride>(20);
        private readonly Dictionary<CharacterMotor, CharacterBones> _boneCache = new Dictionary<CharacterMotor, CharacterBones>();

        private PartyController _party;
        private ThirdPersonCameraRig _cameraRig;
        private CharacterMotor _approachingCharacter;
        private CharacterMotor _posedCharacter;
        private float _beaconClock;

        /// <summary>Singleton active sea mine in the loaded scene.</summary>
        public static SeaMineBomb Active { get; private set; }

        /// <summary>Fired when timer penalties, decipher progress, inspection state, or deactivation changes.</summary>
        public event Action StateChanged;

        /// <summary>Seconds left before detonation (starts at 3600s / 1 hour).</summary>
        public float RemainingSeconds { get; private set; } = DefaultDurationSeconds;

        /// <summary>True once all active decipher modules are solved and the bomb has been deactivated.</summary>
        public bool IsDisarmed { get; private set; }

        /// <summary>True if the countdown reached zero before deactivation.</summary>
        public bool IsExploded { get; private set; }

        /// <summary>True when Module 1 (4-letter word TØNT) has been deciphered.</summary>
        public bool IsWordSolved { get; private set; }

        /// <summary>True when Module 2 (10 on/off contacts) has been deciphered.</summary>
        public bool IsContactsSolved { get; private set; }

        /// <summary>True once the required decipher modules are solved and the bomb can be deactivated.</summary>
        public bool CanDeactivate => IsWordSolved && IsContactsSolved && IsWiresSolved && IsKeysSolved && !IsDisarmed && !IsExploded;

        /// <summary>True once all three Module 4 keyholes have a right key turned in them.</summary>
        public bool IsKeysSolved => _insertedKeys.Count >= KeySlotCount;

        /// <summary>How many keyholes (from slot 1) already have a right key turned.</summary>
        public int KeysTurned => _insertedKeys.Count;

        /// <summary>Seconds left of the red flash on <see cref="KeyRejectSlot"/> after a key that won't turn.</summary>
        public float KeyRejectFlashTimer { get; private set; }

        /// <summary>The keyhole (0..2) a refused key was tried in.</summary>
        public int KeyRejectSlot { get; private set; } = -1;

        /// <summary>True when the character bent over the mine is holding something with <see cref="BombKey"/>.</summary>
        public bool InspectorHoldsKey => InspectorHeldItem != null && InspectorHeldItem.GetComponent<BombKey>() != null;

        private PickupItem InspectorHeldItem =>
            InspectingCharacter != null && InspectingCharacter.TryGetComponent(out CharacterItemHolder holder) ? holder.HeldItem : null;

        /// <summary>
        /// Puts the held key into the next empty keyhole (slot 1, then 2, then 3). A right key turns, stays in
        /// the bomb and lights the slot green; a wrong one won't turn, flashes the slot red and stays in the hand.
        /// Returns true when a key turned.
        /// </summary>
        public bool TryUseKey()
        {
            if (IsDisarmed || IsExploded || IsKeysSolved)
            {
                return false;
            }

            PickupItem held = InspectorHeldItem;
            BombKey key = held != null ? held.GetComponent<BombKey>() : null;
            if (key == null)
            {
                LastFeedbackMessage = "HOLD A KEY TO USE THE KEYHOLES";
                LastFeedbackWasPenalty = false;
                StateChanged?.Invoke();
                return false;
            }

            int slot = _insertedKeys.Count;
            if (!key.FitsBomb)
            {
                KeyRejectSlot = slot;
                KeyRejectFlashTimer = KeyRejectFlashSeconds;
                LastFeedbackMessage = $"THAT KEY WON'T TURN IN SLOT {slot + 1}";
                LastFeedbackWasPenalty = false;
                Refresh3DVisuals();
                StateChanged?.Invoke();
                return false;
            }

            // the key stays in the lock: take it out of the hand and keep it hidden on the bomb
            InspectingCharacter.GetComponent<CharacterItemHolder>().ReleaseHeldItem();
            held.transform.SetParent(controlPanelRoot != null ? controlPanelRoot : transform, false);
            held.gameObject.SetActive(false);
            _insertedKeys.Add(held);
            KeyRejectFlashTimer = 0f;
            KeyRejectSlot = -1;
            LastFeedbackMessage = IsKeysSolved ? "MODULE 4 — ALL THREE KEYS TURNED!" : $"KEY TURNED IN SLOT {slot + 1}";
            LastFeedbackWasPenalty = false;
            Refresh3DVisuals();
            StateChanged?.Invoke();
            return true;
        }

        /// <summary>Builder hook for the Module 4 keyholes on the 3D control panel.</summary>
        public void ConfigureKeyholes(Renderer[] lights, GameObject[] turnedKeys, Material offMat)
        {
            keyholeLights = lights ?? System.Array.Empty<Renderer>();
            keyholeKeys = turnedKeys ?? System.Array.Empty<GameObject>();
            keyholeOffMaterial = offMat;
            Refresh3DVisuals();
        }

        /// <summary>Ordered wire indices (into <see cref="Core.Palette.BombWireNames"/>) that disarm Module 3.</summary>
        public IReadOnlyList<int> CorrectWireSequence =>
            correctWireSequence != null && correctWireSequence.Length > 0 ? correctWireSequence : DefaultWireSequence;

        /// <summary>How many wires in the required sequence have been cut so far.</summary>
        public int WiresCutInSequence { get; private set; }

        /// <summary>Next wire index that must be cut, or -1 if already solved.</summary>
        public int NextRequiredWireIndex => WiresCutInSequence < CorrectWireSequence.Count ? CorrectWireSequence[WiresCutInSequence] : -1;

        /// <summary>True when Module 3's wire sequence has been cut in order.</summary>
        public bool IsWiresSolved { get; private set; }

        /// <summary>True when wire <paramref name="index"/> (0..7) has been cut.</summary>
        public bool IsWireCut(int index) => index >= 0 && index < WireCount && _wiresCut[index];

        /// <summary>True when the character bent over the mine is holding <see cref="WireCutters"/>.</summary>
        public bool InspectorHasWireCutters
        {
            get
            {
                if (InspectingCharacter == null || !InspectingCharacter.TryGetComponent(out CharacterItemHolder holder)) return false;
                return holder.HeldItem != null && holder.HeldItem.GetComponent<WireCutters>() != null;
            }
        }

        /// <summary>
        /// Cuts wire <paramref name="index"/> (0..7). Needs wire cutters in hand. The right wire solves Module 3;
        /// any other wire sets the mine off and the game is lost. Returns true when the wire was actually cut.
        /// </summary>
        public bool TryCutWire(int index)
        {
            if (IsDisarmed || IsExploded || IsWiresSolved || index < 0 || index >= WireCount || _wiresCut[index])
            {
                return false;
            }

            if (!InspectorHasWireCutters)
            {
                LastFeedbackMessage = "YOU NEED WIRE CUTTERS TO CUT A WIRE";
                LastFeedbackWasPenalty = false;
                StateChanged?.Invoke();
                return false;
            }

            _wiresCut[index] = true;
            if (index == NextRequiredWireIndex)
            {
                WiresCutInSequence++;
                if (WiresCutInSequence >= CorrectWireSequence.Count)
                {
                    IsWiresSolved = true;
                    LastFeedbackMessage = "MODULE 3 WIRES CUT — CIRCUIT DEAD!";
                }
                else
                {
                    LastFeedbackMessage = $"WIRE {WiresCutInSequence}/{CorrectWireSequence.Count} ({Core.Palette.BombWireNames[index]}) CUT!";
                }

                LastFeedbackWasPenalty = false;
                PenaltyFlashTimer = 0f;
                Refresh3DVisuals();
                StateChanged?.Invoke();
                return true;
            }

            TriggerExplosion();
            LastFeedbackMessage = $"WRONG WIRE ({Core.Palette.BombWireNames[index]})! ATOM BOMB DETONATED!";
            StateChanged?.Invoke();
            return true;
        }

        /// <summary>Total number of decipher modules on the sea mine (2 active + 4 placeholders).</summary>
        public int TotalModules => ModuleCount;

        /// <summary>Current 4-letter word entered on Module 1.</summary>
        public string CurrentWord => new string(_wordLetters);

        /// <summary>Most recent feedback message from a decipher attempt or bomb event.</summary>
        public string LastFeedbackMessage { get; private set; } = "DECIPHER MODULES TO DEACTIVATE";

        /// <summary>True when the most recent feedback message came from a penalty or detonation.</summary>
        public bool LastFeedbackWasPenalty { get; private set; }

        /// <summary>Remaining seconds for visual penalty flash feedback.</summary>
        public float PenaltyFlashTimer { get; private set; }

        /// <summary>Remaining seconds for flashing the Module 1 word letters red after a wrong guess.</summary>
        public float WordPenaltyFlashTimer { get; private set; }

        /// <summary>Transform of the angled control hatch on the sea mine.</summary>
        public Transform ControlPanelTransform => controlPanelRoot;

        /// <summary>Character currently bending down inspecting the sea mine, or null when idle.</summary>
        public CharacterMotor InspectingCharacter { get; private set; }

        /// <summary>True when the player's currently controlled character is inspecting the bomb in first-person.</summary>
        public bool IsActiveCharacterInspecting
        {
            get
            {
                ResolveSceneRefs();
                return _party != null && InspectingCharacter != null && _party.ActiveCharacter == InspectingCharacter;
            }
        }

        /// <summary>World-space centre of the angled decipher control panel on the sea mine.</summary>
        public Vector3 ControlPanelWorldCenter => controlPanelRoot != null
            ? controlPanelRoot.position
            : transform.position + Vector3.up * 0.56f;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStaticState()
        {
            Active = null;
        }

        /// <summary>Wires procedural 3D panel visuals and materials (called by the editor scene builder).</summary>
        public void ConfigureVisuals(
            Transform panelRoot,
            Transform[] switchLevers,
            Renderer[] moduleLights,
            Renderer beaconRenderer,
            Material redMat,
            Material greenMat,
            Material amberMat,
            Vector3 standOffsetLocal)
        {
            controlPanelRoot = panelRoot;
            contactSwitchLevers = switchLevers ?? Array.Empty<Transform>();
            moduleStatusLights = moduleLights ?? Array.Empty<Renderer>();
            armedBeaconRenderer = beaconRenderer;
            ledRedMaterial = redMat;
            ledGreenMaterial = greenMat;
            ledAmberMaterial = amberMat;
            inspectStandOffsetLocal = standOffsetLocal;
            Refresh3DVisuals();
        }

        private void Awake()
        {
            Active = this;
            ResetBomb();
        }

        private void OnEnable()
        {
            Active = this;
            ResolveSceneRefs();
            BindPartyEvents(true);
            Refresh3DVisuals();
        }

        private void OnDisable()
        {
            RestoreBoneOverrides();
            BindPartyEvents(false);
            if (_cameraRig != null && _cameraRig.IsInspectingOverride)
            {
                _cameraRig.ClearInspectionOverride(snap: false);
            }

            if (Active == this)
            {
                Active = null;
            }
        }

        private void OnDestroy()
        {
            RestoreBoneOverrides();
            BindPartyEvents(false);
            if (Active == this)
            {
                Active = null;
            }
        }

        private void Update()
        {
            if (!Application.isPlaying)
            {
                return;
            }

            Tick(Time.deltaTime);
            UpdatePendingApproach();
        }

        private void LateUpdate()
        {
            if (!Application.isPlaying)
            {
                return;
            }

            ApplyInspectionPose();
            SyncInspectionCamera(snapCamera: false);
        }

        /// <summary>Advances the bomb countdown timer and penalty flash state.</summary>
        public void Tick(float dt)
        {
            if (dt <= 0f)
            {
                return;
            }

            _beaconClock += dt;

            if (PenaltyFlashTimer > 0f)
            {
                PenaltyFlashTimer = Mathf.Max(0f, PenaltyFlashTimer - dt);
            }

            if (WordPenaltyFlashTimer > 0f)
            {
                WordPenaltyFlashTimer = Mathf.Max(0f, WordPenaltyFlashTimer - dt);
            }

            if (KeyRejectFlashTimer > 0f)
            {
                KeyRejectFlashTimer = Mathf.Max(0f, KeyRejectFlashTimer - dt);
                if (KeyRejectFlashTimer <= 0f)
                {
                    KeyRejectSlot = -1;     // the red goes out again
                    Refresh3DVisuals();
                    StateChanged?.Invoke();
                }
            }

            if (!IsDisarmed && !IsExploded)
            {
                RemainingSeconds = Mathf.Max(0f, RemainingSeconds - dt);
                if (RemainingSeconds <= 0f)
                {
                    TriggerExplosion();
                }
            }

            UpdateBeaconBlink();
        }

        /// <summary>Formats <paramref name="seconds"/> as HH:MM:SS.</summary>
        public static string FormatTime(float seconds)
        {
            int total = Mathf.Max(0, Mathf.CeilToInt(seconds));
            int hours = total / 3600;
            int minutes = (total % 3600) / 60;
            int secs = total % 60;
            return $"{hours:00}:{minutes:00}:{secs:00}";
        }

        /// <summary>Returns the character at slot <paramref name="slotIndex"/> (0..3) in Module 1.</summary>
        public char GetWordLetter(int slotIndex)
        {
            return slotIndex >= 0 && slotIndex < _wordLetters.Length ? _wordLetters[slotIndex] : 'A';
        }

        /// <summary>Sets slot <paramref name="slotIndex"/> (0..3) to <paramref name="letter"/>.</summary>
        public void SetWordLetter(int slotIndex, char letter)
        {
            if (IsWordSolved || IsDisarmed || IsExploded || slotIndex < 0 || slotIndex >= _wordLetters.Length)
            {
                return;
            }

            char upper = char.ToUpperInvariant(letter);
            if (Alphabet.IndexOf(upper) < 0)
            {
                return;
            }

            if (_wordLetters[slotIndex] == upper)
            {
                return;
            }

            _wordLetters[slotIndex] = upper;
            StateChanged?.Invoke();
        }

        /// <summary>Cycles slot <paramref name="slotIndex"/> (0..3) through <see cref="Alphabet"/> by <paramref name="delta"/>.</summary>
        public void CycleWordLetter(int slotIndex, int delta)
        {
            if (IsWordSolved || IsDisarmed || IsExploded || slotIndex < 0 || slotIndex >= _wordLetters.Length)
            {
                return;
            }

            int idx = Alphabet.IndexOf(_wordLetters[slotIndex]);
            if (idx < 0)
            {
                idx = 0;
            }

            int next = (idx + delta) % Alphabet.Length;
            if (next < 0)
            {
                next += Alphabet.Length;
            }

            _wordLetters[slotIndex] = Alphabet[next];
            StateChanged?.Invoke();
        }

        /// <summary>Checks the 4-letter word against TØNT; deducts 1 minute on a wrong guess.</summary>
        public bool TrySubmitWord(string overrideWord = null)
        {
            if (IsDisarmed || IsExploded)
            {
                return IsWordSolved;
            }

            if (IsWordSolved)
            {
                return true;
            }

            string candidate = (overrideWord ?? CurrentWord).Trim().ToUpperInvariant();
            if (string.Equals(candidate, TargetWord, StringComparison.Ordinal))
            {
                for (int i = 0; i < _wordLetters.Length && i < TargetWord.Length; i++)
                {
                    _wordLetters[i] = TargetWord[i];
                }

                IsWordSolved = true;
                LastFeedbackMessage = "MODULE 1 DECIPHERED (TØNT)!";
                LastFeedbackWasPenalty = false;
                PenaltyFlashTimer = 0f;
                WordPenaltyFlashTimer = 0f;
                Refresh3DVisuals();
                StateChanged?.Invoke();
                return true;
            }

            WordPenaltyFlashTimer = 1.6f;
            ApplyPenalty("WRONG WORD! -01:00 PENALTY");
            return false;
        }

        /// <summary>Returns the current state (true = ON) of contact <paramref name="index"/> (0..9).</summary>
        public bool GetContact(int index)
        {
            return index >= 0 && index < _contacts.Length && _contacts[index];
        }

        /// <summary>Returns the target state (true = ON) of contact <paramref name="index"/> (0..9).</summary>
        public bool GetTargetContact(int index)
        {
            return index >= 0 && index < _targetContacts.Length && _targetContacts[index];
        }

        /// <summary>Toggles on/off contact <paramref name="index"/> (0..9).</summary>
        public void ToggleContact(int index)
        {
            if (IsContactsSolved || IsDisarmed || IsExploded || index < 0 || index >= _contacts.Length)
            {
                return;
            }

            _contacts[index] = !_contacts[index];
            Refresh3DVisuals();
            StateChanged?.Invoke();
        }

        /// <summary>Sets on/off contact <paramref name="index"/> (0..9) explicitly.</summary>
        public void SetContact(int index, bool isOn)
        {
            if (IsContactsSolved || IsDisarmed || IsExploded || index < 0 || index >= _contacts.Length)
            {
                return;
            }

            if (_contacts[index] == isOn)
            {
                return;
            }

            _contacts[index] = isOn;
            Refresh3DVisuals();
            StateChanged?.Invoke();
        }

        /// <summary>Checks the 10 on/off contacts against the target sequence; deducts 1 minute if wrong.</summary>
        public bool TrySubmitContacts()
        {
            if (IsDisarmed || IsExploded)
            {
                return IsContactsSolved;
            }

            if (IsContactsSolved)
            {
                return true;
            }

            for (int i = 0; i < ContactCount; i++)
            {
                if (_contacts[i] != _targetContacts[i])
                {
                    ApplyPenalty("WRONG CONTACTS! -01:00 PENALTY");
                    return false;
                }
            }

            IsContactsSolved = true;
            LastFeedbackMessage = "MODULE 2 CONTACTS LOCKED!";
            LastFeedbackWasPenalty = false;
            PenaltyFlashTimer = 0f;
            Refresh3DVisuals();
            StateChanged?.Invoke();
            return true;
        }

        /// <summary>Returns whether module slot <paramref name="moduleIndex"/> (0..5) shows as solved.</summary>
        public bool IsModuleSolved(int moduleIndex)
        {
            switch (moduleIndex)
            {
                case 0: return IsWordSolved;
                case 1: return IsContactsSolved;
                case 2: return IsWiresSolved;
                case 3: return IsKeysSolved;
                case 4:
                case 5: return IsDisarmed;
                default: return false;
            }
        }

        /// <summary>Deactivates the sea mine once the implemented decipher modules are solved.</summary>
        public bool TryDeactivate()
        {
            if (!CanDeactivate)
            {
                return false;
            }

            IsDisarmed = true;
            LastFeedbackMessage = "ATOM BOMB DEACTIVATED — BEACH SAFE!";
            LastFeedbackWasPenalty = false;
            PenaltyFlashTimer = 0f;
            Refresh3DVisuals();
            StateChanged?.Invoke();
            return true;
        }

        /// <summary>Resets timer, decipher state, and 3D indicators back to the 1-hour armed state.</summary>
        public void ResetBomb()
        {
            RemainingSeconds = DefaultDurationSeconds;
            IsDisarmed = false;
            IsExploded = false;
            IsWordSolved = false;
            IsContactsSolved = false;
            IsWiresSolved = false;
            WiresCutInSequence = 0;
            // test reset: hand the turned keys back by dropping them beside the mine
            for (int k = 0; k < _insertedKeys.Count; k++)
            {
                PickupItem key = _insertedKeys[k];
                if (key == null) continue;
                key.transform.SetParent(transform.parent, true);
                key.transform.position = GetInspectStandPoint() + transform.right * (0.25f * (k - 1)) + Vector3.up * 0.05f;
                key.gameObject.SetActive(true);
            }

            _insertedKeys.Clear();
            KeyRejectFlashTimer = 0f;
            KeyRejectSlot = -1;
            Array.Clear(_wiresCut, 0, _wiresCut.Length);
            PenaltyFlashTimer = 0f;
            WordPenaltyFlashTimer = 0f;
            LastFeedbackMessage = "DECIPHER MODULES TO DEACTIVATE";
            LastFeedbackWasPenalty = false;

            for (int i = 0; i < _wordLetters.Length; i++)
            {
                _wordLetters[i] = 'A';
            }

            for (int i = 0; i < _contacts.Length; i++)
            {
                _contacts[i] = false;
            }

            Refresh3DVisuals();
            StateChanged?.Invoke();
        }

        /// <summary>True when <paramref name="motor"/> is currently bending down inspecting this bomb.</summary>
        public bool IsInspecting(CharacterMotor motor)
        {
            return motor != null && InspectingCharacter == motor;
        }

        /// <summary>True when <paramref name="motor"/> is within interaction reach of the sea mine.</summary>
        public bool IsCharacterInReach(CharacterMotor motor)
        {
            if (motor == null)
            {
                return false;
            }

            return PlanarDistance(motor.transform.position, transform.position) <= interactReach;
        }

        /// <summary>Returns the grounded world spot directly in front of the mine's control panel.</summary>
        public Vector3 GetInspectStandPoint()
        {
            Vector3 worldOffset = transform.TransformDirection(inspectStandOffsetLocal);
            worldOffset.y = 0f;
            if (worldOffset.sqrMagnitude < 0.01f)
            {
                worldOffset = new Vector3(-1.32f, 0f, 0f);
            }

            Vector3 point = transform.position + worldOffset;
            if (InspectingCharacter != null && InspectingCharacter.Area != null)
            {
                point = InspectingCharacter.Area.Clamp(point);
            }
            else if (_party != null && _party.ActiveCharacter != null && _party.ActiveCharacter.Area != null)
            {
                point = _party.ActiveCharacter.Area.Clamp(point);
            }

            point.y = SampleGroundY(point, transform.position.y);
            return point;
        }

        /// <summary>Requests that <paramref name="motor"/> walk to and inspect the bomb, or stand up if already inspecting.</summary>
        public void OnInteractRequested(CharacterMotor motor)
        {
            if (motor == null)
            {
                return;
            }

            if (InspectingCharacter == motor)
            {
                StopInspection();
                return;
            }

            if (IsCharacterInReach(motor))
            {
                StartInspection(motor);
                return;
            }

            _approachingCharacter = motor;
            motor.SetDestination(GetInspectStandPoint(), sprint: false);
        }

        /// <summary>Cancels any pending approach walk toward the bomb when the player takes manual movement elsewhere.</summary>
        public void CancelPendingApproach(CharacterMotor motor)
        {
            if (_approachingCharacter == motor)
            {
                _approachingCharacter = null;
            }
        }

        /// <summary>Places <paramref name="motor"/> into the bent-down pose at the mine and activates 1st-person decipher view.</summary>
        public void StartInspection(CharacterMotor motor)
        {
            if (motor == null)
            {
                return;
            }

            ResolveSceneRefs();
            _approachingCharacter = null;

            if (InspectingCharacter != null && InspectingCharacter != motor)
            {
                RestoreBoneOverrides();
            }

            InspectingCharacter = motor;

            Vector3 standPos = GetInspectStandPoint();
            if (motor.Area != null)
            {
                standPos = motor.Area.Clamp(standPos);
            }

            standPos.y = SampleGroundY(standPos, transform.position.y);
            Vector3 toMine = transform.position - standPos;
            toMine.y = 0f;
            Quaternion lookRot = toMine.sqrMagnitude > 1e-4f
                ? Quaternion.LookRotation(toMine.normalized, Vector3.up)
                : motor.transform.rotation;

            CharacterController cc = motor.GetComponent<CharacterController>();
            if (cc != null) cc.enabled = false;
            motor.transform.SetPositionAndRotation(standPos, lookRot);
            if (cc != null) cc.enabled = true;
            Physics.SyncTransforms();

            motor.Stop();
            motor.SetManualInput(Vector2.zero, false);

            ApplyInspectionPose();
            SyncInspectionCamera(snapCamera: true);

            StateChanged?.Invoke();
            _party?.NotifyExternalStateChanged();
        }

        /// <summary>Exits bomb inspection, restoring standing posture and third-person/normal camera.</summary>
        public void StopInspection()
        {
            ResolveSceneRefs();
            _approachingCharacter = null;

            RestoreBoneOverrides();
            InspectingCharacter = null;

            if (_cameraRig != null)
            {
                _cameraRig.ClearInspectionOverride(snap: true);
            }

            StateChanged?.Invoke();
            _party?.NotifyExternalStateChanged();
        }

        /// <summary>Applies the bending-down bone pose onto <see cref="InspectingCharacter"/> for this frame.</summary>
        public void ApplyInspectionPose()
        {
            RestoreBoneOverrides();

            if (InspectingCharacter == null)
            {
                return;
            }

            InspectingCharacter.Stop();
            InspectingCharacter.SetManualInput(Vector2.zero, false);

            Vector3 toMine = transform.position - InspectingCharacter.transform.position;
            toMine.y = 0f;
            if (toMine.sqrMagnitude > 1e-4f)
            {
                InspectingCharacter.transform.rotation = Quaternion.LookRotation(toMine.normalized, Vector3.up);
            }

            CharacterBones bones = GetOrResolveBones(InspectingCharacter);
            Transform charTf = InspectingCharacter.transform;
            Vector3 right = charTf.right;
            Vector3 up = charTf.up;
            Vector3 forward = charTf.forward;

            // Bend knees and pitch torso forward so the character squats/leans closely over the low sea-mine hatch.
            Vector3 hipsShift = -up * 0.25f - forward * 0.04f;
            RecordAndRotateWorld(bones.Hips, Quaternion.AngleAxis(22f, right), trackPos: true, worldPosDelta: hipsShift);
            RecordAndRotateWorld(bones.Spine, Quaternion.AngleAxis(22f, right));
            RecordAndRotateWorld(bones.Chest, Quaternion.AngleAxis(20f, right));
            RecordAndRotateWorld(bones.Neck, Quaternion.AngleAxis(-14f, right));
            RecordAndRotateWorld(bones.Head, Quaternion.AngleAxis(-12f, right));

            RecordAndRotateWorld(bones.LeftUpperLeg, Quaternion.AngleAxis(-68f, right));
            RecordAndRotateWorld(bones.LeftLowerLeg, Quaternion.AngleAxis(92f, right));
            RecordAndRotateWorld(bones.LeftFoot, Quaternion.AngleAxis(-46f, right));

            RecordAndRotateWorld(bones.RightUpperLeg, Quaternion.AngleAxis(-68f, right));
            RecordAndRotateWorld(bones.RightLowerLeg, Quaternion.AngleAxis(92f, right));
            RecordAndRotateWorld(bones.RightFoot, Quaternion.AngleAxis(-46f, right));

            Vector3 panelCenter = ControlPanelWorldCenter;
            Vector3 leftTarget = Vector3.Lerp(
                charTf.position + up * 0.56f + forward * 0.44f - right * 0.20f,
                panelCenter - right * 0.16f,
                0.55f);
            Vector3 rightTarget = Vector3.Lerp(
                charTf.position + up * 0.56f + forward * 0.44f + right * 0.20f,
                panelCenter + right * 0.16f,
                0.55f);

            Vector3 leftPole = -up - right * 0.45f - forward * 0.20f;
            Vector3 rightPole = -up + right * 0.45f - forward * 0.20f;

            SolveAndRecordArm(bones.LeftUpperArm, bones.LeftLowerArm, bones.LeftHand, leftTarget, leftPole);
            SolveAndRecordArm(bones.RightUpperArm, bones.RightLowerArm, bones.RightHand, rightTarget, rightPole);

            _posedCharacter = InspectingCharacter;
        }

        /// <summary>Synchronizes the camera override depending on whether the inspecting character is currently active.</summary>
        public void SyncInspectionCamera(bool snapCamera)
        {
            ResolveSceneRefs();
            if (_cameraRig == null)
            {
                return;
            }

            if (InspectingCharacter == null || _party == null || _party.ActiveCharacter != InspectingCharacter)
            {
                if (_cameraRig.IsInspectingOverride)
                {
                    _cameraRig.ClearInspectionOverride(snap: snapCamera);
                }

                return;
            }

            Transform charTf = InspectingCharacter.transform;
            Vector3 forward = charTf.forward;
            forward.y = 0f;
            forward = forward.sqrMagnitude > 1e-4f ? forward.normalized : Vector3.forward;

            // Bent-down first-person eye vantage looking closely into the sea mine's control panel.
            Vector3 eyePos = charTf.position + Vector3.up * 1.04f + forward * 0.38f;
            Vector3 lookTarget = ControlPanelWorldCenter + Vector3.up * 0.04f;
            Vector3 lookDir = lookTarget - eyePos;
            Quaternion eyeRot = lookDir.sqrMagnitude > 1e-5f
                ? Quaternion.LookRotation(lookDir.normalized, Vector3.up)
                : charTf.rotation;

            _cameraRig.SetInspectionOverride(charTf, eyePos, eyeRot, 56f, snap: snapCamera);
        }

        private void UpdatePendingApproach()
        {
            if (_approachingCharacter == null)
            {
                return;
            }

            if (_party != null && _party.ActiveCharacter != _approachingCharacter)
            {
                _approachingCharacter = null;
                return;
            }

            if (IsCharacterInReach(_approachingCharacter))
            {
                CharacterMotor arrived = _approachingCharacter;
                _approachingCharacter = null;
                StartInspection(arrived);
                return;
            }

            if (!_approachingCharacter.HasDestination)
            {
                _approachingCharacter = null;
            }
        }

        private void ApplyPenalty(string reason)
        {
            RemainingSeconds = Mathf.Max(0f, RemainingSeconds - WrongAttemptPenaltySeconds);
            PenaltyFlashTimer = 1.6f;
            LastFeedbackMessage = reason;
            LastFeedbackWasPenalty = true;

            if (RemainingSeconds <= 0f)
            {
                TriggerExplosion();
                return;
            }

            StateChanged?.Invoke();
        }

        private void TriggerExplosion()
        {
            if (IsExploded || IsDisarmed)
            {
                return;
            }

            IsExploded = true;
            RemainingSeconds = 0f;
            PenaltyFlashTimer = 2.5f;
            LastFeedbackMessage = "ATOM BOMB DETONATED!";
            LastFeedbackWasPenalty = true;
            Refresh3DVisuals();
            StateChanged?.Invoke();
        }

        private void Refresh3DVisuals()
        {
            for (int i = 0; i < contactSwitchLevers.Length && i < ContactCount; i++)
            {
                Transform lever = contactSwitchLevers[i];
                if (lever == null)
                {
                    continue;
                }

                bool on = _contacts[i];
                lever.localRotation = Quaternion.Euler(on ? -32f : 32f, 0f, 0f);
            }

            for (int i = 0; i < moduleStatusLights.Length; i++)
            {
                Renderer r = moduleStatusLights[i];
                if (r == null)
                {
                    continue;
                }

                bool solved = IsModuleSolved(i);
                Material targetMat = solved
                    ? ledGreenMaterial
                    : (i < 4 ? ledRedMaterial : ledAmberMaterial);
                if (targetMat != null)
                {
                    r.sharedMaterial = targetMat;
                }
            }

            for (int i = 0; i < keyholeLights.Length; i++)
            {
                Renderer r = keyholeLights[i];
                if (r == null) continue;
                Material m = i < _insertedKeys.Count ? ledGreenMaterial
                    : (i == KeyRejectSlot && KeyRejectFlashTimer > 0f ? ledRedMaterial : keyholeOffMaterial);
                if (m != null) r.sharedMaterial = m;
            }

            for (int i = 0; i < keyholeKeys.Length; i++)
            {
                if (keyholeKeys[i] != null) keyholeKeys[i].SetActive(i < _insertedKeys.Count);
            }

            UpdateBeaconBlink();
        }

        private void UpdateBeaconBlink()
        {
            if (armedBeaconRenderer == null)
            {
                return;
            }

            if (IsDisarmed)
            {
                if (ledGreenMaterial != null) armedBeaconRenderer.sharedMaterial = ledGreenMaterial;
                return;
            }

            if (IsExploded)
            {
                if (ledRedMaterial != null) armedBeaconRenderer.sharedMaterial = ledRedMaterial;
                return;
            }

            float period = PenaltyFlashTimer > 0f ? 0.16f : 1.0f;
            bool lit = Mathf.Repeat(_beaconClock, period) < period * 0.45f;
            Material mat = lit ? ledRedMaterial : ledAmberMaterial;
            if (mat != null)
            {
                armedBeaconRenderer.sharedMaterial = mat;
            }
        }

        private void ResolveSceneRefs()
        {
            if (_party == null)
            {
                _party = FindAnyObjectByType<PartyController>();
                if (_party != null && isActiveAndEnabled)
                {
                    BindPartyEvents(true);
                }
            }

            if (_cameraRig == null)
            {
                _cameraRig = FindAnyObjectByType<ThirdPersonCameraRig>();
            }
        }

        private void BindPartyEvents(bool subscribe)
        {
            if (_party == null)
            {
                return;
            }

            _party.StateChanged -= OnPartyStateChanged;
            if (subscribe)
            {
                _party.StateChanged += OnPartyStateChanged;
            }
        }

        private void OnPartyStateChanged()
        {
            if (InspectingCharacter != null)
            {
                SyncInspectionCamera(snapCamera: true);
            }
        }

        private CharacterBones GetOrResolveBones(CharacterMotor motor)
        {
            if (_boneCache.TryGetValue(motor, out CharacterBones cached))
            {
                return cached;
            }

            var map = new Dictionary<string, Transform>(StringComparer.Ordinal);
            foreach (Transform t in motor.GetComponentsInChildren<Transform>(true))
            {
                if (!map.ContainsKey(t.name))
                {
                    map[t.name] = t;
                }
            }

            var bones = new CharacterBones();
            map.TryGetValue("Hips", out bones.Hips);
            map.TryGetValue("Spine", out bones.Spine);
            map.TryGetValue("Chest", out bones.Chest);
            map.TryGetValue("Neck", out bones.Neck);
            map.TryGetValue("Head", out bones.Head);
            map.TryGetValue("LeftUpperLeg", out bones.LeftUpperLeg);
            map.TryGetValue("LeftLowerLeg", out bones.LeftLowerLeg);
            map.TryGetValue("LeftFoot", out bones.LeftFoot);
            map.TryGetValue("RightUpperLeg", out bones.RightUpperLeg);
            map.TryGetValue("RightLowerLeg", out bones.RightLowerLeg);
            map.TryGetValue("RightFoot", out bones.RightFoot);
            map.TryGetValue("LeftUpperArm", out bones.LeftUpperArm);
            map.TryGetValue("LeftLowerArm", out bones.LeftLowerArm);
            map.TryGetValue("LeftHand", out bones.LeftHand);
            map.TryGetValue("RightUpperArm", out bones.RightUpperArm);
            map.TryGetValue("RightLowerArm", out bones.RightLowerArm);
            map.TryGetValue("RightHand", out bones.RightHand);
            _boneCache[motor] = bones;
            return bones;
        }

        private void RestoreBoneOverrides()
        {
            for (int i = _boneOverrides.Count - 1; i >= 0; i--)
            {
                BoneOverride entry = _boneOverrides[i];
                if (entry.Bone == null)
                {
                    continue;
                }

                if (entry.Bone.localRotation == entry.PostLocalRot)
                {
                    entry.Bone.localRotation = entry.PreLocalRot;
                }

                if (entry.TrackPos && entry.Bone.localPosition == entry.PostLocalPos)
                {
                    entry.Bone.localPosition = entry.PreLocalPos;
                }
            }

            _boneOverrides.Clear();
            _posedCharacter = null;
        }

        private void RecordAndRotateWorld(Transform bone, Quaternion worldDelta, bool trackPos = false, Vector3 worldPosDelta = default)
        {
            if (bone == null)
            {
                return;
            }

            Quaternion preRot = bone.localRotation;
            Vector3 prePos = bone.localPosition;

            if (trackPos && worldPosDelta != Vector3.zero)
            {
                bone.position += worldPosDelta;
            }

            bone.rotation = worldDelta * bone.rotation;

            _boneOverrides.Add(new BoneOverride
            {
                Bone = bone,
                PreLocalRot = preRot,
                PostLocalRot = bone.localRotation,
                TrackPos = trackPos,
                PreLocalPos = prePos,
                PostLocalPos = bone.localPosition,
            });
        }

        private void SolveAndRecordArm(Transform upper, Transform lower, Transform hand, Vector3 target, Vector3 pole)
        {
            if (upper == null || lower == null || hand == null)
            {
                return;
            }

            Quaternion preUpper = upper.localRotation;
            Quaternion preLower = lower.localRotation;

            BeerDrinker.SolveArm(upper, lower, hand, target, pole);

            _boneOverrides.Add(new BoneOverride
            {
                Bone = upper,
                PreLocalRot = preUpper,
                PostLocalRot = upper.localRotation,
            });
            _boneOverrides.Add(new BoneOverride
            {
                Bone = lower,
                PreLocalRot = preLower,
                PostLocalRot = lower.localRotation,
            });
        }

        private static float PlanarDistance(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x;
            float dz = a.z - b.z;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        private static float SampleGroundY(Vector3 worldXZ, float fallbackY)
        {
            Vector3 origin = new Vector3(worldXZ.x, fallbackY + 4f, worldXZ.z);
            RaycastHit[] hits = Physics.RaycastAll(origin, Vector3.down, 16f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            if (hits != null && hits.Length > 0)
            {
                Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
                for (int i = 0; i < hits.Length; i++)
                {
                    Collider col = hits[i].collider;
                    if (col == null || col.gameObject.layer == 2)
                    {
                        continue;
                    }

                    if (col.GetComponentInParent<CharacterMotor>() != null ||
                        col.GetComponentInParent<PickupItem>() != null ||
                        col.GetComponentInParent<SeaMineBomb>() != null)
                    {
                        continue;
                    }

                    return hits[i].point.y;
                }
            }

            return fallbackY;
        }
    }
}
