using System;
using System.Collections.Generic;
using Arnafjordur.Core;
using Arnafjordur.Intro;
using UnityEngine;
using UnityEngine.Rendering;

namespace Arnafjordur.Characters
{
    /// <summary>
    /// Interactive fishing rod carried in one hand, extended from its stowed trunk form when picked up,
    /// and used to cast a bobber into the fjord or Stórá river, detect bites, and reel in fish.
    /// </summary>
    [DefaultExecutionOrder(20)]
    public sealed class FishingRod : MonoBehaviour, IUsableItem
    {
        /// <summary>Lifecycle phase of the fishing rod.</summary>
        public enum RodState
        {
            Idle = 0,
            Casting = 1,
            Fishing = 2,
            Bite = 3,
            Reeling = 4,
        }

        private const float CastDuration = 0.55f;
        private const float CastWindupEnd = 0.18f;
        private const float BiteDelaySeconds = 2.2f;
        private const float BiteWindowSeconds = 4.5f;
        private const float ReelDuration = 0.75f;
        private const float MaxLineDistance = 15f;
        private const float CatchDisplaySeconds = 6.0f;
        private const float RippleLifetimeSeconds = 0.9f;
        private const int LineSegments = 8;

        /// <summary>Probability (20%) of reeling in the wire-cutting clue note instead of a fish.</summary>
        public const float NoteCatchChance = 0.20f;

        /// <summary>Clue written on the fished-up note pointing to the radio for the wire colours.</summary>
        public const string WireClueText = "Lurtað eftir radionum, so finna tit litirnar";

        private static readonly float[] WaterProbeDistances = { 7.5f, 6.0f, 9.5f, 4.8f, 11.5f };

        private struct ActiveRipple
        {
            public Transform Transform;
            public Material Material;
            public float Age;
            public float MaxRadius;
        }

        private static Material s_rippleBaseMaterial;
        private static Vector3[] s_cachedRiverVertices;
        private static bool s_riverCacheAttempted;

        [SerializeField] private Transform rodRig;
        [SerializeField] private Transform telescopicSection;
        [SerializeField] private Transform rodTip;
        [SerializeField] private Transform reelCrank;
        [SerializeField] private Transform bobber;
        [SerializeField] private Transform caughtFish;
        [SerializeField] private LineRenderer lineRenderer;
        [SerializeField] private float seaLevel = -89.2f;

        private readonly List<ActiveRipple> _activeRipples = new List<ActiveRipple>();

        private PickupItem _pickup;
        private CharacterItemHolder _cachedHolder;
        private Transform _holderUpperArm;
        private Transform _holderLowerArm;
        private Transform _holderHand;
        private float _stateElapsed;
        private float _displayCatchTimer;
        private float _nextAmbientRippleTime;
        private float _nextSplashTime;
        private bool _castInWater;
        private bool _hookedNote;
        private bool? _nextCatchNoteOverride;
        private Vector3 _castOriginPos;
        private Vector3 _castTargetPos;
        private Vector3 _bobberLandedPos;
        private Vector3 _reelStartPos;

        /// <summary>Current fishing state.</summary>
        public RodState State { get; private set; }

        /// <summary>True while the line and bobber are cast out or being reeled in.</summary>
        public bool IsLineOut => State != RodState.Idle;

        /// <summary>True when reeling in a hooked fish.</summary>
        public bool HasHookedFish { get; private set; }

        /// <summary>Total number of fish landed with this rod.</summary>
        public int FishCaughtCount { get; private set; }

        /// <summary>True once the wire-clue note has been fished up.</summary>
        public bool HasCaughtNote { get; private set; }

        /// <summary>Most recently fished-up clue note, if any.</summary>
        public ClueNote LastCaughtNote { get; private set; }

        /// <summary>Overrides the next bite's note-vs-fish roll (used by editor verification).</summary>
        public void SetNextCatchNoteOverride(bool? isNote) => _nextCatchNoteOverride = isNote;

        /// <summary>Current world-space position of the bobber.</summary>
        public Vector3 BobberWorldPosition => bobber != null ? bobber.position : transform.position;

