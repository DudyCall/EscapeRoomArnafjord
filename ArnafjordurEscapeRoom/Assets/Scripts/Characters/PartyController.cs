using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

namespace Arnafjordur.Characters
{
    /// <summary>
    /// Coordinates active-character input (WASD/Arrows, Shift sprint, click-to-move), character switching,
    /// and companion Follow/Roam behavior across the three playable characters.
    /// </summary>
    public sealed class PartyController : MonoBehaviour
    {
        /// <summary>Autonomous movement mode applied to non-active characters.</summary>
        public enum CompanionMode
        {
            Stay = 0,
            Follow = 1,
            Roam = 2,
        }

        [SerializeField] private CharacterMotor[] characters = Array.Empty<CharacterMotor>();
        [SerializeField] private ThirdPersonCameraRig cameraRig;
        [SerializeField] private Camera raycastCamera;
        [SerializeField] private int activeIndex;
        [SerializeField] private CompanionMode companionMode = CompanionMode.Stay;
        [SerializeField] private float followDistance = 2.0f;
        [SerializeField] private float followRetargetThreshold = 0.75f;
        [SerializeField] private float roamRadius = 9.0f;
        [SerializeField] private float roamIntervalMin = 2.0f;
        [SerializeField] private float roamIntervalMax = 4.8f;

        private float[] _nextRoamTime = Array.Empty<float>();
        private Vector3 _roamCenter = Vector3.zero;

        /// <summary>Fired whenever the active character or companion mode changes.</summary>
        public event Action StateChanged;

        /// <summary>All party members managed by this controller.</summary>
        public CharacterMotor[] Characters => characters;

        /// <summary>Index of the currently player-controlled character.</summary>
        public int ActiveIndex => activeIndex;

        /// <summary>Currently player-controlled character motor, or null when unconfigured.</summary>
        public CharacterMotor ActiveCharacter => IsValidIndex(activeIndex) ? characters[activeIndex] : null;

        /// <summary>Active companion autonomy mode for non-selected party members.</summary>
        public CompanionMode CurrentCompanionMode => companionMode;

        /// <summary>True when the camera rig is currently in first-person view.</summary>
        public bool IsFirstPerson => cameraRig != null && cameraRig.IsFirstPerson;

        /// <summary>
        /// Assigns party members and camera links cleanly from Editor setup or runtime tests.
        /// </summary>
        public void Configure(CharacterMotor[] party, ThirdPersonCameraRig rig, Camera cam = null)
        {
            BindCameraEvents(false);
            characters = party ?? Array.Empty<CharacterMotor>();
            cameraRig = rig;
            raycastCamera = cam != null ? cam : Camera.main;
            _nextRoamTime = new float[characters.Length];
            activeIndex = characters.Length > 0 ? Mathf.Clamp(activeIndex, 0, characters.Length - 1) : 0;

            if (isActiveAndEnabled)
            {
                BindCameraEvents(true);
            }

            if (cameraRig != null && ActiveCharacter != null)
            {
                cameraRig.SetTarget(ActiveCharacter.transform, snap: true);
            }

            StateChanged?.Invoke();
        }

        private void Awake()
        {
            EnsureStateArrays();
        }

        private void OnEnable()
        {
            BindCameraEvents(true);
        }

        private void OnDisable()
        {
            BindCameraEvents(false);
        }

        private void Start()
        {
            BindCameraEvents(true);
            if (cameraRig != null && ActiveCharacter != null)
            {
                cameraRig.SetTarget(ActiveCharacter.transform, snap: true);
            }

            StateChanged?.Invoke();
        }

        /// <summary>Toggles between first-person and third-person view on the attached camera rig.</summary>
        public void ToggleFirstPerson()
        {
            if (cameraRig != null)
            {
                cameraRig.ToggleFirstPerson();
            }

            StateChanged?.Invoke();
        }

        private void BindCameraEvents(bool subscribe)
        {
            if (cameraRig == null)
            {
                return;
            }

            cameraRig.ViewModeChanged -= OnCameraViewModeChanged;
            if (subscribe)
            {
                cameraRig.ViewModeChanged += OnCameraViewModeChanged;
            }
        }

        private void OnCameraViewModeChanged(bool _)
        {
            StateChanged?.Invoke();
        }

        private void Update()
        {
            EnsureStateArrays();
            if (characters == null || characters.Length == 0)
            {
                return;
            }

            HandleHotkeys();
            HandleMovementInput();
            HandlePointerMove();
            UpdateCompanions();
        }

        /// <summary>
        /// Switches direct player control to the character at <paramref name="index"/>.
        /// </summary>
        public void SelectCharacter(int index)
        {
            if (!IsValidIndex(index) || index == activeIndex)
            {
                return;
            }

            CharacterMotor previous = ActiveCharacter;
            if (previous != null)
            {
                previous.SetManualInput(Vector2.zero, false);
            }

            activeIndex = index;
            CharacterMotor next = ActiveCharacter;
            if (next != null)
            {
                next.SetManualInput(Vector2.zero, false);
                if (cameraRig != null)
                {
                    cameraRig.SetTarget(next.transform, snap: false);
                }
            }

            StateChanged?.Invoke();
        }

