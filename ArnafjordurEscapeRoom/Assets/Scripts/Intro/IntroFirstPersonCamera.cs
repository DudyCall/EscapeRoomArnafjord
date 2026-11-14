using System.Collections.Generic;
using Arnafjordur.Characters;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;

namespace Arnafjordur.Intro
{
    /// <summary>
    /// Lets the player toggle into a live first-person cabin view ([V] or [C]) during the intro car scenes,
    /// switch seats/characters ([1]-[3] / [Tab]), and look around (Right-Mouse drag or [Q]/[E]).
    /// Syncs with <see cref="ThirdPersonCameraRig.SessionFirstPersonPreference"/> across scene transitions.
    /// </summary>
    public static class IntroFirstPersonCamera
    {
        private const float EyeUpOffset = 0.055f;
        private const float EyeForwardOffset = 0.06f;
        private const float FallbackHeadHeight = 1.15f;
        private const float FirstPersonFov = 65f;
        private const float FirstPersonNearClip = 0.04f;
        private const float DefaultNearClip = 0.15f;
        private const float LookSensitivity = 0.24f;
        private const float KeyLookSpeedDeg = 95f;
        private const float MinYawOffset = -110f;
        private const float MaxYawOffset = 110f;
        private const float MinPitchOffset = -55f;
        private const float MaxPitchOffset = 65f;
        private const float PropHideDistanceSqr = 0.20f * 0.20f;

        private static readonly string[] OrderedCharacterNames = { "Olavur", "Leivur", "Andrias" };
        private static readonly List<Renderer> HiddenRenderers = new List<Renderer>();
        private static readonly Transform[] SlotBuffer = new Transform[3];

        private static int _lastInputFrame = -1;
        private static int _activeOccupantIndex = 1; // Default to Leivur (driver seat)
        private static float _yawOffset;
        private static float _pitchOffset;
        private static bool _wasApplied;

        /// <summary>True while first-person cabin view is enabled for intro scenes.</summary>
        public static bool IsActive => ThirdPersonCameraRig.SessionFirstPersonPreference;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetState()
        {
            RestoreHiddenRenderers();
            _lastInputFrame = -1;
            _activeOccupantIndex = 1;
            _yawOffset = 0f;
            _pitchOffset = 0f;
            _wasApplied = false;
        }

        /// <summary>
        /// Sets preview mode state explicitly for Editor verification renders.
        /// </summary>
        public static void SetPreviewState(bool active, int occupantIndex = 1, float yawOffset = 0f, float pitchOffset = 6f)
        {
            if (Application.isPlaying)
            {
                ThirdPersonCameraRig.SessionFirstPersonPreference = active;
            }

            _activeOccupantIndex = Mathf.Clamp(occupantIndex, 0, OrderedCharacterNames.Length - 1);
            _yawOffset = Mathf.Clamp(yawOffset, MinYawOffset, MaxYawOffset);
            _pitchOffset = Mathf.Clamp(pitchOffset, MinPitchOffset, MaxPitchOffset);
        }

        /// <summary>
        /// Processes camera view input once per frame and overrides <paramref name="cam"/> when first-person view is active.
        /// </summary>
        public static void UpdateAndApply(Camera cam, Transform carRoot)
        {
            if (cam == null)
            {
                return;
            }

            HandleInputOncePerFrame();

            if (!IsActive)
            {
                DeactivateIfNeeded(cam);
                return;
            }

            if (!ResolveOccupantsFromCar(carRoot))
            {
                return;
            }

            ApplyToOccupants(cam);
        }

        /// <summary>
        /// Processes camera view input once per frame and overrides <paramref name="cam"/> using explicit occupant transforms.
        /// </summary>
        public static void UpdateAndApply(Camera cam, Transform[] occupantRoots)
        {
            if (cam == null)
            {
                return;
            }

            HandleInputOncePerFrame();

            if (!IsActive)
            {
                DeactivateIfNeeded(cam);
                return;
            }

            if (!ResolveOccupantsFromArray(occupantRoots))
            {
                return;
            }

            ApplyToOccupants(cam);
        }

