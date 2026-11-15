using System;
using System.Collections.Generic;
using Arnafjordur.Core;
using Arnafjordur.Intro;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Arnafjordur.Characters
{
    /// <summary>
    /// Tracks beer consumption for a playable character and drives progressive drunken effects:
    /// steering weave, sideways stumbles, torso/head sway, puking droplets/puddle, collapsing to the ground,
    /// camera roll/FOV wobble, and active-character URP post-processing.
    /// </summary>
    [DefaultExecutionOrder(-20)]
    public sealed class CharacterDrunk : MonoBehaviour
    {
        public enum DrunkStage
        {
            Sober = 0,
            SlightlyBuzzed = 1,
            Buzzed = 2,
            HardToWalk = 3,
            PukingAndFalling = 4,
        }

        private enum KnockdownPhase
        {
            None = 0,
            Puking = 1,
            Falling = 2,
            Fallen = 3,
            GettingUp = 4,
        }

        private struct BoneOverride
        {
            public Transform Bone;
            public Quaternion PreLocalRot;
            public Quaternion PostLocalRot;
            public bool TrackPos;
            public Vector3 PreLocalPos;
            public Vector3 PostLocalPos;
        }

        private sealed class VomitDroplet
        {
            public Transform Transform;
            public Vector3 Velocity;
            public float GroundY;
            public float Age;
            public float MaxAge;
            public bool Landed;
        }

        private const float PukeEndTime = 2.2f;
        private const float FallEndTime = 2.95f;
        private const float FallenEndTime = 5.8f;
        private const float GetUpEndTime = 7.2f;
        private const float Stage4RepeatMoveSeconds = 8.5f;
        private const float PuddleLifetimeSeconds = 25f;

        private static Volume s_sharedVolume;
        private static VolumeProfile s_sharedProfile;
        private static Vignette s_vignette;
        private static ChromaticAberration s_chromaticAberration;
        private static LensDistortion s_lensDistortion;
        private static ColorAdjustments s_colorAdjustments;
        private static Bloom s_bloom;
        private static Material[] s_vomitMaterials;

        private readonly List<BoneOverride> _boneOverrides = new List<BoneOverride>(16);
        private readonly List<VomitDroplet> _activeDroplets = new List<VomitDroplet>(64);

        private CharacterMotor _motor;
        private CharacterItemHolder _itemHolder;
        private PartyController _party;
        private bool _bonesResolved;
        private Transform _hips;
        private Transform _spine;
        private Transform _chest;
        private Transform _neck;
        private Transform _head;
        private Transform _leftUpperLeg;
        private Transform _leftLowerLeg;
        private Transform _leftFoot;
        private Transform _rightUpperLeg;
        private Transform _rightLowerLeg;
        private Transform _rightFoot;
        private Transform _leftUpperArm;
        private Transform _leftLowerArm;
        private Transform _leftHand;
        private Transform _rightUpperArm;
        private Transform _rightLowerArm;
        private Transform _rightHand;

        private KnockdownPhase _knockdownPhase;
        private float _knockdownElapsed;
        private bool _pendingPukeAndFall;
        private bool _puddleSpawned;
        private Transform _currentPuddleRoot;
        private float _dropletSpawnAccum;

        private float _drunkClock;
        private float _phaseOffset;
        private float _stumbleCooldown = 1.2f;
        private float _stumbleRemaining;
        private float _stumbleSideSign = 1f;
        private float _stumbleMoveTimer;
        private Vector3 _smoothedVelocity;
        private float _currentPukeHeadPitchDeg;

        /// <summary>Fired whenever beer intake, drunk stage, or knockdown phase changes.</summary>
        public event Action StateChanged;

        /// <summary>Total beers consumed by this character (each 5-sip bottle contributes 1.0).</summary>
        public float BeersDrunk { get; private set; }

        /// <summary>Total individual sips consumed by this character.</summary>
        public int SipsDrunk { get; private set; }

        /// <summary>Current intoxication tier derived from <see cref="BeersDrunk"/>.</summary>
        public DrunkStage Stage
        {
            get
            {
                if (BeersDrunk >= 3.99f) return DrunkStage.PukingAndFalling;
                if (BeersDrunk >= 2.99f) return DrunkStage.HardToWalk;
                if (BeersDrunk >= 1.99f) return DrunkStage.Buzzed;
                if (BeersDrunk >= 0.99f) return DrunkStage.SlightlyBuzzed;
                return DrunkStage.Sober;
            }
        }

        /// <summary>True while doubled over retching and puking.</summary>
        public bool IsPuking => _knockdownPhase == KnockdownPhase.Puking;

        /// <summary>True while collapsing or lying sprawled on the ground.</summary>
        public bool IsFallen => _knockdownPhase == KnockdownPhase.Falling || _knockdownPhase == KnockdownPhase.Fallen;

        /// <summary>True whenever puking, falling, lying down, or getting back up prevents walking.</summary>
        public bool IsIncapacitated => _knockdownPhase != KnockdownPhase.None;

        /// <summary>True when <see cref="CharacterDrunk"/> controls both arms instead of <see cref="CharacterItemHolder"/>.</summary>
        public bool OverridesArmPose => _knockdownPhase != KnockdownPhase.None;

        /// <summary>Short readable intoxication state for HUD display.</summary>
        public string StatusLabel
        {
            get
            {
                string beersText = $"{BeersDrunk:0.#} beers";
                if (IsPuking) return $"Puking! ({beersText})";
                if (IsFallen || _knockdownPhase == KnockdownPhase.GettingUp) return $"Fallen! ({beersText})";

                switch (Stage)
                {
                    case DrunkStage.PukingAndFalling: return $"Wasted ({beersText})";
                    case DrunkStage.HardToWalk: return $"Hard to walk ({beersText})";
                    case DrunkStage.Buzzed: return $"Buzzed ({beersText})";
                    case DrunkStage.SlightlyBuzzed: return $"Slightly buzzed ({beersText})";
                    default: return BeersDrunk > 0.01f ? $"Warming up ({beersText})" : "Sober";
                }
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStaticResources()
        {
            s_sharedVolume = null;
            s_sharedProfile = null;
            s_vignette = null;
            s_chromaticAberration = null;
            s_lensDistortion = null;
            s_colorAdjustments = null;
            s_bloom = null;
            s_vomitMaterials = null;
        }

        private void Awake()
        {
            TryGetComponent(out _motor);
            TryGetComponent(out _itemHolder);
            _phaseOffset = Mathf.Abs(gameObject.name.GetHashCode() % 100) * 0.031f;
            ResolveBones();
        }

        private void OnDisable()
        {
            RestoreBoneOverrides();
            ClearSharedVolumeIfOwner();
        }

        private void OnDestroy()
        {
            RestoreBoneOverrides();
            ClearActiveDroplets();
            ClearSharedVolumeIfOwner();
        }

        private void Update()
        {
            Tick(Time.deltaTime);
        }

        /// <summary>Advances intoxication timers, pending knockdown triggers, vomit droplets, and post-processing.</summary>
        public void Tick(float dt)
        {
            if (dt <= 0f)
            {
                return;
            }

            _drunkClock += dt;

            if (_pendingPukeAndFall && (_itemHolder == null || !_itemHolder.IsUsingItem))
            {
                _pendingPukeAndFall = false;
                TriggerPukeAndFall();
            }

            if (IsIncapacitated)
            {
                AdvanceKnockdown(dt);
            }

            UpdateActiveDroplets(dt);
            UpdatePostProcessingVolume();
        }

        private void LateUpdate()
        {
            ApplyBonePose();
        }

        /// <summary>Records one sip of beer (default 0.2 of a 5-sip bottle).</summary>
        public void RecordSip(float beerFraction = 0.2f)
        {
            if (beerFraction <= 0f)
            {
                return;
            }

            SipsDrunk++;
            BeersDrunk += beerFraction;

            if (BeersDrunk >= 3.99f)
            {
                _pendingPukeAndFall = true;
            }

            StateChanged?.Invoke();
        }

        /// <summary>Sets beer intake directly (useful for testing or scenario setup).</summary>
        public void SetBeersDrunk(float beers, bool triggerKnockdownImmediately = false)
        {
            BeersDrunk = Mathf.Max(0f, beers);
            SipsDrunk = Mathf.RoundToInt(BeersDrunk * 5f);
            _stumbleMoveTimer = 0f;

            if (BeersDrunk >= 3.99f)
            {
                if (triggerKnockdownImmediately)
                {
                    _pendingPukeAndFall = false;
                    TriggerPukeAndFall();
                }
                else
                {
                    _pendingPukeAndFall = true;
                }
            }
            else
            {
                _pendingPukeAndFall = false;
                if (IsIncapacitated)
                {
                    _knockdownPhase = KnockdownPhase.None;
                    _knockdownElapsed = 0f;
                    RestoreBoneOverrides();
                }
            }

            UpdatePostProcessingVolume();
            StateChanged?.Invoke();
        }

        /// <summary>Starts the puking, collapsing, lying down, and getting back up sequence.</summary>
        public void TriggerPukeAndFall()
        {
            if (IsIncapacitated)
            {
                return;
            }

            _knockdownPhase = KnockdownPhase.Puking;
            _knockdownElapsed = 0f;
            _stumbleMoveTimer = 0f;
            _stumbleRemaining = 0f;
            _puddleSpawned = false;
            _currentPuddleRoot = null;
            _dropletSpawnAccum = 0f;
            _smoothedVelocity = Vector3.zero;

            if (_motor == null) TryGetComponent(out _motor);
            _motor?.Stop();

            if (_itemHolder == null) TryGetComponent(out _itemHolder);
            if (_itemHolder != null && _itemHolder.HeldItem != null &&
                _itemHolder.HeldItem.TryGetComponent(out BeerBottle bottle) && bottle.IsEmpty)
            {
                Vector3 handPos = _itemHolder.HeldItem.transform.position;
                Quaternion handRot = _itemHolder.HeldItem.transform.rotation;
                PickupItem dropped = _itemHolder.DropHeldItem();
                if (dropped != null)
                {
                    dropped.transform.SetPositionAndRotation(handPos, handRot);
                    bottle.ThrowFrom(transform);
                }
            }

            UpdatePostProcessingVolume();
            StateChanged?.Invoke();
        }

        /// <summary>Modifies the character's desired planar velocity based on drunkenness and stumble impulses.</summary>
        public Vector3 ModifyDesiredVelocity(Vector3 desiredVelocity, bool wantsSprint, float dt)
        {
            if (IsIncapacitated)
            {
                _smoothedVelocity = Vector3.zero;
                return Vector3.zero;
            }

            DrunkStage stage = Stage;
            if (stage == DrunkStage.Sober)
            {
                _smoothedVelocity = desiredVelocity;
                return desiredVelocity;
            }

            float speed = new Vector2(desiredVelocity.x, desiredVelocity.z).magnitude;
            if (speed <= 0.001f)
            {
                float decay = stage >= DrunkStage.HardToWalk ? 4.5f : 10f;
                _smoothedVelocity = Vector3.Lerp(_smoothedVelocity, Vector3.zero, 1f - Mathf.Exp(-decay * dt));
                return _smoothedVelocity.sqrMagnitude < 0.0004f ? Vector3.zero : _smoothedVelocity;
            }

            float t = _drunkClock + _phaseOffset;
            float weaveDeg;
            float speedScale;
            float responsiveness;

            switch (stage)
            {
                case DrunkStage.SlightlyBuzzed:
                    speedScale = 1.0f;
                    weaveDeg = Mathf.Sin(t * 2.1f) * 6f + Mathf.Sin(t * 3.4f) * 3f;
                    responsiveness = 14f;
                    break;

                case DrunkStage.Buzzed:
                    speedScale = 0.90f * (0.94f + 0.06f * Mathf.Sin(t * 2.6f));
                    weaveDeg = Mathf.Sin(t * 2.4f) * 14f + Mathf.Sin(t * 4.1f) * 7f;
                    responsiveness = 7.5f;
                    break;

                case DrunkStage.HardToWalk:
                    speedScale = ComputeHardToWalkSpeedScale(t, wantsSprint, speed);
                    weaveDeg = Mathf.Sin(t * 2.7f) * 28f + Mathf.Sin(t * 4.9f) * 14f;
                    responsiveness = 3.8f;
                    break;

                default: // DrunkStage.PukingAndFalling (while standing between collapses)
                    speedScale = ComputeHardToWalkSpeedScale(t, wantsSprint, speed) * 0.92f;
                    weaveDeg = (Mathf.Sin(t * 2.7f) * 28f + Mathf.Sin(t * 4.9f) * 14f) * 1.2f;
                    responsiveness = 2.8f;
                    _stumbleMoveTimer += dt * (wantsSprint ? 1.8f : 1f);
                    if (_stumbleMoveTimer >= Stage4RepeatMoveSeconds)
                    {
                        TriggerPukeAndFall();
                        return Vector3.zero;
                    }
                    break;
            }

            Vector3 targetVel = Quaternion.Euler(0f, weaveDeg, 0f) * (desiredVelocity * speedScale);

            if (stage >= DrunkStage.HardToWalk)
            {
                targetVel += UpdateAndSampleStumbleImpulse(targetVel, stage, dt);
            }
            else
            {
                _stumbleRemaining = 0f;
            }

            float blend = 1f - Mathf.Exp(-responsiveness * dt);
            _smoothedVelocity = Vector3.Lerp(_smoothedVelocity, targetVel, blend);
            return _smoothedVelocity;
        }

        private float ComputeHardToWalkSpeedScale(float t, bool wantsSprint, float inputSpeed)
        {
            if (wantsSprint && _motor != null && _motor.RunSpeed > 0.01f && inputSpeed > _motor.WalkSpeed + 0.2f)
            {
                // Trying to sprint while wasted drops to a clumsy lurch capped at ~58% of run speed.
                float capped = _motor.RunSpeed * 0.58f;
                return Mathf.Min(1f, capped / inputSpeed);
            }

            return Mathf.Lerp(0.50f, 0.82f, 0.5f + 0.5f * Mathf.Sin(t * 3.1f));
        }

        private Vector3 UpdateAndSampleStumbleImpulse(Vector3 moveVel, DrunkStage stage, float dt)
        {
            if (_stumbleRemaining > 0f)
            {
                _stumbleRemaining = Mathf.Max(0f, _stumbleRemaining - dt);
            }
            else
            {
                _stumbleCooldown -= dt;
                if (_stumbleCooldown <= 0f)
                {
                    _stumbleRemaining = 0.55f;
                    _stumbleCooldown = UnityEngine.Random.Range(2.4f, 3.8f);
                    _stumbleSideSign = -_stumbleSideSign;
                }
            }

            if (_stumbleRemaining <= 0f)
            {
                return Vector3.zero;
            }

            float u = 1f - (_stumbleRemaining / 0.55f);
            float envelope = Mathf.Sin(u * Mathf.PI);
            Vector3 dir = new Vector3(moveVel.x, 0f, moveVel.z);
            dir = dir.sqrMagnitude > 0.001f ? dir.normalized : transform.forward;
            Vector3 side = new Vector3(dir.z, 0f, -dir.x);
            float strength = stage == DrunkStage.PukingAndFalling ? 1.35f : 1.15f;
            return side * (_stumbleSideSign * strength * envelope);
        }

        /// <summary>Multiplier applied to <see cref="CharacterMotor"/> turn speed.</summary>
        public float GetTurnSpeedMultiplier()
        {
            switch (Stage)
            {
                case DrunkStage.SlightlyBuzzed: return 0.95f;
                case DrunkStage.Buzzed: return 0.80f;
                case DrunkStage.HardToWalk: return 0.52f;
                case DrunkStage.PukingAndFalling: return 0.40f;
                default: return 1.0f;
            }
        }

        /// <summary>Returns drunken camera angle, FOV, and eye-position offsets for <see cref="ThirdPersonCameraRig"/>.</summary>
        public void GetCameraOffsets(out float pitchOffset, out float yawOffset, out float rollOffset, out float fovOffset, out Vector3 eyeWorldOffset)
        {
            pitchOffset = 0f;
            yawOffset = 0f;
            rollOffset = 0f;
            fovOffset = 0f;
            eyeWorldOffset = Vector3.zero;

            DrunkStage stage = Stage;
            if (stage != DrunkStage.Sober)
            {
                float t = _drunkClock + _phaseOffset;
                float rollAmp;
                float pitchAmp;
                float yawAmp;
                float fovAmp;

                switch (stage)
                {
                    case DrunkStage.SlightlyBuzzed:
                        rollAmp = 1.4f;
                        pitchAmp = 0.6f;
                        yawAmp = 0.8f;
                        fovAmp = 0.8f;
                        break;
                    case DrunkStage.Buzzed:
                        rollAmp = 3.2f;
                        pitchAmp = 1.4f;
                        yawAmp = 2.0f;
                        fovAmp = 1.8f;
                        break;
                    case DrunkStage.HardToWalk:
                        rollAmp = 6.2f;
                        pitchAmp = 2.6f;
                        yawAmp = 3.8f;
                        fovAmp = 3.4f;
                        break;
                    default:
                        rollAmp = 8.5f;
                        pitchAmp = 3.5f;
                        yawAmp = 5.0f;
                        fovAmp = 4.5f;
                        break;
                }

                rollOffset = Mathf.Sin(t * 1.45f) * rollAmp + Mathf.Sin(t * 2.7f) * (rollAmp * 0.35f);
                pitchOffset = Mathf.Sin(t * 1.9f + 0.7f) * pitchAmp;
                yawOffset = Mathf.Sin(t * 1.25f + 1.3f) * yawAmp;
                fovOffset = (0.5f + 0.5f * Mathf.Sin(t * 1.6f)) * fovAmp;
            }

            if (!IsIncapacitated)
            {
                return;
            }

            float fallWeight = GetFallBlendWeight();
            float pukeWeight = GetPukeBlendWeight();

            pitchOffset += 28f * pukeWeight;
            eyeWorldOffset += Vector3.down * (0.38f * pukeWeight + 1.25f * fallWeight);
            eyeWorldOffset += transform.forward * (0.32f * fallWeight);
            rollOffset += 22f * fallWeight;
        }

        private float GetPukeBlendWeight()
        {
            if (_knockdownPhase == KnockdownPhase.Puking)
            {
                return Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(_knockdownElapsed / 0.35f));
            }

            if (_knockdownPhase == KnockdownPhase.Falling)
            {
                float u = Mathf.Clamp01((_knockdownElapsed - PukeEndTime) / (FallEndTime - PukeEndTime));
                return 1f - Mathf.SmoothStep(0f, 1f, u);
            }

            return 0f;
        }

        private float GetFallBlendWeight()
        {
            switch (_knockdownPhase)
            {
                case KnockdownPhase.Falling:
                    return Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((_knockdownElapsed - PukeEndTime) / (FallEndTime - PukeEndTime)));
                case KnockdownPhase.Fallen:
                    return 1f;
                case KnockdownPhase.GettingUp:
                    return 1f - Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((_knockdownElapsed - FallenEndTime) / (GetUpEndTime - FallenEndTime)));
                default:
                    return 0f;
            }
        }

        private void AdvanceKnockdown(float dt)
        {
            _knockdownElapsed += dt;

            if (_motor == null) TryGetComponent(out _motor);
            _motor?.Stop();

            KnockdownPhase prev = _knockdownPhase;

            if (_knockdownElapsed < PukeEndTime)
            {
                _knockdownPhase = KnockdownPhase.Puking;
                float heave = SampleHeave(_knockdownElapsed);
                if (_knockdownElapsed >= 0.40f && _knockdownElapsed <= 1.95f)
                {
                    if (!_puddleSpawned && heave > 0.2f)
                    {
                        SpawnVomitPuddle();
                    }

                    if (heave > 0.2f)
                    {
                        SpawnVomitDroplets(dt, heave);
                    }
                }

                UpdatePuddleScale(_knockdownElapsed);
            }
            else if (_knockdownElapsed < FallEndTime)
            {
                _knockdownPhase = KnockdownPhase.Falling;
            }
            else if (_knockdownElapsed < FallenEndTime)
            {
                _knockdownPhase = KnockdownPhase.Fallen;
            }
            else if (_knockdownElapsed < GetUpEndTime)
            {
                _knockdownPhase = KnockdownPhase.GettingUp;
            }
            else
            {
                _knockdownPhase = KnockdownPhase.None;
                _knockdownElapsed = 0f;
                _stumbleMoveTimer = 0f;
                _motor?.Stop();
                RestoreBoneOverrides();
            }

            if (_knockdownPhase != prev)
            {
                StateChanged?.Invoke();
            }
        }

        private static float SampleHeave(float elapsed)
        {
            if (elapsed < 0.40f || elapsed > 1.95f)
            {
                return 0f;
            }

            return Mathf.Max(0f, Mathf.Sin((elapsed - 0.40f) * Mathf.PI * 3.8f));
        }

        private void SpawnVomitDroplets(float dt, float heave)
        {
            _dropletSpawnAccum += dt * Mathf.Lerp(18f, 44f, heave);
            int toSpawn = Mathf.Min(8, Mathf.FloorToInt(_dropletSpawnAccum));
            if (toSpawn <= 0)
            {
                return;
            }

            _dropletSpawnAccum -= toSpawn;
            EnsureVomitMaterials();

            Vector3 forward = transform.forward;
            forward.y = 0f;
            forward = forward.sqrMagnitude > 0.001f ? forward.normalized : Vector3.forward;
            Vector3 side = Vector3.Cross(Vector3.up, forward);

            Vector3 mouthDir = Quaternion.AngleAxis(_currentPukeHeadPitchDeg, transform.right)
                * (forward * 0.12f - Vector3.up * 0.02f);
            Vector3 mouthPos = _head != null
                ? _head.position + mouthDir
                : transform.position + Vector3.up * 1.1f + forward * 0.45f;

            Vector3 puddleSpot = transform.position + forward * 0.65f;
            float groundY = SampleGroundY(puddleSpot);

            for (int i = 0; i < toSpawn; i++)
            {
                if (_activeDroplets.Count >= 90)
                {
                    break;
                }

                GameObject go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                go.name = "VomitDroplet";
                if (go.TryGetComponent(out Collider col))
                {
                    SafeDestroy(col);
                }

                float size = UnityEngine.Random.Range(0.025f, 0.055f);
                go.transform.position = mouthPos + side * UnityEngine.Random.Range(-0.02f, 0.02f);
                go.transform.localScale = new Vector3(size, size * 0.85f, size);

                if (go.TryGetComponent(out Renderer rend))
                {
                    rend.sharedMaterial = s_vomitMaterials[UnityEngine.Random.Range(0, s_vomitMaterials.Length)];
                }

                Vector3 vel = forward * UnityEngine.Random.Range(0.95f, 1.85f)
                    + Vector3.down * UnityEngine.Random.Range(0.45f, 1.25f)
                    + side * UnityEngine.Random.Range(-0.28f, 0.28f);

                _activeDroplets.Add(new VomitDroplet
                {
                    Transform = go.transform,
                    Velocity = vel,
                    GroundY = groundY + 0.015f,
                    Age = 0f,
                    MaxAge = 1.2f,
                    Landed = false,
                });
            }
        }

        private void UpdateActiveDroplets(float dt)
        {
            for (int i = _activeDroplets.Count - 1; i >= 0; i--)
            {
                VomitDroplet d = _activeDroplets[i];
                if (d.Transform == null)
                {
                    _activeDroplets.RemoveAt(i);
                    continue;
                }

                d.Age += dt;
                if (d.Age >= d.MaxAge)
                {
                    SafeDestroy(d.Transform.gameObject);
                    _activeDroplets.RemoveAt(i);
                    continue;
                }

                if (!d.Landed)
                {
                    d.Velocity.y -= 9.81f * dt;
                    Vector3 nextPos = d.Transform.position + d.Velocity * dt;
                    if (nextPos.y <= d.GroundY)
                    {
                        nextPos.y = d.GroundY;
                        d.Landed = true;
                        Vector3 s = d.Transform.localScale;
                        d.Transform.localScale = new Vector3(s.x * 1.6f, 0.008f, s.z * 1.6f);
                    }

                    d.Transform.position = nextPos;
                }
            }
        }

        /// <summary>Destroys any active procedural vomit droplets and puddle created by this character.</summary>
        public void ClearSpawnedVomit()
        {
            ClearActiveDroplets();
            if (_currentPuddleRoot != null)
            {
                SafeDestroy(_currentPuddleRoot.gameObject);
                _currentPuddleRoot = null;
            }
        }

        private void ClearActiveDroplets()
        {
            for (int i = 0; i < _activeDroplets.Count; i++)
            {
                if (_activeDroplets[i].Transform != null)
                {
                    SafeDestroy(_activeDroplets[i].Transform.gameObject);
                }
            }

            _activeDroplets.Clear();
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

        private void SpawnVomitPuddle()
        {
            _puddleSpawned = true;
            EnsureVomitMaterials();

            Vector3 forward = transform.forward;
            forward.y = 0f;
            forward = forward.sqrMagnitude > 0.001f ? forward.normalized : Vector3.forward;
            Vector3 centerXZ = transform.position + forward * 0.65f;
            float groundY = SampleGroundY(centerXZ);

            GameObject root = new GameObject("VomitPuddle");
            root.transform.position = new Vector3(centerXZ.x, groundY + 0.008f, centerXZ.z);
            root.transform.rotation = Quaternion.Euler(0f, UnityEngine.Random.Range(0f, 360f), 0f);
            root.transform.localScale = Vector3.one * 0.15f;

            CreatePuddleBlob(root.transform, Vector3.zero, 0.36f, s_vomitMaterials[0]);
            CreatePuddleBlob(root.transform, new Vector3(0.14f, 0.001f, 0.08f), 0.24f, s_vomitMaterials[1]);
            CreatePuddleBlob(root.transform, new Vector3(-0.15f, 0.0015f, -0.06f), 0.22f, s_vomitMaterials[2]);
            CreatePuddleBlob(root.transform, new Vector3(0.05f, 0.002f, -0.16f), 0.19f, s_vomitMaterials[0]);
            CreatePuddleBlob(root.transform, new Vector3(-0.08f, 0.002f, 0.17f), 0.16f, s_vomitMaterials[1]);

            _currentPuddleRoot = root.transform;
            if (Application.isPlaying)
            {
                Destroy(root, PuddleLifetimeSeconds);
            }
        }

        private static void CreatePuddleBlob(Transform parent, Vector3 localOffset, float diameter, Material mat)
        {
            GameObject blob = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            blob.name = "Blob";
            if (blob.TryGetComponent(out Collider col))
            {
                SafeDestroy(col);
            }

            blob.transform.SetParent(parent, false);
            blob.transform.localPosition = localOffset;
            blob.transform.localScale = new Vector3(diameter, 0.006f, diameter * UnityEngine.Random.Range(0.82f, 1.18f));
            if (blob.TryGetComponent(out Renderer rend))
            {
                rend.sharedMaterial = mat;
            }
        }

        private void UpdatePuddleScale(float elapsed)
        {
            if (_currentPuddleRoot == null)
            {
                return;
            }

            float t = Mathf.Clamp01((elapsed - 0.45f) / 1.5f);
            float s = Mathf.Lerp(0.15f, 1.0f, Mathf.SmoothStep(0f, 1f, t));
            _currentPuddleRoot.localScale = Vector3.one * s;
        }

        private float SampleGroundY(Vector3 worldXZ)
        {
            Vector3 origin = new Vector3(worldXZ.x, transform.position.y + 1.5f, worldXZ.z);
            RaycastHit[] hits = Physics.RaycastAll(origin, Vector3.down, 12f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
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

                    if (col.gameObject.layer == 2 || col.gameObject.name == "CarObstacle")
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

            return transform.position.y;
        }

        private static void EnsureVomitMaterials()
        {
            if (s_vomitMaterials != null && s_vomitMaterials.Length == 3 && s_vomitMaterials[0] != null)
            {
                return;
            }

            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null)
            {
                shader = Shader.Find("Standard");
            }

            Color[] tones =
            {
                Color.Lerp(Palette.TurfMoss, Palette.LanternAmber, 0.35f),
                Color.Lerp(Palette.TurfMoss, Palette.DriftwoodTimber, 0.35f),
                Color.Lerp(Palette.LanternAmber, Palette.DriftwoodTimber, 0.45f),
            };

            s_vomitMaterials = new Material[tones.Length];
            for (int i = 0; i < tones.Length; i++)
            {
                Material mat = new Material(shader) { name = $"Mat_Vomit_{i}" };
                mat.SetColor("_BaseColor", tones[i]);
                mat.SetColor("_Color", tones[i]);
                mat.SetFloat("_Smoothness", 0.75f);
                mat.SetFloat("_Metallic", 0f);
                s_vomitMaterials[i] = mat;
            }
        }

        private void ResolveBones()
        {
            if (_bonesResolved)
            {
                return;
            }

            _bonesResolved = true;
            var map = new Dictionary<string, Transform>(StringComparer.Ordinal);
            foreach (Transform t in GetComponentsInChildren<Transform>(true))
            {
                if (!map.ContainsKey(t.name))
                {
                    map[t.name] = t;
                }
            }

            map.TryGetValue("Hips", out _hips);
            map.TryGetValue("Spine", out _spine);
            map.TryGetValue("Chest", out _chest);
            map.TryGetValue("Neck", out _neck);
            map.TryGetValue("Head", out _head);
            map.TryGetValue("LeftUpperLeg", out _leftUpperLeg);
            map.TryGetValue("LeftLowerLeg", out _leftLowerLeg);
            map.TryGetValue("LeftFoot", out _leftFoot);
            map.TryGetValue("RightUpperLeg", out _rightUpperLeg);
            map.TryGetValue("RightLowerLeg", out _rightLowerLeg);
            map.TryGetValue("RightFoot", out _rightFoot);
            map.TryGetValue("LeftUpperArm", out _leftUpperArm);
            map.TryGetValue("LeftLowerArm", out _leftLowerArm);
            map.TryGetValue("LeftHand", out _leftHand);
            map.TryGetValue("RightUpperArm", out _rightUpperArm);
            map.TryGetValue("RightLowerArm", out _rightLowerArm);
            map.TryGetValue("RightHand", out _rightHand);
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

        /// <summary>Reapplies the procedural drunk body pose for the current frame or simulation step.</summary>
        public void ApplyBonePose()
        {
            RestoreBoneOverrides();
            ResolveBones();

            if (!IsIncapacitated && Stage == DrunkStage.Sober)
            {
                return;
            }

            Vector3 right = transform.right;
            Vector3 up = transform.up;
            Vector3 forward = transform.forward;

            if (IsIncapacitated)
            {
                ApplyKnockdownPose(right, up, forward);
                return;
            }

            ApplyStandingSwayPose(right, up, forward);
        }

        private void ApplyStandingSwayPose(Vector3 right, Vector3 up, Vector3 forward)
        {
            float swayDeg;
            switch (Stage)
            {
                case DrunkStage.SlightlyBuzzed: swayDeg = 3f; break;
                case DrunkStage.Buzzed: swayDeg = 7f; break;
                case DrunkStage.HardToWalk: swayDeg = 13f; break;
                default: swayDeg = 17f; break;
            }

            float t = _drunkClock + _phaseOffset;
            float roll = Mathf.Sin(t * 1.7f) * swayDeg + Mathf.Sin(t * 3.1f) * (swayDeg * 0.35f);
            float pitch = Mathf.Sin(t * 2.1f + 0.5f) * (swayDeg * 0.65f);

            float stumbleDip = 0f;
            if (_stumbleRemaining > 0f)
            {
                float u = 1f - (_stumbleRemaining / 0.55f);
                stumbleDip = Mathf.Sin(u * Mathf.PI);
                roll += _stumbleSideSign * stumbleDip * 14f;
            }

            if (stumbleDip > 0.001f && _hips != null)
            {
                RecordAndRotateWorld(_hips, Quaternion.AngleAxis(roll * 0.25f, forward), true, Vector3.down * (0.06f * stumbleDip));
                float thighDip = -14f * stumbleDip;
                float shinDip = 26f * stumbleDip;
                RecordAndRotateWorld(_leftUpperLeg, Quaternion.AngleAxis(thighDip, right));
                RecordAndRotateWorld(_leftLowerLeg, Quaternion.AngleAxis(shinDip, right));
                RecordAndRotateWorld(_rightUpperLeg, Quaternion.AngleAxis(thighDip, right));
                RecordAndRotateWorld(_rightLowerLeg, Quaternion.AngleAxis(shinDip, right));
            }

            RecordAndRotateWorld(_spine, Quaternion.AngleAxis(pitch * 0.35f, right) * Quaternion.AngleAxis(roll * 0.35f, forward));
            RecordAndRotateWorld(_chest, Quaternion.AngleAxis(pitch * 0.45f, right) * Quaternion.AngleAxis(roll * 0.45f, forward));
            RecordAndRotateWorld(_neck, Quaternion.AngleAxis(-pitch * 0.15f, right) * Quaternion.AngleAxis(roll * 0.25f, forward));
            RecordAndRotateWorld(_head, Quaternion.AngleAxis(pitch * 0.35f, right) * Quaternion.AngleAxis(-roll * 0.30f, forward));
        }

        private void ApplyKnockdownPose(Vector3 right, Vector3 up, Vector3 forward)
        {
            float pukeW = GetPukeBlendWeight();
            float fallW = GetFallBlendWeight();
            float heave = _knockdownPhase == KnockdownPhase.Puking ? SampleHeave(_knockdownElapsed) : 0f;

            // Hips target drop & tilt: puke hunches down and pitches forward; fall drops Hips onto the ground.
            float currentHipsY = _hips != null ? _hips.position.y : transform.position.y + 0.9f;
            float fallenHipsDeltaY = (transform.position.y + 0.14f) - currentHipsY;
            Vector3 hipsShift = (-up * 0.12f - forward * 0.06f) * pukeW
                + (Vector3.up * fallenHipsDeltaY + forward * 0.32f) * fallW;

            float hipsPitch = 18f * pukeW + 84f * fallW;
            float hipsRoll = 18f * fallW + Mathf.Sin(_drunkClock * 2.2f) * (2.5f * pukeW);
            Quaternion hipsRot = Quaternion.AngleAxis(hipsRoll, forward) * Quaternion.AngleAxis(hipsPitch, right);
            RecordAndRotateWorld(_hips, hipsRot, true, hipsShift);

            // Spine, chest, neck, head: deep retching hunch during puke, relaxed on ground when fallen.
            float spinePitch = 24f * pukeW + 2f * fallW;
            float chestPitch = (26f + heave * 14f) * pukeW + 2f * fallW;
            float neckPitch = (16f + heave * 12f) * pukeW - 6f * fallW;
            float headPitch = (14f + heave * 14f) * pukeW - 12f * fallW;
            _currentPukeHeadPitchDeg = hipsPitch + spinePitch + chestPitch + neckPitch + headPitch;

            RecordAndRotateWorld(_spine, Quaternion.AngleAxis(spinePitch, right));
            RecordAndRotateWorld(_chest, Quaternion.AngleAxis(chestPitch, right));
            RecordAndRotateWorld(_neck, Quaternion.AngleAxis(neckPitch, right));
            RecordAndRotateWorld(_head, Quaternion.AngleAxis(28f * fallW, forward) * Quaternion.AngleAxis(headPitch, right));

            // Legs: bent knees while puking (compensating for pelvis forward pitch); relaxed on ground when fallen.
            float leftThigh = -42f * pukeW - 4f * fallW;
            float leftShin = 48f * pukeW + 6f * fallW;
            float rightThigh = -42f * pukeW - 8f * fallW;
            float rightShin = 48f * pukeW + 10f * fallW;

            RecordAndRotateWorld(_leftUpperLeg, Quaternion.AngleAxis(-6f * fallW, up) * Quaternion.AngleAxis(leftThigh, right));
            RecordAndRotateWorld(_leftLowerLeg, Quaternion.AngleAxis(leftShin, right));
            RecordAndRotateWorld(_leftFoot, Quaternion.AngleAxis(-24f * pukeW, right));

            RecordAndRotateWorld(_rightUpperLeg, Quaternion.AngleAxis(8f * fallW, up) * Quaternion.AngleAxis(rightThigh, right));
            RecordAndRotateWorld(_rightLowerLeg, Quaternion.AngleAxis(rightShin, right));
            RecordAndRotateWorld(_rightFoot, Quaternion.AngleAxis(-24f * pukeW, right));

            // Arms: brace toward knees while puking, sprawl outward onto ground while fallen.
            Vector3 hipsPos = _hips != null ? _hips.position : transform.position + up * 0.5f;
            Vector3 pukeLeftTarget = hipsPos + forward * 0.26f - right * 0.19f - up * 0.12f;
            Vector3 pukeRightTarget = hipsPos + forward * 0.24f + right * 0.19f - up * 0.10f;
            Vector3 fallLeftTarget = transform.position + forward * 0.78f - right * 0.42f + up * 0.05f;
            Vector3 fallRightTarget = transform.position + forward * 0.72f + right * 0.45f + up * 0.05f;

            float armDenom = Mathf.Max(0.001f, pukeW + fallW);
            Vector3 leftTarget = Vector3.Lerp(pukeLeftTarget, fallLeftTarget, fallW / armDenom);
            Vector3 rightTarget = Vector3.Lerp(pukeRightTarget, fallRightTarget, fallW / armDenom);

            Vector3 leftPole = Vector3.Lerp(-up - right * 0.55f, -right - forward * 0.25f + up * 0.10f, fallW / armDenom);
            Vector3 rightPole = Vector3.Lerp(-up + right * 0.55f, right - forward * 0.25f + up * 0.10f, fallW / armDenom);

            SolveAndRecordArm(_leftUpperArm, _leftLowerArm, _leftHand, leftTarget, leftPole);
            SolveAndRecordArm(_rightUpperArm, _rightLowerArm, _rightHand, rightTarget, rightPole);
        }

        private void UpdatePostProcessingVolume()
        {
            if (!Application.isPlaying)
            {
                return;
            }

            if (_party == null)
            {
                _party = FindAnyObjectByType<PartyController>();
            }

            if (_party != null && _party.ActiveCharacter != null && _party.ActiveCharacter.gameObject != gameObject)
            {
                if (_party.ActiveCharacter.GetComponent<CharacterDrunk>() == null && s_sharedVolume != null)
                {
                    s_sharedVolume.weight = 0f;
                }

                return;
            }

            EnsureSharedVolume();
            if (s_sharedVolume == null)
            {
                return;
            }

            if (BeersDrunk < 0.05f && !IsIncapacitated)
            {
                s_sharedVolume.weight = 0f;
                return;
            }

            Camera mainCam = Camera.main;
            if (mainCam != null && mainCam.TryGetComponent(out UniversalAdditionalCameraData camData) && !camData.renderPostProcessing)
            {
                camData.renderPostProcessing = true;
            }

            s_sharedVolume.weight = 1f;

            float level = Mathf.Clamp01(BeersDrunk / 4f);
            float pulse = 0.5f + 0.5f * Mathf.Sin(_drunkClock * 2.3f);
            float pukeW = GetPukeBlendWeight();
            float fallW = GetFallBlendWeight();

            // Stage-driven intensity curve: gentle at 1 beer, noticeably stronger at 2, heavy at 3+.
            float vig = Mathf.Lerp(0.08f, 0.48f, level) + 0.14f * pukeW + 0.20f * fallW;
            float chroma = Mathf.Lerp(0.03f, 0.45f, level * level) + pulse * 0.10f * level;
            float lens = -Mathf.Lerp(0.02f, 0.34f, level) * (0.75f + 0.25f * pulse);
            float saturation = Mathf.Lerp(2f, -24f, level) - 22f * pukeW - 25f * fallW;
            float contrast = Mathf.Lerp(2f, 16f, level);
            float bloom = Mathf.Lerp(0.05f, 0.65f, level);

            Color soberTint = Color.white;
            Color warmBuzz = Color.Lerp(Color.white, Palette.SunbreakKey, 0.25f);
            Color nauseaGreen = Color.Lerp(Color.white, Palette.TurfMoss, 0.42f);
            Color filterColor = Color.Lerp(soberTint, warmBuzz, Mathf.Clamp01(level * 1.4f));
            filterColor = Color.Lerp(filterColor, nauseaGreen, pukeW * 0.85f);

            s_vignette.intensity.value = Mathf.Clamp01(vig);
            s_vignette.smoothness.value = Mathf.Lerp(0.35f, 0.65f, level);
            s_vignette.color.value = Color.Lerp(Palette.DeepFjordSlate, Color.black, 0.5f);

            s_chromaticAberration.intensity.value = Mathf.Clamp01(chroma);

            s_lensDistortion.intensity.value = Mathf.Clamp(lens, -1f, 1f);
            s_lensDistortion.scale.value = Mathf.Lerp(1.0f, 1.06f, level);

            s_colorAdjustments.saturation.value = Mathf.Clamp(saturation, -100f, 100f);
            s_colorAdjustments.contrast.value = Mathf.Clamp(contrast, -100f, 100f);
            s_colorAdjustments.colorFilter.value = filterColor;
            s_colorAdjustments.postExposure.value = -0.35f * fallW;

            s_bloom.threshold.value = 0.85f;
            s_bloom.intensity.value = bloom;
            s_bloom.scatter.value = 0.65f;
        }

        private void ClearSharedVolumeIfOwner()
        {
            if (s_sharedVolume == null)
            {
                return;
            }

            if (_party != null && _party.ActiveCharacter != null && _party.ActiveCharacter.gameObject != gameObject)
            {
                return;
            }

            s_sharedVolume.weight = 0f;
        }

        private static void EnsureSharedVolume()
        {
            if (s_sharedVolume != null && s_sharedProfile != null)
            {
                return;
            }

            GameObject go = GameObject.Find("_DrunkPostProcessVolume");
            if (go == null)
            {
                go = new GameObject("_DrunkPostProcessVolume");
            }

            s_sharedVolume = go.TryGetComponent(out Volume vol) ? vol : go.AddComponent<Volume>();
            s_sharedVolume.isGlobal = true;
            s_sharedVolume.priority = 50f;
            s_sharedVolume.weight = 0f;

            s_sharedProfile = ScriptableObject.CreateInstance<VolumeProfile>();
            s_vignette = s_sharedProfile.Add<Vignette>(true);
            s_chromaticAberration = s_sharedProfile.Add<ChromaticAberration>(true);
            s_lensDistortion = s_sharedProfile.Add<LensDistortion>(true);
            s_colorAdjustments = s_sharedProfile.Add<ColorAdjustments>(true);
            s_bloom = s_sharedProfile.Add<Bloom>(true);

            s_vignette.intensity.overrideState = true;
            s_vignette.smoothness.overrideState = true;
            s_vignette.color.overrideState = true;
            s_chromaticAberration.intensity.overrideState = true;
            s_lensDistortion.intensity.overrideState = true;
            s_lensDistortion.scale.overrideState = true;
            s_colorAdjustments.saturation.overrideState = true;
            s_colorAdjustments.contrast.overrideState = true;
            s_colorAdjustments.colorFilter.overrideState = true;
            s_colorAdjustments.postExposure.overrideState = true;
            s_bloom.threshold.overrideState = true;
            s_bloom.intensity.overrideState = true;
            s_bloom.scatter.overrideState = true;

            s_sharedVolume.sharedProfile = s_sharedProfile;
        }
    }
}