        /// <summary>
        /// Cycles active control forward through the party.
        /// </summary>
        public void CycleCharacter()
        {
            if (characters == null || characters.Length == 0)
            {
                return;
            }

            SelectCharacter((activeIndex + 1) % characters.Length);
        }

        /// <summary>
        /// Sets the autonomous behavior mode for non-active party members.
        /// </summary>
        public void SetCompanionMode(CompanionMode mode)
        {
            if (companionMode == mode)
            {
                return;
            }

            companionMode = mode;
            EnsureStateArrays();

            for (int i = 0; i < characters.Length; i++)
            {
                if (i == activeIndex || characters[i] == null)
                {
                    continue;
                }

                if (mode == CompanionMode.Stay)
                {
                    characters[i].Stop();
                }
                else if (mode == CompanionMode.Roam)
                {
                    characters[i].Stop();
                    _nextRoamTime[i] = Time.time + UnityEngine.Random.Range(0.05f, 0.45f);
                }
            }

            StateChanged?.Invoke();
        }

        /// <summary>Toggles between Follow and Stay companion modes.</summary>
        public void ToggleFollow()
        {
            SetCompanionMode(companionMode == CompanionMode.Follow ? CompanionMode.Stay : CompanionMode.Follow);
        }

        /// <summary>Toggles between Roam and Stay companion modes.</summary>
        public void ToggleRoam()
        {
            SetCompanionMode(companionMode == CompanionMode.Roam ? CompanionMode.Stay : CompanionMode.Roam);
        }

        private void HandleHotkeys()
        {
            Keyboard kb = Keyboard.current;
            if (kb == null)
            {
                return;
            }

            if (kb.digit1Key.wasPressedThisFrame) SelectCharacter(0);
            else if (kb.digit2Key.wasPressedThisFrame) SelectCharacter(1);
            else if (kb.digit3Key.wasPressedThisFrame) SelectCharacter(2);
            else if (kb.tabKey.wasPressedThisFrame) CycleCharacter();

            if (kb.fKey.wasPressedThisFrame) ToggleFollow();
            if (kb.rKey.wasPressedThisFrame) ToggleRoam();
        }

        private void HandleMovementInput()
        {
            CharacterMotor active = ActiveCharacter;
            if (active == null)
            {
                return;
            }

            Keyboard kb = Keyboard.current;
            if (kb == null)
            {
                active.SetManualInput(Vector2.zero, false);
                return;
            }

            float x = 0f;
            float y = 0f;
            if (kb.aKey.isPressed || kb.leftArrowKey.isPressed) x -= 1f;
            if (kb.dKey.isPressed || kb.rightArrowKey.isPressed) x += 1f;
            if (kb.sKey.isPressed || kb.downArrowKey.isPressed) y -= 1f;
            if (kb.wKey.isPressed || kb.upArrowKey.isPressed) y += 1f;

            bool sprint = kb.leftShiftKey.isPressed || kb.rightShiftKey.isPressed;
            Vector2 moveInput = new Vector2(x, y);
            if (moveInput.sqrMagnitude <= 0.0001f)
            {
                active.SetManualInput(Vector2.zero, sprint);
                return;
            }

            moveInput.Normalize();
            Camera cam = ResolveCamera();
            Vector3 forward = cam != null ? cam.transform.forward : Vector3.forward;
            Vector3 right = cam != null ? cam.transform.right : Vector3.right;
            forward.y = 0f;
            right.y = 0f;
            forward = forward.sqrMagnitude > 0.001f ? forward.normalized : Vector3.forward;
            right = right.sqrMagnitude > 0.001f ? right.normalized : Vector3.right;

            Vector3 worldDir = right * moveInput.x + forward * moveInput.y;
            active.SetManualInput(new Vector2(worldDir.x, worldDir.z), sprint);
        }

        private void HandlePointerMove()
        {
            CharacterMotor active = ActiveCharacter;
            if (active == null)
            {
                return;
            }

            if (!TryGetPrimaryPointerClick(out Vector2 screenPos, out int pointerId))
            {
                return;
            }

            if (EventSystem.current != null &&
                (pointerId < 0 ? EventSystem.current.IsPointerOverGameObject() : EventSystem.current.IsPointerOverGameObject(pointerId)))
            {
                return;
            }

            Camera cam = ResolveCamera();
            if (cam == null)
            {
                return;
            }

            if (TrySampleGroundPoint(cam.ScreenPointToRay(screenPos), out Vector3 worldPoint))
            {
                Keyboard kb = Keyboard.current;
                bool sprint = kb != null && (kb.leftShiftKey.isPressed || kb.rightShiftKey.isPressed);
                active.SetDestination(worldPoint, sprint);
            }
        }