        private static void HandleInputOncePerFrame()
        {
            if (_lastInputFrame == Time.frameCount)
            {
                return;
            }

            _lastInputFrame = Time.frameCount;

            Keyboard kb = Keyboard.current;
            if (kb != null && (kb.vKey.wasPressedThisFrame || kb.cKey.wasPressedThisFrame))
            {
                bool next = !ThirdPersonCameraRig.SessionFirstPersonPreference;
                ThirdPersonCameraRig.SessionFirstPersonPreference = next;
                if (next)
                {
                    _yawOffset = 0f;
                    _pitchOffset = 0f;
                }
            }

            if (!IsActive)
            {
                return;
            }

            if (kb != null)
            {
                if (kb.digit1Key.wasPressedThisFrame) SelectOccupant(0);
                else if (kb.digit2Key.wasPressedThisFrame) SelectOccupant(1);
                else if (kb.digit3Key.wasPressedThisFrame) SelectOccupant(2);
                else if (kb.tabKey.wasPressedThisFrame) SelectOccupant((_activeOccupantIndex + 1) % OrderedCharacterNames.Length);

                float dt = Time.deltaTime;
                if (kb.qKey.isPressed) _yawOffset = Mathf.Clamp(_yawOffset - KeyLookSpeedDeg * dt, MinYawOffset, MaxYawOffset);
                if (kb.eKey.isPressed) _yawOffset = Mathf.Clamp(_yawOffset + KeyLookSpeedDeg * dt, MinYawOffset, MaxYawOffset);
            }

            Mouse mouse = Mouse.current;
            if (mouse != null && mouse.rightButton.isPressed)
            {
                Vector2 delta = mouse.delta.ReadValue();
                _yawOffset = Mathf.Clamp(_yawOffset + delta.x * LookSensitivity, MinYawOffset, MaxYawOffset);
                _pitchOffset = Mathf.Clamp(_pitchOffset - delta.y * LookSensitivity, MinPitchOffset, MaxPitchOffset);
            }
        }

        private static void SelectOccupant(int index)
        {
            if (_activeOccupantIndex == index)
            {
                return;
            }

            _activeOccupantIndex = index;
            _yawOffset = 0f;
            _pitchOffset = 0f;
        }

        private static bool ResolveOccupantsFromCar(Transform carRoot)
        {
            for (int i = 0; i < SlotBuffer.Length; i++)
            {
                SlotBuffer[i] = null;
            }

            if (carRoot == null)
            {
                return false;
            }

            Transform occupants = carRoot.Find("Occupants");
            Transform searchRoot = occupants != null ? occupants : carRoot;

            for (int i = 0; i < OrderedCharacterNames.Length; i++)
            {
                SlotBuffer[i] = FindNamedOccupant(searchRoot, OrderedCharacterNames[i]);
            }

            return HasAnySlot();
        }

        private static bool ResolveOccupantsFromArray(Transform[] occupantRoots)
        {
            for (int i = 0; i < SlotBuffer.Length; i++)
            {
                SlotBuffer[i] = null;
            }

            if (occupantRoots == null || occupantRoots.Length == 0)
            {
                return false;
            }

            for (int i = 0; i < occupantRoots.Length; i++)
            {
                Transform occ = occupantRoots[i];
                if (occ == null)
                {
                    continue;
                }

                int slot = MatchSlotIndex(occ.name);
                if (slot >= 0 && SlotBuffer[slot] == null)
                {
                    SlotBuffer[slot] = occ;
                }
            }

            // Fill remaining slots in given order if custom names were used.
            for (int i = 0; i < occupantRoots.Length; i++)
            {
                Transform occ = occupantRoots[i];
                if (occ == null)
                {
                    continue;
                }

                bool alreadyAssigned = false;
                for (int s = 0; s < SlotBuffer.Length; s++)
                {
                    if (SlotBuffer[s] == occ)
                    {
                        alreadyAssigned = true;
                        break;
                    }
                }

                if (alreadyAssigned)
                {
                    continue;
                }

                for (int s = 0; s < SlotBuffer.Length; s++)
                {
                    if (SlotBuffer[s] == null)
                    {
                        SlotBuffer[s] = occ;
                        break;
                    }
                }
            }

            return HasAnySlot();
        }

