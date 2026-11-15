using System;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;

namespace Arnafjordur.Characters
{
    /// <summary>
    /// Smooth third-person orbit and switchable first-person camera that follows the active character,
    /// supporting right-drag/Q-E look/orbit, wheel zoom (including zoom-in transition), and V/C view toggling.
    /// </summary>
    public sealed class ThirdPersonCameraRig : MonoBehaviour
    {
        [SerializeField] private Transform target;
        [SerializeField] private float yaw = 180f;
        [SerializeField] private float pitch = 18f;
        [SerializeField] private float distance = 5.4f;
        [SerializeField] private float minDistance = 2.6f;
        [SerializeField] private float maxDistance = 12.5f;
        [SerializeField] private float minPitch = 8f;
        [SerializeField] private float maxPitch = 65f;
        [SerializeField] private float targetHeight = 1.25f;
        [SerializeField] private float focusSmoothTime = 0.14f;
        [SerializeField] private float orbitSensitivity = 0.24f;
        [SerializeField] private float keyOrbitSpeedDeg = 95f;
        [SerializeField] private float zoomStep = 0.65f;
        [SerializeField, Tooltip("Metres the camera stays above the ground beneath it.")] private float groundClearance = 0.35f;
        [SerializeField, Tooltip("Slide in front of hillsides and houses that come between the camera and the character.")]
        private bool avoidObstacles = true;

        [Header("First Person")]
        [SerializeField] private bool firstPerson;
        [SerializeField] private float firstPersonEyeHeight = 1.60f;
        [SerializeField] private float firstPersonForwardOffset = 0.06f;
        [SerializeField] private float firstPersonMinPitch = -70f;
        [SerializeField] private float firstPersonMaxPitch = 75f;
        [SerializeField] private float thirdPersonFov = 45f;
        [SerializeField] private float firstPersonFov = 65f;

        private static readonly RaycastHit[] Hits = new RaycastHit[16];

        private Vector3 _smoothedFocus;
        private Vector3 _focusVelocity;
        private bool _hasInitializedFocus;
        private Camera _camera;
        private Transform _hiddenTarget;
        private CharacterMotor _targetMotor;
        private bool _hasInspectionOverride;
        private Transform _inspectionTarget;
        private Vector3 _inspectionPos;
        private Quaternion _inspectionRot;
        private float _inspectionFov = 56f;