        private static bool TryGetPrimaryPointerClick(out Vector2 screenPosition, out int pointerId)
        {
            Mouse mouse = Mouse.current;
            if (mouse != null && mouse.leftButton.wasPressedThisFrame)
            {
                screenPosition = mouse.position.ReadValue();
                pointerId = -1;
                return true;
            }

            Touchscreen touch = Touchscreen.current;
            if (touch != null && touch.primaryTouch.press.wasPressedThisFrame)
            {
                screenPosition = touch.primaryTouch.position.ReadValue();
                pointerId = touch.primaryTouch.touchId.ReadValue();
                return true;
            }

            screenPosition = Vector2.zero;
            pointerId = -1;
            return false;
        }

        private static bool TrySampleGroundPoint(Ray ray, out Vector3 worldPoint)
        {
            RaycastHit[] hits = Physics.RaycastAll(ray, 160f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            if (hits != null && hits.Length > 0)
            {
                Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
                for (int i = 0; i < hits.Length; i++)
                {
                    if (hits[i].collider != null && hits[i].collider.GetComponentInParent<CharacterMotor>() == null)
                    {
                        worldPoint = hits[i].point;
                        return true;
                    }
                }
            }

            Plane groundPlane = new Plane(Vector3.up, Vector3.zero);
            if (groundPlane.Raycast(ray, out float distance) && distance > 0f)
            {
                worldPoint = ray.GetPoint(distance);
                return true;
            }

            worldPoint = Vector3.zero;
            return false;
        }

        private void UpdateCompanions()
        {
            CharacterMotor leader = ActiveCharacter;
            if (companionMode == CompanionMode.Stay)
            {
                return;
            }

            int followerSlot = 0;
            for (int i = 0; i < characters.Length; i++)
            {
                CharacterMotor companion = characters[i];
                if (i == activeIndex || companion == null)
                {
                    continue;
                }

                companion.SetManualInput(Vector2.zero, false);

                if (companionMode == CompanionMode.Follow && leader != null)
                {
                    Vector3 leaderForward = leader.transform.forward;
                    leaderForward.y = 0f;
                    leaderForward = leaderForward.sqrMagnitude > 0.001f ? leaderForward.normalized : Vector3.forward;
                    Vector3 leaderRight = new Vector3(leaderForward.z, 0f, -leaderForward.x);

                    float lateralSign = followerSlot == 0 ? -1f : 1f;
                    followerSlot++;

                    Vector3 slotPos = leader.transform.position
                        - leaderForward * followDistance
                        + leaderRight * (lateralSign * followDistance * 0.65f);

                    Vector3 delta = slotPos - companion.transform.position;
                    delta.y = 0f;
                    float dist = delta.magnitude;
                    if (dist > followRetargetThreshold)
                    {
                        companion.SetDestination(slotPos, sprint: dist > 4.2f);
                    }
                }
                else if (companionMode == CompanionMode.Roam)
                {
                    if (!companion.HasDestination && Time.time >= _nextRoamTime[i])
                    {
                        Vector3 nextTarget = PickRoamDestination(i);
                        companion.SetDestination(nextTarget, sprint: false);
                        _nextRoamTime[i] = Time.time + UnityEngine.Random.Range(roamIntervalMin, roamIntervalMax);
                    }
                }
            }
        }

        private Vector3 PickRoamDestination(int characterIndex)
        {
            float maxExtent = characters[characterIndex] != null && characters[characterIndex].Area == null
                ? Mathf.Min(roamRadius, characters[characterIndex].ArenaHalfExtent - 1.5f)
                : roamRadius;

            for (int attempt = 0; attempt < 8; attempt++)
            {
                Vector2 offset = UnityEngine.Random.insideUnitCircle * maxExtent;
                Vector3 candidate = RoamCenter() + new Vector3(offset.x, 0f, offset.y);
                bool clear = true;
                for (int other = 0; other < characters.Length; other++)
                {
                    if (other == characterIndex || characters[other] == null)
                    {
                        continue;
                    }

                    Vector3 sep = candidate - characters[other].transform.position;
                    sep.y = 0f;
                    if (sep.sqrMagnitude < 3.24f)
                    {
                        clear = false;
                        break;
                    }
                }

                if (clear)
                {
                    return candidate;
                }
            }

            Vector2 fallback = UnityEngine.Random.insideUnitCircle * maxExtent;
            return RoamCenter() + new Vector3(fallback.x, 0f, fallback.y);
        }

        /// <summary>Roaming stays round the middle of the flat arena, or round the leader out in the village.</summary>
        private Vector3 RoamCenter()
        {
            CharacterMotor leader = ActiveCharacter;
            return leader != null && leader.Area != null ? leader.transform.position : _roamCenter;
        }

        private Camera ResolveCamera()
        {
            if (raycastCamera != null)
            {
                return raycastCamera;
            }

            if (cameraRig != null && cameraRig.TryGetComponent(out Camera rigCam))
            {
                raycastCamera = rigCam;
                return rigCam;
            }

            raycastCamera = Camera.main;
            return raycastCamera;
        }

        private bool IsValidIndex(int index)
        {
            return characters != null && index >= 0 && index < characters.Length && characters[index] != null;
        }

        private void EnsureStateArrays()
        {
            int count = characters != null ? characters.Length : 0;
            if (_nextRoamTime == null || _nextRoamTime.Length != count)
            {
                _nextRoamTime = new float[count];
            }
        }
    }
}