        private static bool HasAnySlot()
        {
            for (int i = 0; i < SlotBuffer.Length; i++)
            {
                if (SlotBuffer[i] != null)
                {
                    return true;
                }
            }

            return false;
        }

        private static int MatchSlotIndex(string objectName)
        {
            for (int i = 0; i < OrderedCharacterNames.Length; i++)
            {
                if (objectName.IndexOf(OrderedCharacterNames[i], System.StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return i;
                }
            }

            return -1;
        }

        private static Transform FindNamedOccupant(Transform searchRoot, string charName)
        {
            Transform exact = searchRoot.Find(charName + "_Seated") ?? searchRoot.Find(charName);
            if (exact != null)
            {
                return exact;
            }

            for (int i = 0; i < searchRoot.childCount; i++)
            {
                Transform child = searchRoot.GetChild(i);
                if (child.name.IndexOf(charName, System.StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return child;
                }
            }

            return null;
        }

        private static void ApplyToOccupants(Camera cam)
        {
            Transform activeOcc = SlotBuffer[_activeOccupantIndex];
            if (activeOcc == null)
            {
                for (int i = 0; i < SlotBuffer.Length; i++)
                {
                    if (SlotBuffer[i] != null)
                    {
                        _activeOccupantIndex = i;
                        activeOcc = SlotBuffer[i];
                        break;
                    }
                }
            }

            if (activeOcc == null)
            {
                return;
            }

            RestoreHiddenRenderers();

            Vector3 flatForward = Vector3.ProjectOnPlane(activeOcc.forward, Vector3.up);
            if (flatForward.sqrMagnitude < 1e-4f)
            {
                flatForward = Vector3.forward;
            }

            float baseYaw = Quaternion.LookRotation(flatForward.normalized, Vector3.up).eulerAngles.y;
            Quaternion lookRotation = Quaternion.Euler(_pitchOffset, baseYaw + _yawOffset, 0f);
            Vector3 flatLookForward = Quaternion.Euler(0f, baseYaw, 0f) * Vector3.forward;

            Transform headBone = FindDeepChild(activeOcc, "Head");
            Vector3 eyePos = headBone != null
                ? headBone.position + Vector3.up * EyeUpOffset + flatLookForward * EyeForwardOffset
                : activeOcc.position + Vector3.up * FallbackHeadHeight + flatLookForward * EyeForwardOffset;

            HideActiveOccupantGeometry(activeOcc, eyePos);

            cam.transform.SetPositionAndRotation(eyePos, lookRotation);
            cam.fieldOfView = FirstPersonFov;
            cam.nearClipPlane = FirstPersonNearClip;
            _wasApplied = true;
        }

        private static void HideActiveOccupantGeometry(Transform activeOcc, Vector3 eyePos)
        {
            foreach (Renderer r in activeOcc.GetComponentsInChildren<Renderer>(true))
            {
                if (r is SkinnedMeshRenderer || (r.bounds.center - eyePos).sqrMagnitude < PropHideDistanceSqr)
                {
                    r.shadowCastingMode = ShadowCastingMode.ShadowsOnly;
                    HiddenRenderers.Add(r);
                }
            }
        }

        private static void DeactivateIfNeeded(Camera cam)
        {
            if (!_wasApplied)
            {
                return;
            }

            RestoreHiddenRenderers();
            if (cam != null)
            {
                cam.nearClipPlane = DefaultNearClip;
            }

            _wasApplied = false;
        }

        private static void RestoreHiddenRenderers()
        {
            for (int i = 0; i < HiddenRenderers.Count; i++)
            {
                Renderer r = HiddenRenderers[i];
                if (r != null)
                {
                    r.shadowCastingMode = ShadowCastingMode.On;
                }
            }

            HiddenRenderers.Clear();
        }

        private static Transform FindDeepChild(Transform parent, string targetName)
        {
            for (int i = 0; i < parent.childCount; i++)
            {
                Transform child = parent.GetChild(i);
                if (string.Equals(child.name, targetName, System.StringComparison.OrdinalIgnoreCase))
                {
                    return child;
                }

                Transform found = FindDeepChild(child, targetName);
                if (found != null)
                {
                    return found;
                }
            }

            return null;
        }
    }
}