        /// <summary>Carries the player's first-person preference across runtime scene loads (intro into village).</summary>
        public static bool SessionFirstPersonPreference { get; set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetSessionPreference()
        {
            SessionFirstPersonPreference = false;
        }

        /// <summary>Fired whenever first-person mode is enabled or disabled.</summary>
        public event Action<bool> ViewModeChanged;

        /// <summary>True when first-person view is currently active.</summary>
        public bool IsFirstPerson => firstPerson;

        /// <summary>True when locked into a bent-down close-up inspection view (e.g., sea mine bomb).</summary>
        public bool IsInspectingOverride => _hasInspectionOverride && target != null && target == _inspectionTarget;

        /// <summary>Current horizontal orbit angle in degrees.</summary>
        public float Yaw => yaw;

        /// <summary>Current vertical orbit angle in degrees.</summary>
        public float Pitch => pitch;

        /// <summary>Current camera orbit distance in meters.</summary>
        public float Distance => distance;

        /// <summary>
        /// Assigns the transform to follow and optionally snaps immediately to the new focus point.
        /// </summary>
        public void SetTarget(Transform newTarget, bool snap = false)
        {
            if (newTarget != target && _hiddenTarget != null)
            {
                SetTargetHidden(_hiddenTarget, false);
                _hiddenTarget = null;
            }

            if (newTarget != target && _targetMotor != null)
            {
                _targetMotor.LookYawOverride = null;
                _targetMotor = null;
            }

            if (_hasInspectionOverride && newTarget != _inspectionTarget)
            {
                _hasInspectionOverride = false;
                _inspectionTarget = null;
                EnsureCamera();
                if (!firstPerson && _camera != null)
                {
                    _camera.fieldOfView = thirdPersonFov;
                    _camera.nearClipPlane = 0.15f;
                }
            }

            target = newTarget;
            if (target == null)
            {
                return;
            }

            target.TryGetComponent(out _targetMotor);
            if (firstPerson)
            {
                yaw = target.eulerAngles.y;
                SetTargetHidden(target, true);
                _hiddenTarget = target;
                if (_targetMotor != null)
                {
                    _targetMotor.LookYawOverride = yaw;
                }
            }

            if (snap || !_hasInitializedFocus)
            {
                _smoothedFocus = target.position + Vector3.up * targetHeight;
                _focusVelocity = Vector3.zero;
                _hasInitializedFocus = true;
                ApplyTransform();
            }
        }

        /// <summary>Toggles between third-person orbit and first-person view.</summary>
        public void ToggleFirstPerson()
        {
            SetFirstPerson(!firstPerson, snap: true);
        }

        /// <summary>Enables or disables first-person view cleanly, restoring character visibility when exited.</summary>
        public void SetFirstPerson(bool enabled, bool snap = true)
        {
            bool changed = firstPerson != enabled;
            firstPerson = enabled;
            if (Application.isPlaying)
            {
                SessionFirstPersonPreference = enabled;
            }

            EnsureCamera();

            if (enabled)
            {
                if (changed && target != null)
                {
                    yaw = target.eulerAngles.y;
                    pitch = 0f;
                }

                if (target != null)
                {
                    target.TryGetComponent(out _targetMotor);
                    SetTargetHidden(target, true);
                    _hiddenTarget = target;
                    if (_targetMotor != null)
                    {
                        _targetMotor.LookYawOverride = yaw;
                    }
                }

                if (_camera != null)
                {
                    _camera.fieldOfView = firstPersonFov;
                    _camera.nearClipPlane = 0.05f;
                }
            }
            else
            {
                if (_hiddenTarget != null)
                {
                    SetTargetHidden(_hiddenTarget, false);
                    _hiddenTarget = null;
                }
                else if (target != null)
                {
                    SetTargetHidden(target, false);
                }

                if (_targetMotor != null)
                {
                    _targetMotor.LookYawOverride = null;
                }

                pitch = Mathf.Clamp(pitch, minPitch, maxPitch);
                if (_camera != null)
                {
                    _camera.fieldOfView = thirdPersonFov;
                    _camera.nearClipPlane = 0.15f;
                }
            }

            if (target != null && (snap || !_hasInitializedFocus))
            {
                _smoothedFocus = target.position + Vector3.up * targetHeight;
                _focusVelocity = Vector3.zero;
                _hasInitializedFocus = true;
                ApplyTransform();
            }

            ViewModeChanged?.Invoke(firstPerson);
        }

        /// <summary>
        /// Sets initial orbit angles and distance cleanly (useful for scene framing setup).
        /// </summary>
        public void SetOrbit(float newYaw, float newPitch, float newDistance, bool snap = true)
        {
            yaw = newYaw;
            pitch = Mathf.Clamp(newPitch, minPitch, maxPitch);
            distance = Mathf.Clamp(newDistance, minDistance, maxDistance);
            if (snap && target != null)
            {
                _smoothedFocus = target.position + Vector3.up * targetHeight;
                _focusVelocity = Vector3.zero;
                _hasInitializedFocus = true;
                ApplyTransform();
            }
        }

        /// <summary>Locks the camera into a close-up first-person inspection vantage for <paramref name="inspectTarget"/>.</summary>
        public void SetInspectionOverride(Transform inspectTarget, Vector3 worldPos, Quaternion worldRot, float fov = 56f, bool snap = true)
        {
            _hasInspectionOverride = inspectTarget != null;
            _inspectionTarget = inspectTarget;
            _inspectionPos = worldPos;
            _inspectionRot = worldRot;
            _inspectionFov = fov;

            if (snap && _hasInspectionOverride)
            {
                ApplyTransform();
            }
        }

        /// <summary>Releases any active inspection camera lock and restores normal first/third-person framing.</summary>
        public void ClearInspectionOverride(bool snap = true)
        {
            if (!_hasInspectionOverride)
            {
                return;
            }

            _hasInspectionOverride = false;
            _inspectionTarget = null;
            EnsureCamera();

            if (!firstPerson)
            {
                if (_hiddenTarget != null)
                {
                    SetTargetHidden(_hiddenTarget, false);
                    _hiddenTarget = null;
                }

                if (_camera != null)
                {
                    _camera.fieldOfView = thirdPersonFov;
                    _camera.nearClipPlane = 0.15f;
                }
            }

            if (snap && target != null)
            {
                _smoothedFocus = target.position + Vector3.up * targetHeight;
                _focusVelocity = Vector3.zero;
                _hasInitializedFocus = true;
                ApplyTransform();
            }
        }

        private void Start()
        {
            EnsureCamera();
            if (_camera != null && !firstPerson && _camera.fieldOfView > 1f)
            {
                thirdPersonFov = _camera.fieldOfView;
            }

            if (Application.isPlaying && SessionFirstPersonPreference && !firstPerson)
            {
                SetFirstPerson(true, snap: true);
            }
            else if (firstPerson)
            {
                SetFirstPerson(true, snap: true);
            }
            else if (target != null && !_hasInitializedFocus)
            {
                _smoothedFocus = target.position + Vector3.up * targetHeight;
                _hasInitializedFocus = true;
                ApplyTransform();
            }
        }

        private void OnDisable()
        {
            RestoreHiddenTarget();
        }

        private void OnDestroy()
        {
            RestoreHiddenTarget();
        }

        private void LateUpdate()
        {
            HandleOrbitAndZoom(Time.deltaTime);

            if (target == null)
            {
                return;
            }

            Vector3 desiredFocus = target.position + Vector3.up * targetHeight;
            if (!_hasInitializedFocus)
            {
                _smoothedFocus = desiredFocus;
                _focusVelocity = Vector3.zero;
                _hasInitializedFocus = true;
            }
            else
            {
                _smoothedFocus = Vector3.SmoothDamp(_smoothedFocus, desiredFocus, ref _focusVelocity, focusSmoothTime);
            }

            ApplyTransform();
        }

        private void HandleOrbitAndZoom(float dt)
        {
            if (IsInspectingOverride)
            {
                return;
            }

            Keyboard kb = Keyboard.current;
            if (kb != null && (kb.vKey.wasPressedThisFrame || kb.cKey.wasPressedThisFrame))
            {
                ToggleFirstPerson();
                return;
            }

            float pMin = firstPerson ? firstPersonMinPitch : minPitch;
            float pMax = firstPerson ? firstPersonMaxPitch : maxPitch;

            Mouse mouse = Mouse.current;
            if (mouse != null)
            {
                if (mouse.rightButton.isPressed)
                {
                    Vector2 delta = mouse.delta.ReadValue();
                    yaw += delta.x * orbitSensitivity;
                    pitch = Mathf.Clamp(pitch - delta.y * orbitSensitivity, pMin, pMax);
                }

                float scrollY = mouse.scroll.ReadValue().y;
                if (Mathf.Abs(scrollY) > 0.01f)
                {
                    // Normalize both standard mouse wheel notches (~120) and smooth trackpad deltas.
                    float normalized = Mathf.Abs(scrollY) > 20f ? Mathf.Sign(scrollY) : scrollY;
                    if (firstPerson)
                    {
                        if (normalized < 0f)
                        {
                            distance = minDistance;
                            SetFirstPerson(false, snap: true);
                        }
                    }
                    else if (distance <= minDistance + 0.01f && normalized > 0f)
                    {
                        SetFirstPerson(true, snap: true);
                    }
                    else
                    {
                        distance = Mathf.Clamp(distance - normalized * zoomStep, minDistance, maxDistance);
                    }
                }
            }

            if (kb != null)
            {
                if (kb.qKey.isPressed) yaw -= keyOrbitSpeedDeg * dt;
                if (kb.eKey.isPressed) yaw += keyOrbitSpeedDeg * dt;
            }
        }

        private void ApplyTransform()
        {
            EnsureCamera();

            CharacterDrunk drunk = _targetMotor != null
                ? _targetMotor.GetComponent<CharacterDrunk>()
                : (target != null ? target.GetComponent<CharacterDrunk>() : null);
            float drunkPitch = 0f;
            float drunkYaw = 0f;
            float drunkRoll = 0f;
            float drunkFov = 0f;
            Vector3 drunkEyeOffset = Vector3.zero;
            if (drunk != null)
            {
                drunk.GetCameraOffsets(out drunkPitch, out drunkYaw, out drunkRoll, out drunkFov, out drunkEyeOffset);
            }

            if (IsInspectingOverride)
            {
                if (_hiddenTarget != target)
                {
                    if (_hiddenTarget != null)
                    {
                        SetTargetHidden(_hiddenTarget, false);
                    }

                    SetTargetHidden(target, true);
                    _hiddenTarget = target;
                }

                transform.SetPositionAndRotation(_inspectionPos, _inspectionRot);
                if (_camera != null)
                {
                    _camera.fieldOfView = _inspectionFov;
                    _camera.nearClipPlane = 0.05f;
                }

                return;
            }

            if (firstPerson)
            {
                if (target == null)
                {
                    return;
                }

                if (_hiddenTarget != target)
                {
                    if (_hiddenTarget != null)
                    {
                        SetTargetHidden(_hiddenTarget, false);
                    }

                    target.TryGetComponent(out _targetMotor);
                    SetTargetHidden(target, true);
                    _hiddenTarget = target;
                }

                if (_targetMotor != null)
                {
                    _targetMotor.LookYawOverride = yaw;
                }

                Quaternion fpRot = Quaternion.Euler(pitch + drunkPitch, yaw + drunkYaw, drunkRoll);
                Vector3 forwardFlat = Quaternion.Euler(0f, yaw, 0f) * Vector3.forward;
                Vector3 eyePos = target.position + Vector3.up * firstPersonEyeHeight + forwardFlat * firstPersonForwardOffset + drunkEyeOffset;
                eyePos.y = Mathf.Max(eyePos.y, target.position.y + 0.18f);
                transform.SetPositionAndRotation(eyePos, fpRot);
                if (_camera != null)
                {
                    _camera.fieldOfView = firstPersonFov + drunkFov;
                    _camera.nearClipPlane = 0.05f;
                }

                return;
            }

            Vector3 focus = _smoothedFocus + Vector3.up * (drunkEyeOffset.y * 0.55f);
            Quaternion rot = Quaternion.Euler(pitch + drunkPitch, yaw + drunkYaw, drunkRoll);
            Vector3 back = rot * Vector3.back;
            float reach = distance;
            if (avoidObstacles)
            {
                // Only the static world (mesh colliders) blocks the view: characters and the car do not.
                int n = Physics.SphereCastNonAlloc(focus, 0.25f, back, Hits, distance,
                    Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
                for (int i = 0; i < n; i++)
                {
                    if (Hits[i].collider is MeshCollider && Hits[i].distance > 0f && Hits[i].distance < reach)
                    {
                        reach = Hits[i].distance;
                    }
                }

                reach = Mathf.Max(reach, Mathf.Min(distance, 1.2f));
            }

            Vector3 desiredPosition = focus + back * reach;

            // Stay above whatever ground is under the camera (the flat test floor sits at 0, the village far below it).
            float floor = float.NegativeInfinity;
            Vector3 probe = new Vector3(desiredPosition.x, Mathf.Max(desiredPosition.y, focus.y) + 3f, desiredPosition.z);
            int below = Physics.RaycastNonAlloc(probe, Vector3.down, Hits, 40f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < below; i++)
            {
                if (Hits[i].collider is MeshCollider || Hits[i].collider is BoxCollider)
                {
                    floor = Mathf.Max(floor, Hits[i].point.y);
                }
            }

            if (!float.IsNegativeInfinity(floor) && desiredPosition.y < floor + groundClearance)
            {
                desiredPosition.y = floor + groundClearance;
            }

            transform.SetPositionAndRotation(desiredPosition, rot);
            if (_camera != null && thirdPersonFov > 1f)
            {
                _camera.fieldOfView = thirdPersonFov + drunkFov;
            }
        }

        private void EnsureCamera()
        {
            if (_camera == null)
            {
                TryGetComponent(out _camera);
            }
        }

        private void RestoreHiddenTarget()
        {
            if (_hiddenTarget != null)
            {
                SetTargetHidden(_hiddenTarget, false);
                _hiddenTarget = null;
            }

            if (_targetMotor != null)
            {
                _targetMotor.LookYawOverride = null;
                _targetMotor = null;
            }
        }

        private static void SetTargetHidden(Transform targetRoot, bool hidden)
        {
            if (targetRoot == null)
            {
                return;
            }

            ShadowCastingMode mode = hidden ? ShadowCastingMode.ShadowsOnly : ShadowCastingMode.On;
            foreach (Renderer r in targetRoot.GetComponentsInChildren<Renderer>(true))
            {
                r.shadowCastingMode = mode;
            }
        }
    }
}
