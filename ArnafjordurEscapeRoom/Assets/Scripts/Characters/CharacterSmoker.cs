using Arnafjordur.Intro;
using UnityEngine;

namespace Arnafjordur.Characters
{
    /// <summary>
    /// Coordinates taking a cigarette from <see cref="CigarettePack"/> with the left hand,
    /// placing it in the mouth, and raising a <see cref="Lighter"/> in the right hand to light it.
    /// Driven by <see cref="CharacterItemHolder.LateUpdate"/> so arm IK runs in a deterministic order.
    /// </summary>
    public sealed class CharacterSmoker : MonoBehaviour
    {
        private enum SmokePhase
        {
            None,
            ReachingPack,
            PullingCigarette,
            BringingToMouth,
            LoweringLeftArm,
            InMouthUnlit,
            RaisingLighter,
            LightingCigarette,
            LoweringLighter,
            InMouthLit,
            DrinkingReachMouth,
            DrinkingHoldAside,
            DrinkingReturnToMouth,
            TossReachMouth,
            TossFlick
        }

        private const float ReachPackSeconds = 0.35f;
        private const float PullCigSeconds = 0.30f;
        private const float BringToMouthSeconds = 0.45f;
        private const float LowerLeftSeconds = 0.30f;
        private const float RaiseLighterSeconds = 0.42f;
        private const float HoldLighterSeconds = 0.55f;
        private const float LowerLighterSeconds = 0.38f;
        private const float DrinkReachSeconds = 0.22f;
        private const float DrinkPullAsideSeconds = 0.25f;
        private const float DrinkReturnSeconds = 0.35f;
        private const float TossFlickSeconds = 0.20f;
        private const float BurnDurationSeconds = 120f;

        private SmokePhase _phase;
        private float _phaseTime;
        private float _burnRemaining;
        private CigarettePack _activePack;
        private Lighter _activeLighter;
        private Cigarette _cigarette;
        private Vector3 _pullEndTarget;
        private Quaternion _pullEndRot;
        private Vector3 _lastLeftTarget;
        private Vector3 _lastLeftPole;

        public bool HasUnlitCigarette => _phase == SmokePhase.InMouthUnlit && _cigarette != null;
        public bool HasCigaretteInMouth => _cigarette != null && _phase != SmokePhase.None;

        /// <summary>Starts the left-hand animation to take a cigarette from <paramref name="pack"/> and put it in the mouth.</summary>
        public bool TryTakeCigarette(CharacterItemHolder holder, CigarettePack pack)
        {
            if (holder == null || pack == null || _phase != SmokePhase.None) return false;
            if (holder.LeftUpperArmBone == null || holder.LeftLowerArmBone == null || holder.LeftHandBone == null || holder.HeadBone == null)
            {
                return false;
            }

            Cigarette spawned = pack.SpawnCigarette();
            if (spawned == null) return false;

            _activePack = pack;
            _cigarette = spawned;
            _phase = SmokePhase.ReachingPack;
            _phaseTime = 0f;
            return true;
        }

        /// <summary>Starts raising the held <paramref name="lighter"/> to light the unlit cigarette in the mouth.</summary>
        public bool TryLightCigarette(CharacterItemHolder holder, Lighter lighter)
        {
            if (holder == null || lighter == null || _phase != SmokePhase.InMouthUnlit || _cigarette == null)
            {
                return false;
            }

            _activeLighter = lighter;
            _activeLighter.ExtendFlame(RaiseLighterSeconds + HoldLighterSeconds + 0.3f);
            _phase = SmokePhase.RaisingLighter;
            _phaseTime = 0f;
            return true;
        }

        /// <summary>Provides the right-arm IK target while raising/holding/lowering the lighter to the cigarette tip.</summary>
        public bool TryGetRightArmLighterPose(
            CharacterItemHolder holder,
            Vector3 baseFist,
            Vector3 basePole,
            Vector3 baseAxis,
            out Vector3 fistTarget,
            out Vector3 pole,
            out Vector3 axis,
            out float weight)
        {
            fistTarget = baseFist;
            pole = basePole;
            axis = baseAxis;
            weight = 0f;

            if (_phase != SmokePhase.RaisingLighter &&
                _phase != SmokePhase.LightingCigarette &&
                _phase != SmokePhase.LoweringLighter)
            {
                return false;
            }

            if (_cigarette == null || _activeLighter == null || holder.HeldItem == null ||
                holder.HeldItem.gameObject != _activeLighter.gameObject)
            {
                _phase = _cigarette != null ? (_cigarette.IsLit ? SmokePhase.InMouthLit : SmokePhase.InMouthUnlit) : SmokePhase.None;
                return false;
            }

            if (_phase == SmokePhase.RaisingLighter)
            {
                weight = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(_phaseTime / RaiseLighterSeconds));
            }
            else if (_phase == SmokePhase.LightingCigarette)
            {
                weight = 1f;
            }
            else
            {
                weight = 1f - Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(_phaseTime / LowerLighterSeconds));
            }