        /// <summary>Context-sensitive action verb shown in the HUD button for the current state.</summary>
        public string ActionLabel
        {
            get
            {
                switch (State)
                {
                    case RodState.Idle:
                        return "Cast";
                    case RodState.Bite:
                        return "Fish On! Reel";
                    default:
                        return "Reel In";
                }
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStaticCaches()
        {
            s_cachedRiverVertices = null;
            s_riverCacheAttempted = false;
        }

        /// <summary>Wires procedural child parts and water level (called by scene builder).</summary>
        public void Configure(
            Transform rig,
            Transform telescopic,
            Transform tip,
            Transform crank,
            Transform bobberTransform,
            Transform fishTransform,
            LineRenderer line,
            float waterSeaLevel = -89.2f)
        {
            rodRig = rig;
            telescopicSection = telescopic;
            rodTip = tip;
            reelCrank = crank;
            bobber = bobberTransform;
            caughtFish = fishTransform;
            lineRenderer = line;
            seaLevel = waterSeaLevel;
            ResolveParts();
        }

        /// <summary>Resets the rod to its stowed, idle state and clears temporary water ripples.</summary>
        public void ResetToStowed()
        {
            State = RodState.Idle;
            HasHookedFish = false;
            FishCaughtCount = 0;
            HasCaughtNote = false;
            LastCaughtNote = null;
            _hookedNote = false;
            _nextCatchNoteOverride = null;
            _stateElapsed = 0f;
            _displayCatchTimer = 0f;
            _castInWater = false;
            ClearRipples();

            if (_pickup == null)
            {
                _pickup = GetComponent<PickupItem>();
            }

            if (_pickup != null)
            {
                _pickup.SetUseVerb(ActionLabel);
            }

            ApplyPose();
        }

        /// <summary>Handles use input (U/Space/HUD button): casts out or reels in.</summary>
        public void OnUseItem(CharacterItemHolder user, PickupItem item)
        {
            if (user == null)
            {
                return;
            }

            if (user.TryGetComponent(out CharacterDrunk drunk) && drunk.IsIncapacitated)
            {
                return;
            }

            if (State == RodState.Idle)
            {
                StartCast(user.transform);
            }
            else if (State == RodState.Casting || State == RodState.Fishing || State == RodState.Bite)
            {
                StartReel(hookFish: State == RodState.Bite);
            }
        }

        private void Awake()
        {
            _pickup = GetComponent<PickupItem>();
            ResolveParts();
        }

        private void OnDisable()
        {
            ClearRipples();
        }

        private void OnDestroy()
        {
            ClearRipples();
        }

        private void LateUpdate()
        {
            if (!Application.isPlaying)
            {
                return;
            }

            Tick(Time.deltaTime);
            ApplyPose();
        }

        /// <summary>Advances casting, bite timing, reeling, and ripple lifetime deterministically.</summary>
        public void Tick(float dt)
        {
            if (dt <= 0f)
            {
                return;
            }

            if (_pickup == null)
            {
                _pickup = GetComponent<PickupItem>();
            }

            ResolveParts();
            UpdateActiveRipples(dt);

            if (_pickup != null && !_pickup.IsHeld)
            {
                if (State != RodState.Idle)
                {
                    State = RodState.Idle;
                    HasHookedFish = false;
                    _stateElapsed = 0f;
                }

                _displayCatchTimer = 0f;
                if (caughtFish != null && caughtFish.gameObject.activeSelf)
                {
                    caughtFish.gameObject.SetActive(false);
                }

                _pickup.SetUseVerb(ActionLabel);
                return;
            }

            if (_displayCatchTimer > 0f && State == RodState.Idle)
            {
                _displayCatchTimer = Mathf.Max(0f, _displayCatchTimer - dt);
                if (_displayCatchTimer <= 0f && caughtFish != null)
                {
                    caughtFish.gameObject.SetActive(false);
                }
            }

            _stateElapsed += dt;

            switch (State)
            {
                case RodState.Casting:
                    if (_stateElapsed >= CastDuration)
                    {
                        _bobberLandedPos = _castTargetPos;
                        if (_castInWater)
                        {
                            State = RodState.Fishing;
                            _stateElapsed = 0f;
                            _nextAmbientRippleTime = 1.2f;
                            SpawnWaterRipple(_bobberLandedPos, 0.55f);
                        }
                        else
                        {
                            State = RodState.Reeling;
                            _stateElapsed = 0f;
                            _reelStartPos = _bobberLandedPos;
                            HasHookedFish = false;
                        }
                    }
                    break;

                case RodState.Fishing:
                    if (_pickup != null && _pickup.Holder != null)
                    {
                        Vector3 holderPos = _pickup.Holder.transform.position;
                        float planarDist = Vector2.Distance(
                            new Vector2(holderPos.x, holderPos.z),
                            new Vector2(_bobberLandedPos.x, _bobberLandedPos.z));
                        if (planarDist > MaxLineDistance)
                        {
                            StartReel(false);
                            break;
                        }
                    }

                    if (_stateElapsed >= _nextAmbientRippleTime)
                    {
                        _nextAmbientRippleTime = _stateElapsed + 1.2f;
                        SpawnWaterRipple(_bobberLandedPos, 0.42f);
                    }

                    if (_stateElapsed >= BiteDelaySeconds)
                    {
                        State = RodState.Bite;
                        _stateElapsed = 0f;
                        _nextSplashTime = 0f;
                        SpawnWaterRipple(_bobberLandedPos, 0.75f);
                    }
                    break;

                case RodState.Bite:
                    if (_stateElapsed >= _nextSplashTime)
                    {
                        _nextSplashTime = _stateElapsed + 0.45f;
                        SpawnWaterRipple(_bobberLandedPos, 0.85f);
                    }

                    if (_stateElapsed >= BiteWindowSeconds)
                    {
                        State = RodState.Fishing;
                        _stateElapsed = 0f;
                        _nextAmbientRippleTime = 1.2f;
                    }
                    break;

                case RodState.Reeling:
                    if (reelCrank != null)
                    {
                        reelCrank.Rotate(Vector3.forward, 540f * dt, Space.Self);
                    }

                    if (_stateElapsed >= ReelDuration)
                    {
                        if (HasHookedFish)
                        {
                            if (_hookedNote)
                            {
                                GiveCaughtNoteToHolder();
                            }
                            else
                            {
                                FishCaughtCount++;
                                _displayCatchTimer = CatchDisplaySeconds;
                            }
                        }

                        _hookedNote = false;
                        HasHookedFish = false;
                        State = RodState.Idle;
                        _stateElapsed = 0f;
                    }
                    break;
            }

            if (_pickup != null)
            {
                _pickup.SetUseVerb(ActionLabel);
            }
        }

        /// <summary>Updates telescopic extension, rod pitch, character arm pose, bobber placement, and fishing line.</summary>
        public void ApplyPose()
        {
            if (_pickup == null)
            {
                _pickup = GetComponent<PickupItem>();
            }

            ResolveParts();

            bool held = _pickup != null && _pickup.IsHeld;
            if (!held)
            {
                if (rodRig != null)
                {
                    rodRig.localPosition = new Vector3(0.03f, -0.01f, -0.12f);
                    rodRig.localRotation = Quaternion.Euler(82f, -32f, 0f);
                }

                if (telescopicSection != null)
                {
                    telescopicSection.localScale = new Vector3(1f, 0.42f, 1f);
                }

                Vector3 stowedTip = rodTip != null ? rodTip.position : transform.position + transform.up * 0.55f;
                if (bobber != null)
                {
                    bobber.position = stowedTip - Vector3.up * 0.08f;
                    bobber.rotation = Quaternion.identity;
                }

                if (caughtFish != null)
                {
                    caughtFish.gameObject.SetActive(false);
                }

                UpdateLineRenderer(stowedTip, bobber != null ? bobber.position : stowedTip, 0.01f);
                return;
            }

            if (telescopicSection != null)
            {
                telescopicSection.localScale = Vector3.one;
            }

            ApplyActiveHolderArmPose();

            float rodPitchDeg = ComputeRodPitchDegrees();
            if (rodRig != null)
            {
                rodRig.localPosition = Vector3.zero;
                rodRig.localRotation = Quaternion.Euler(0f, 0f, rodPitchDeg);
            }

            Vector3 tipPos = rodTip != null ? rodTip.position : transform.position + transform.up * 1.45f;
            Vector3 bobberPos = ComputeBobberWorldPosition(tipPos);
            bool showFish = (State == RodState.Reeling && HasHookedFish && !_hookedNote) || (State == RodState.Idle && _displayCatchTimer > 0f);

            if (bobber != null)
            {
                bobber.position = bobberPos;
                bobber.rotation = Quaternion.identity;
            }

            if (caughtFish != null)
            {
                caughtFish.gameObject.SetActive(showFish);
                if (showFish)
                {
                    float wiggleTime = Application.isPlaying ? Time.time : _stateElapsed;
                    float wiggle = Mathf.Sin(wiggleTime * 9f) * 14f;
                    caughtFish.localPosition = new Vector3(0f, -0.14f, 0f);
                    caughtFish.localRotation = Quaternion.Euler(wiggle, 0f, wiggle * 0.4f);
                }
            }

            float sagFactor = State == RodState.Bite || State == RodState.Reeling ? 0.025f : 0.08f;
            UpdateLineRenderer(tipPos, bobberPos, sagFactor);
        }

        private void StartCast(Transform caster)
        {
            ResolveParts();
            Vector3 fwd = caster.forward;
            fwd.y = 0f;
            fwd = fwd.sqrMagnitude > 0.001f ? fwd.normalized : Vector3.forward;

            _castOriginPos = rodTip != null ? rodTip.position : caster.position + Vector3.up * 1.75f + fwd * 0.9f;
            if (TryFindWaterSurface(caster, out Vector3 waterPos))
            {
                _castInWater = true;
                _castTargetPos = waterPos;
            }
            else
            {
                _castInWater = false;
                Vector3 landPoint = caster.position + fwd * 4.5f;
                float groundY = SampleGroundY(landPoint, caster.position.y);
                _castTargetPos = new Vector3(landPoint.x, groundY + 0.04f, landPoint.z);
            }

            HasHookedFish = false;
            _displayCatchTimer = 0f;
            _stateElapsed = 0f;
            _nextAmbientRippleTime = 1.2f;
            _nextSplashTime = 0f;
            State = RodState.Casting;

            if (_pickup != null)
            {
                _pickup.SetUseVerb(ActionLabel);
            }
        }

        private void StartReel(bool hookFish)
        {
            ResolveParts();
            HasHookedFish = hookFish;
            bool rollNote = _nextCatchNoteOverride ?? (!HasCaughtNote && UnityEngine.Random.value < NoteCatchChance);
            _nextCatchNoteOverride = null;
            _hookedNote = hookFish && !HasCaughtNote && rollNote;
            _reelStartPos = bobber != null ? bobber.position : _bobberLandedPos;
            if (HasHookedFish && _castInWater)
            {
                SpawnWaterRipple(_bobberLandedPos, 0.90f);
            }

            State = RodState.Reeling;
            _stateElapsed = 0f;

            if (_pickup != null)
            {
                _pickup.SetUseVerb(ActionLabel);
            }
        }

        private void GiveCaughtNoteToHolder()
        {
            HasCaughtNote = true;
            ClueNote note = ClueNote.CreateNoteObject("WireClueNote", "Note", WireClueText);
            LastCaughtNote = note;
            PickupItem noteItem = note.GetComponent<PickupItem>();

            CharacterItemHolder holder = _pickup != null ? _pickup.Holder : null;
            if (holder != null)
            {
                if (!holder.TryGetComponent(out CharacterPockets pockets))
                {
                    pockets = holder.gameObject.AddComponent<CharacterPockets>();
                }

                pockets.Add(noteItem);
            }
            else
            {
                note.transform.position = transform.position;
            }

            HouseDoor.Announce("Caught a note! Drop the rod [G] and press [I] to take it out, then [U] to read.");
        }

        private float ComputeRodPitchDegrees()
        {
            switch (State)
            {
                case RodState.Casting:
                    if (_stateElapsed < CastWindupEnd)
                    {
                        float u = Mathf.Clamp01(_stateElapsed / CastWindupEnd);
                        return Mathf.Lerp(36f, -18f, Mathf.SmoothStep(0f, 1f, u));
                    }
                    else
                    {
                        float u = Mathf.Clamp01((_stateElapsed - CastWindupEnd) / Mathf.Max(0.01f, CastDuration - CastWindupEnd));
                        return Mathf.Lerp(-18f, 56f, Mathf.SmoothStep(0f, 1f, u));
                    }

                case RodState.Fishing:
                    return 42f + Mathf.Sin(_stateElapsed * 1.8f) * 1.5f;

                case RodState.Bite:
                    return 46f + Mathf.Sin(_stateElapsed * 24f) * 7f;

                case RodState.Reeling:
                    return 24f + Mathf.Sin(_stateElapsed * 12f) * 3f;

                default:
                    return 36f;
            }
        }

        private Vector3 ComputeBobberWorldPosition(Vector3 tipPos)
        {
            switch (State)
            {
                case RodState.Casting:
                    if (_stateElapsed < CastWindupEnd)
                    {
                        Vector3 charBack = _pickup != null && _pickup.Holder != null
                            ? -_pickup.Holder.transform.forward * 0.14f
                            : Vector3.zero;
                        return tipPos - Vector3.up * 0.34f + charBack;
                    }
                    else
                    {
                        float flightU = Mathf.Clamp01((_stateElapsed - CastWindupEnd) / Mathf.Max(0.01f, CastDuration - CastWindupEnd));
                        Vector3 pos = Vector3.Lerp(_castOriginPos, _castTargetPos, flightU);
                        pos.y += 1.35f * Mathf.Sin(flightU * Mathf.PI);
                        return pos;
                    }

                case RodState.Fishing:
                    return _bobberLandedPos + Vector3.up * (Mathf.Sin(_stateElapsed * 3.2f) * 0.02f);

                case RodState.Bite:
                    return _bobberLandedPos + Vector3.up * (-0.06f * Mathf.Abs(Mathf.Sin(_stateElapsed * 12f)));

                case RodState.Reeling:
                {
                    float reelU = Mathf.Clamp01(_stateElapsed / ReelDuration);
                    Vector3 hangEnd = tipPos - Vector3.up * 0.32f;
                    Vector3 pos = Vector3.Lerp(_reelStartPos, hangEnd, Mathf.SmoothStep(0f, 1f, reelU));
                    pos.y += 0.45f * Mathf.Sin(reelU * Mathf.PI);
                    return pos;
                }

                default:
                {
                    float swayT = Application.isPlaying ? Time.time : _stateElapsed;
                    Vector3 sway = new Vector3(Mathf.Sin(swayT * 2.1f) * 0.018f, 0f, Mathf.Cos(swayT * 1.7f) * 0.018f);
                    return tipPos - Vector3.up * 0.32f + sway;
                }
            }
        }

        private void ApplyActiveHolderArmPose()
        {
            if (State == RodState.Idle || _pickup == null || _pickup.Holder == null)
            {
                return;
            }

            CharacterItemHolder holder = _pickup.Holder;
            if (holder.TryGetComponent(out CharacterDrunk drunk) && drunk.OverridesArmPose)
            {
                return;
            }

            if (_cachedHolder != holder)
            {
                _cachedHolder = holder;
                _holderUpperArm = FindChildByName(holder.transform, "RightUpperArm");
                _holderLowerArm = FindChildByName(holder.transform, "RightLowerArm");
                _holderHand = holder.HandBone != null ? holder.HandBone : FindChildByName(holder.transform, "RightHand");
            }

            if (_holderUpperArm == null || _holderLowerArm == null || _holderHand == null)
            {
                return;
            }

            Transform charRoot = holder.transform;
            Vector3 up = charRoot.up;
            Vector3 fwd = charRoot.forward;
            Vector3 right = charRoot.right;
            Vector3 shoulder = _holderUpperArm.position;

            Vector3 targetFist;
            switch (State)
            {
                case RodState.Casting:
                    if (_stateElapsed < CastWindupEnd)
                    {
                        float u = Mathf.SmoothStep(0f, 1f, _stateElapsed / CastWindupEnd);
                        Vector3 restFist = shoulder - up * 0.25f + fwd * 0.28f + right * 0.06f;
                        Vector3 windFist = shoulder + up * 0.12f + fwd * 0.10f + right * 0.18f;
                        targetFist = Vector3.Lerp(restFist, windFist, u);
                    }
                    else
                    {
                        float u = Mathf.SmoothStep(0f, 1f, (_stateElapsed - CastWindupEnd) / Mathf.Max(0.01f, CastDuration - CastWindupEnd));
                        Vector3 windFist = shoulder + up * 0.12f + fwd * 0.10f + right * 0.18f;
                        Vector3 castFist = shoulder + up * 0.04f + fwd * 0.42f + right * 0.06f;
                        targetFist = Vector3.Lerp(windFist, castFist, u);
                    }
                    break;

                case RodState.Bite:
                    targetFist = shoulder - up * (0.06f + 0.05f * Mathf.Abs(Mathf.Sin(_stateElapsed * 24f))) + fwd * 0.36f + right * 0.08f;
                    break;

                case RodState.Reeling:
                    targetFist = shoulder + up * 0.04f + fwd * 0.30f + right * 0.10f;
                    break;

                default: // Fishing
                    targetFist = shoulder - up * 0.07f + fwd * 0.36f + right * 0.08f;
                    break;
            }

            Vector3 pole = -up + right * 0.45f - fwd * 0.15f;
            for (int pass = 0; pass < 3; pass++)
            {
                BeerDrinker.SolveArm(
                    _holderUpperArm,
                    _holderLowerArm,
                    _holderHand,
                    _holderHand.position + (targetFist - _holderHand.TransformPoint(holder.GripLocal)),
                    pole);
            }

            Vector3 carryAxis = (up + fwd * 0.18f - right * 0.04f).normalized;
            Vector3 across = Vector3.ProjectOnPlane(right, carryAxis);
            if (across.sqrMagnitude < 1e-6f) across = Vector3.ProjectOnPlane(fwd, carryAxis);
            if (across.sqrMagnitude < 1e-6f) across = Vector3.forward;

            Quaternion baseRot = Quaternion.LookRotation(across.normalized, carryAxis) * Quaternion.Euler(_pickup.HeldEulerOffset);
            Vector3 gripPos = _holderHand.TransformPoint(holder.GripLocal) + carryAxis * _pickup.GripBelowCentre;
            transform.SetPositionAndRotation(gripPos, baseRot);
        }

        private void UpdateLineRenderer(Vector3 tipPos, Vector3 bobberPos, float sagFactor)
        {
            if (lineRenderer == null)
            {
                return;
            }

            lineRenderer.useWorldSpace = true;
            lineRenderer.positionCount = LineSegments;
            float dist = Vector3.Distance(tipPos, bobberPos);
            float sag = Mathf.Min(0.45f, dist * sagFactor);

            for (int i = 0; i < LineSegments; i++)
            {
                float t = i / (float)(LineSegments - 1);
                Vector3 pt = Vector3.Lerp(tipPos, bobberPos, t);
                pt.y -= Mathf.Sin(t * Mathf.PI) * sag;
                lineRenderer.SetPosition(i, pt);
            }
        }

        private bool TryFindWaterSurface(Transform caster, out Vector3 waterPos)
        {
            Vector3 fwd = caster.forward;
            fwd.y = 0f;
            fwd = fwd.sqrMagnitude > 0.001f ? fwd.normalized : Vector3.forward;

            for (int i = 0; i < WaterProbeDistances.Length; i++)
            {
                Vector3 sample = caster.position + fwd * WaterProbeDistances[i];
                float groundY = SampleGroundY(sample, caster.position.y);

                if (groundY <= seaLevel - 0.04f)
                {
                    waterPos = new Vector3(sample.x, seaLevel, sample.z);
                    return true;
                }

                if (TryFindRiverSurface(sample, groundY, out float riverY))
                {
                    waterPos = new Vector3(sample.x, riverY, sample.z);
                    return true;
                }
            }

            waterPos = Vector3.zero;
            return false;
        }

        private static bool TryFindRiverSurface(Vector3 sampleXZ, float groundY, out float riverY)
        {
            EnsureRiverCache();
            if (s_cachedRiverVertices == null || s_cachedRiverVertices.Length == 0)
            {
                riverY = 0f;
                return false;
            }

            const float MaxPlanarDistSqr = 2.2f * 2.2f;
            float bestDistSqr = float.PositiveInfinity;
            float bestY = 0f;
            bool found = false;

            for (int i = 0; i < s_cachedRiverVertices.Length; i++)
            {
                Vector3 v = s_cachedRiverVertices[i];
                if (v.y < groundY - 0.15f)
                {
                    continue;
                }

                float dx = v.x - sampleXZ.x;
                float dz = v.z - sampleXZ.z;
                float distSqr = dx * dx + dz * dz;
                if (distSqr <= MaxPlanarDistSqr && distSqr < bestDistSqr)
                {
                    bestDistSqr = distSqr;
                    bestY = v.y;
                    found = true;
                }
            }

            riverY = bestY;
            return found;
        }

        private static void EnsureRiverCache()
        {
            if (s_riverCacheAttempted && s_cachedRiverVertices != null)
            {
                return;
            }

            s_riverCacheAttempted = true;
            GameObject riverGo = GameObject.Find("Exit_River");
            if (riverGo == null || !riverGo.TryGetComponent(out MeshFilter mf) || mf.sharedMesh == null)
            {
                s_cachedRiverVertices = Array.Empty<Vector3>();
                return;
            }

            Vector3[] localVerts = mf.sharedMesh.vertices;
            s_cachedRiverVertices = new Vector3[localVerts.Length];
            Transform riverTf = riverGo.transform;
            for (int i = 0; i < localVerts.Length; i++)
            {
                s_cachedRiverVertices[i] = riverTf.TransformPoint(localVerts[i]);
            }
        }

        private static float SampleGroundY(Vector3 worldXZ, float fallbackY)
        {
            Vector3 origin = new Vector3(worldXZ.x, fallbackY + 6f, worldXZ.z);
            RaycastHit[] hits = Physics.RaycastAll(origin, Vector3.down, 25f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            if (hits != null && hits.Length > 0)
            {
                Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
                for (int i = 0; i < hits.Length; i++)
                {
                    Collider col = hits[i].collider;
                    if (col == null)
                    {
                        continue;
                    }

                    string name = col.gameObject.name;
                    if (col.gameObject.layer == 2 || name == "CarObstacle" || name == "WaterRipple" || name == "Exit_Water")
                    {
                        continue;
                    }

                    if (col.GetComponentInParent<CharacterMotor>() != null || col.GetComponentInParent<PickupItem>() != null)
                    {
                        continue;
                    }

                    return hits[i].point.y;
                }
            }

            return fallbackY;
        }

        private void SpawnWaterRipple(Vector3 waterPos, float maxRadius)
        {
            EnsureRippleMaterial();
            GameObject ring = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            ring.name = "WaterRipple";
            if (ring.TryGetComponent(out Collider col))
            {
                SafeDestroy(col);
            }

            ring.transform.position = new Vector3(waterPos.x, waterPos.y + 0.015f, waterPos.z);
            ring.transform.localScale = new Vector3(0.08f, 0.004f, 0.08f);

            Material instanceMat = s_rippleBaseMaterial != null ? new Material(s_rippleBaseMaterial) : null;
            if (instanceMat != null && ring.TryGetComponent(out Renderer rend))
            {
                rend.sharedMaterial = instanceMat;
                rend.shadowCastingMode = ShadowCastingMode.Off;
            }

            _activeRipples.Add(new ActiveRipple
            {
                Transform = ring.transform,
                Material = instanceMat,
                Age = 0f,
                MaxRadius = maxRadius,
            });
        }

        private void UpdateActiveRipples(float dt)
        {
            for (int i = _activeRipples.Count - 1; i >= 0; i--)
            {
                ActiveRipple ripple = _activeRipples[i];
                if (ripple.Transform == null)
                {
                    SafeDestroy(ripple.Material);
                    _activeRipples.RemoveAt(i);
                    continue;
                }

                ripple.Age += dt;
                if (ripple.Age >= RippleLifetimeSeconds)
                {
                    SafeDestroy(ripple.Transform.gameObject);
                    SafeDestroy(ripple.Material);
                    _activeRipples.RemoveAt(i);
                    continue;
                }

                float u = Mathf.Clamp01(ripple.Age / RippleLifetimeSeconds);
                float d = Mathf.Lerp(0.08f, ripple.MaxRadius * 2f, Mathf.SmoothStep(0f, 1f, u));
                ripple.Transform.localScale = new Vector3(d, 0.003f * (1f - 0.5f * u), d);
                if (ripple.Material != null)
                {
                    Color c = Palette.SeaFoam;
                    c.a = Mathf.Lerp(0.55f, 0f, u);
                    ripple.Material.SetColor("_BaseColor", c);
                }

                _activeRipples[i] = ripple;
            }
        }

        private void ClearRipples()
        {
            for (int i = 0; i < _activeRipples.Count; i++)
            {
                if (_activeRipples[i].Transform != null)
                {
                    SafeDestroy(_activeRipples[i].Transform.gameObject);
                }

                SafeDestroy(_activeRipples[i].Material);
            }

            _activeRipples.Clear();
        }

        private static void EnsureRippleMaterial()
        {
            if (s_rippleBaseMaterial != null)
            {
                return;
            }

            Shader shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            s_rippleBaseMaterial = new Material(shader) { name = "Mat_FishingRipple" };
            s_rippleBaseMaterial.SetFloat("_Surface", 1f);
            s_rippleBaseMaterial.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
            s_rippleBaseMaterial.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
            s_rippleBaseMaterial.SetFloat("_ZWrite", 0f);
            s_rippleBaseMaterial.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            s_rippleBaseMaterial.renderQueue = (int)RenderQueue.Transparent;
            Color c = Palette.SeaFoam;
            c.a = 0.55f;
            s_rippleBaseMaterial.SetColor("_BaseColor", c);
            s_rippleBaseMaterial.SetFloat("_Smoothness", 0.35f);
        }

        private void ResolveParts()
        {
            if (rodRig == null) rodRig = FindChildByName(transform, "RodRig");
            if (telescopicSection == null) telescopicSection = FindChildByName(transform, "TelescopicSection");
            if (rodTip == null) rodTip = FindChildByName(transform, "RodTip");
            if (reelCrank == null) reelCrank = FindChildByName(transform, "ReelCrank");
            if (bobber == null) bobber = FindChildByName(transform, "Bobber");
            if (caughtFish == null) caughtFish = FindChildByName(transform, "CaughtFish");
            if (lineRenderer == null) lineRenderer = GetComponent<LineRenderer>();
        }

        private static Transform FindChildByName(Transform root, string targetName)
        {
            if (root == null)
            {
                return null;
            }

            foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
            {
                if (t.name == targetName)
                {
                    return t;
                }
            }

            return null;
        }

        private static void SafeDestroy(UnityEngine.Object obj)
        {
            if (obj == null)
            {
                return;
            }

            if (Application.isPlaying)
            {
                Destroy(obj);
            }
            else
            {
                DestroyImmediate(obj);
            }
        }
    }
}