            Vector3 up = holder.transform.up;
            Vector3 fwd = holder.transform.forward;
            Vector3 right = holder.transform.right;

            Vector3 lightAxis = (up + fwd * 0.05f).normalized;
            // Place the flame right at the outer tip of the cigarette, clear of the face.
            Vector3 lightFist = _cigarette.TipWorldPosition
                - lightAxis * 0.072f
                + fwd * 0.022f
                + right * 0.004f;
            Vector3 lightPole = -up + right * 0.6f + fwd * 0.2f;

            float arc = 0.06f * Mathf.Sin(Mathf.PI * weight);
            fistTarget = Vector3.Lerp(baseFist, lightFist, weight) + fwd * arc;
            pole = Vector3.Slerp(basePole, lightPole, weight).normalized;
            axis = Vector3.Slerp(baseAxis, lightAxis, weight).normalized;
            return true;
        }

        /// <summary>Advances the smoking state machine and solves left-arm IK when taking a cigarette.</summary>
        public void TickAndPose(CharacterItemHolder holder)
        {
            if (_phase == SmokePhase.None) return;

            _phaseTime += Time.deltaTime;

            switch (_phase)
            {
                case SmokePhase.ReachingPack:
                    UpdateReachingPack(holder);
                    break;
                case SmokePhase.PullingCigarette:
                    UpdatePullingCigarette(holder);
                    break;
                case SmokePhase.BringingToMouth:
                    UpdateBringingToMouth(holder);
                    break;
                case SmokePhase.LoweringLeftArm:
                    UpdateLoweringLeftArm(holder);
                    break;
                case SmokePhase.InMouthUnlit:
                    if (holder.IsUsingItem && _cigarette != null)
                    {
                        _phase = SmokePhase.DrinkingReachMouth;
                        _phaseTime = 0f;
                    }
                    break;
                case SmokePhase.RaisingLighter:
                    if (_phaseTime >= RaiseLighterSeconds)
                    {
                        _phase = SmokePhase.LightingCigarette;
                        _phaseTime = 0f;
                    }
                    break;
                case SmokePhase.LightingCigarette:
                    if (_phaseTime >= 0.25f && _cigarette != null && !_cigarette.IsLit)
                    {
                        _cigarette.SetLit(true);
                    }
                    if (_phaseTime >= HoldLighterSeconds)
                    {
                        _phase = SmokePhase.LoweringLighter;
                        _phaseTime = 0f;
                    }
                    break;
                case SmokePhase.LoweringLighter:
                    if (_phaseTime >= LowerLighterSeconds)
                    {
                        _phase = SmokePhase.InMouthLit;
                        _phaseTime = 0f;
                        _burnRemaining = BurnDurationSeconds;
                    }
                    break;
                case SmokePhase.InMouthLit:
                    if (holder.IsUsingItem && _cigarette != null)
                    {
                        _phase = SmokePhase.DrinkingReachMouth;
                        _phaseTime = 0f;
                        break;
                    }
                    _burnRemaining -= Time.deltaTime;
                    if (_burnRemaining <= 0f)
                    {
                        _phase = SmokePhase.TossReachMouth;
                        _phaseTime = 0f;
                    }
                    break;
                case SmokePhase.DrinkingReachMouth:
                    UpdateDrinkingReachMouth(holder);
                    break;
                case SmokePhase.DrinkingHoldAside:
                    UpdateDrinkingHoldAside(holder);
                    break;
                case SmokePhase.DrinkingReturnToMouth:
                    UpdateDrinkingReturnToMouth(holder);
                    break;
                case SmokePhase.TossReachMouth:
                    UpdateTossReachMouth(holder);
                    break;
                case SmokePhase.TossFlick:
                    UpdateTossFlick(holder);
                    break;
            }
        }

        private void UpdateReachingPack(CharacterItemHolder holder)
        {
            if (!IsPackUsable(holder))
            {
                CancelTake();
                return;
            }

            float t = Mathf.Clamp01(_phaseTime / ReachPackSeconds);
            float u = Mathf.SmoothStep(0f, 1f, t);
            _activePack.SetLidOpen(u);

            Vector3 packTop = _activePack.transform.position + _activePack.transform.up * 0.048f;
            Vector3 pole = -holder.transform.up - holder.transform.right * 0.5f - holder.transform.forward * 0.2f;
            SolveLeftArmBlended(holder, packTop, pole, u);

            if (t >= 1f)
            {
                _phase = SmokePhase.PullingCigarette;
                _phaseTime = 0f;
                if (_cigarette != null) _cigarette.gameObject.SetActive(true);
            }
        }

        private void UpdatePullingCigarette(CharacterItemHolder holder)
        {
            if (!IsPackUsable(holder))
            {
                CancelTake();
                return;
            }

            float t = Mathf.Clamp01(_phaseTime / PullCigSeconds);
            float u = Mathf.SmoothStep(0f, 1f, t);

            Vector3 packUp = _activePack.transform.up;
            Vector3 packTop = _activePack.transform.position + packUp * 0.048f;
            Vector3 target = packTop + packUp * (0.065f * u);
            Vector3 pole = -holder.transform.up - holder.transform.right * 0.5f - holder.transform.forward * 0.2f;

            SolveLeftArmBlended(holder, target, pole, 1f);

            Quaternion rot = Quaternion.LookRotation(packUp, -holder.transform.forward);
            if (_cigarette != null)
            {
                _cigarette.transform.SetPositionAndRotation(target - packUp * 0.04f, rot);
            }

            if (t >= 1f)
            {
                _pullEndTarget = target;
                _pullEndRot = rot;
                _phase = SmokePhase.BringingToMouth;
                _phaseTime = 0f;
            }
        }

        private void UpdateBringingToMouth(CharacterItemHolder holder)
        {
            float t = Mathf.Clamp01(_phaseTime / BringToMouthSeconds);
            float u = Mathf.SmoothStep(0f, 1f, t);

            if (_activePack != null)
            {
                _activePack.SetLidOpen(1f - u);
            }

            Vector3 mouthWorld = GetCigaretteMouthPosition(holder);
            Quaternion mouthRot = holder.HeadBone.rotation * Quaternion.Euler(7f, -10f, 0f);
            Vector3 handAtMouth = mouthWorld + mouthRot * new Vector3(0f, 0f, 0.035f);
            Vector3 arc = (-holder.transform.right * 0.04f + holder.transform.up * 0.03f) * Mathf.Sin(Mathf.PI * u);

            Vector3 target = Vector3.Lerp(_pullEndTarget, handAtMouth, u) + arc;
            Vector3 pole = -holder.transform.up - holder.transform.right * 0.6f + holder.transform.forward * 0.2f;
            SolveLeftArmBlended(holder, target, pole, 1f);

            if (_cigarette != null)
            {
                Vector3 cigStart = _pullEndTarget - (_activePack != null ? _activePack.transform.up : holder.transform.up) * 0.04f;
                Vector3 cigPos = Vector3.Lerp(cigStart, mouthWorld, u) + arc;
                Quaternion cigRot = Quaternion.Slerp(_pullEndRot, mouthRot, u);
                _cigarette.transform.SetPositionAndRotation(cigPos, cigRot);
            }

            if (t >= 1f)
            {
                if (_activePack != null) _activePack.SetLidOpen(0f);
                if (_cigarette != null)
                {
                    _cigarette.transform.SetParent(holder.HeadBone, true);
                    _cigarette.transform.SetPositionAndRotation(mouthWorld, mouthRot);
                }

                _lastLeftTarget = target;
                _lastLeftPole = pole;
                _phase = SmokePhase.LoweringLeftArm;
                _phaseTime = 0f;
            }
        }

        private void UpdateLoweringLeftArm(CharacterItemHolder holder)
        {
            float t = Mathf.Clamp01(_phaseTime / LowerLeftSeconds);
            float weight = 1f - Mathf.SmoothStep(0f, 1f, t);
            SolveLeftArmBlended(holder, _lastLeftTarget, _lastLeftPole, weight);

            if (t >= 1f)
            {
                if (_cigarette == null) _phase = SmokePhase.None;
                else _phase = _cigarette.IsLit ? SmokePhase.InMouthLit : SmokePhase.InMouthUnlit;
                _phaseTime = 0f;
            }
        }

        private void UpdateTossReachMouth(CharacterItemHolder holder)
        {
            if (_cigarette == null || holder.HeadBone == null)
            {
                _phase = SmokePhase.None;
                return;
            }

            float t = Mathf.Clamp01(_phaseTime / DrinkReachSeconds);
            float u = Mathf.SmoothStep(0f, 1f, t);

            Vector3 mouthWorld = GetCigaretteMouthPosition(holder);
            Quaternion mouthRot = holder.HeadBone.rotation * Quaternion.Euler(7f, -10f, 0f);
            Vector3 handAtMouth = mouthWorld + mouthRot * new Vector3(0f, 0f, 0.035f);
            Vector3 pole = -holder.transform.up - holder.transform.right * 0.6f + holder.transform.forward * 0.2f;
            SolveLeftArmBlended(holder, handAtMouth, pole, u);

            if (t >= 1f)
            {
                _cigarette.transform.SetParent(null, true);
                _pullEndTarget = handAtMouth;
                _pullEndRot = mouthRot;
                _phase = SmokePhase.TossFlick;
                _phaseTime = 0f;
            }
        }

        private void UpdateTossFlick(CharacterItemHolder holder)
        {
            float t = Mathf.Clamp01(_phaseTime / TossFlickSeconds);
            float u = Mathf.SmoothStep(0f, 1f, t);

            Vector3 flickTarget = holder.transform.position
                + holder.transform.up * 1.28f
                - holder.transform.right * 0.28f
                + holder.transform.forward * 0.34f;
            Vector3 handPos = Vector3.Lerp(_pullEndTarget, flickTarget, u);
            Vector3 pole = -holder.transform.up - holder.transform.right * 0.5f - holder.transform.forward * 0.2f;
            SolveLeftArmBlended(holder, handPos, pole, 1f);

            if (_cigarette != null)
            {
                _cigarette.transform.SetPositionAndRotation(handPos - (_pullEndRot * Vector3.forward) * 0.035f, _pullEndRot);
            }

            if (t >= 1f)
            {
                if (_cigarette != null)
                {
                    _cigarette.ThrowFrom(holder.transform);
                    _cigarette = null;
                }

                _lastLeftTarget = flickTarget;
                _lastLeftPole = pole;
                _phase = SmokePhase.LoweringLeftArm;
                _phaseTime = 0f;
            }
        }

        private void UpdateDrinkingReachMouth(CharacterItemHolder holder)
        {
            if (_cigarette == null || holder.HeadBone == null)
            {
                _phase = SmokePhase.None;
                return;
            }

            float t = Mathf.Clamp01(_phaseTime / DrinkReachSeconds);
            float u = Mathf.SmoothStep(0f, 1f, t);

            Vector3 mouthWorld = GetCigaretteMouthPosition(holder);
            Quaternion mouthRot = holder.HeadBone.rotation * Quaternion.Euler(7f, -10f, 0f);
            Vector3 handAtMouth = mouthWorld + mouthRot * new Vector3(0f, 0f, 0.035f);
            Vector3 pole = -holder.transform.up - holder.transform.right * 0.6f + holder.transform.forward * 0.2f;
            SolveLeftArmBlended(holder, handAtMouth, pole, u);

            if (t >= 1f)
            {
                _cigarette.transform.SetParent(null, true);
                _pullEndTarget = handAtMouth;
                _pullEndRot = mouthRot;
                _phase = SmokePhase.DrinkingHoldAside;
                _phaseTime = 0f;
            }
        }

        private void UpdateDrinkingHoldAside(CharacterItemHolder holder)
        {
            if (_cigarette == null)
            {
                _phase = SmokePhase.None;
                return;
            }

            float t = Mathf.Clamp01(_phaseTime / DrinkPullAsideSeconds);
            float u = Mathf.SmoothStep(0f, 1f, t);

            GetDrinkAsidePose(holder, out Vector3 asideHand, out Quaternion asideRot);
            Vector3 handPos = Vector3.Lerp(_pullEndTarget, asideHand, u);
            Quaternion cigRot = Quaternion.Slerp(_pullEndRot, asideRot, u);
            Vector3 pole = -holder.transform.up - holder.transform.right * 0.5f - holder.transform.forward * 0.2f;

            SolveLeftArmBlended(holder, handPos, pole, 1f);
            _cigarette.transform.SetPositionAndRotation(handPos - (cigRot * Vector3.forward) * 0.035f, cigRot);

            if (!holder.IsUsingItem && t >= 1f)
            {
                _pullEndTarget = asideHand;
                _pullEndRot = asideRot;
                _phase = SmokePhase.DrinkingReturnToMouth;
                _phaseTime = 0f;
            }
        }

        private void UpdateDrinkingReturnToMouth(CharacterItemHolder holder)
        {
            if (_cigarette == null || holder.HeadBone == null)
            {
                _phase = SmokePhase.None;
                return;
            }

            float t = Mathf.Clamp01(_phaseTime / DrinkReturnSeconds);
            float u = Mathf.SmoothStep(0f, 1f, t);

            Vector3 mouthWorld = GetCigaretteMouthPosition(holder);
            Quaternion mouthRot = holder.HeadBone.rotation * Quaternion.Euler(7f, -10f, 0f);
            Vector3 handAtMouth = mouthWorld + mouthRot * new Vector3(0f, 0f, 0.035f);
            Vector3 arc = (-holder.transform.right * 0.04f + holder.transform.up * 0.02f) * Mathf.Sin(Mathf.PI * u);

            Vector3 handPos = Vector3.Lerp(_pullEndTarget, handAtMouth, u) + arc;
            Quaternion cigRot = Quaternion.Slerp(_pullEndRot, mouthRot, u);
            Vector3 pole = -holder.transform.up - holder.transform.right * 0.6f + holder.transform.forward * 0.2f;

            SolveLeftArmBlended(holder, handPos, pole, 1f);
            _cigarette.transform.SetPositionAndRotation(handPos - (cigRot * Vector3.forward) * 0.035f, cigRot);

            if (t >= 1f)
            {
                _cigarette.transform.SetParent(holder.HeadBone, true);
                _cigarette.transform.SetPositionAndRotation(mouthWorld, mouthRot);
                _lastLeftTarget = handAtMouth;
                _lastLeftPole = pole;
                _phase = SmokePhase.LoweringLeftArm;
                _phaseTime = 0f;
            }
        }

        private static void GetDrinkAsidePose(CharacterItemHolder holder, out Vector3 handTarget, out Quaternion cigRot)
        {
            Vector3 up = holder.transform.up;
            Vector3 fwd = holder.transform.forward;
            Vector3 right = holder.transform.right;
            handTarget = holder.transform.position + up * 1.12f - right * 0.22f + fwd * 0.26f;
            Vector3 cigDir = (fwd - right * 0.35f + up * 0.15f).normalized;
            cigRot = Quaternion.LookRotation(cigDir, up);
        }

        private void SolveLeftArmBlended(CharacterItemHolder holder, Vector3 fistTarget, Vector3 pole, float weight)
        {
            Transform upper = holder.LeftUpperArmBone;
            Transform lower = holder.LeftLowerArmBone;
            Transform hand = holder.LeftHandBone;
            if (upper == null || lower == null || hand == null || holder.HandBone == null) return;

            Quaternion animUpper = upper.rotation;
            Quaternion animLower = lower.rotation;

            Vector3 mirroredOffset = Vector3.Reflect(
                holder.HandBone.TransformPoint(holder.GripLocal) - holder.HandBone.position,
                holder.transform.right);

            for (int pass = 0; pass < 3; pass++)
            {
                Vector3 leftFistNow = hand.position + mirroredOffset;
                BeerDrinker.SolveArm(upper, lower, hand, hand.position + (fistTarget - leftFistNow), pole);
            }

            if (weight < 0.999f)
            {
                upper.rotation = Quaternion.Slerp(animUpper, upper.rotation, weight);
                lower.rotation = Quaternion.Slerp(animLower, lower.rotation, weight);
            }
        }

        private static Vector3 GetCigaretteMouthPosition(CharacterItemHolder holder) =>
            holder.GetMouthWorldPosition() - holder.transform.forward * 0.020f;

        private bool IsPackUsable(CharacterItemHolder holder)
        {
            return _activePack != null
                && _activePack.gameObject.activeInHierarchy
                && holder.HeldItem != null
                && holder.HeldItem.gameObject == _activePack.gameObject;
        }

        private void CancelTake()
        {
            if (_activePack != null) _activePack.SetLidOpen(0f);
            ClearCigarette();
        }

        private void ClearCigarette()
        {
            if (_cigarette != null)
            {
                Destroy(_cigarette.gameObject);
                _cigarette = null;
            }

            _phase = SmokePhase.None;
            _phaseTime = 0f;
        }
    }
}
