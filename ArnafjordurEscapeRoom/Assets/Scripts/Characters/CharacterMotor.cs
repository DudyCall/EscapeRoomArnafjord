using UnityEngine;

namespace Arnafjordur.Characters
{
    /// <summary>
    /// Drives grounded planar locomotion, smooth facing rotation, and Animator Speed blending for a single character.
    /// </summary>
    [RequireComponent(typeof(CharacterController))]
    public sealed class CharacterMotor : MonoBehaviour
    {
        private static readonly int SpeedHash = Animator.StringToHash("Speed");

        [SerializeField] private string displayName = "Character";
        [SerializeField] private float walkSpeed = 1.75f;
        [SerializeField] private float runSpeed = 4.2f;
        [SerializeField] private float turnSpeedDeg = 540f;
        [SerializeField] private float speedDampTime = 0.08f;
        [SerializeField] private float arenaHalfExtent = 18.5f;
        [SerializeField] private float arrivalTolerance = 0.22f;
        [SerializeField] private Animator animator;
        [SerializeField, Tooltip("Ground this character must stay on. Empty: the square flat arena above is used instead.")]
        private WalkableArea walkableArea;

        private CharacterController _controller;
        private bool _hasSpeedParam;
        private Vector2 _manualMoveXZ;
        private bool _manualSprint;
        private bool _hasDestination;
        private Vector3 _destination;
        private bool _sprintToDestination;
        private float _verticalVelocity;

        /// <summary>Human-readable character name displayed in the HUD.</summary>
        public string DisplayName => displayName;

        /// <summary>Configured walking speed in meters per second.</summary>
        public float WalkSpeed => walkSpeed;

        /// <summary>Configured running speed in meters per second.</summary>
        public float RunSpeed => runSpeed;

        /// <summary>Symmetric half-extent of the flat play area in meters.</summary>
        public float ArenaHalfExtent => arenaHalfExtent;

        /// <summary>Ground this character is kept on, or null when it uses the square flat arena.</summary>
        public WalkableArea Area
        {
            get => walkableArea;
            set => walkableArea = value;
        }

        /// <summary>Smoothed planar speed currently reported to the Animator.</summary>
        public float CurrentSpeed { get; private set; }

        /// <summary>True when the character has non-trivial planar motion.</summary>
        public bool IsMoving => CurrentSpeed > 0.08f;

        /// <summary>True while navigating toward an automated or click-to-move point.</summary>
        public bool HasDestination => _hasDestination;

        /// <summary>Current target world point when <see cref="HasDestination"/> is true.</summary>
        public Vector3 Destination => _destination;

        private void Awake()
        {
            _controller = GetComponent<CharacterController>();
            RefreshAnimator();
        }

        /// <summary>
        /// Assigns core identity and component references when configured from Editor setup or tests.
        /// </summary>
        public void Configure(string characterName, Animator targetAnimator = null, float playAreaHalfExtent = 18.5f)
        {
            displayName = characterName;
            arenaHalfExtent = playAreaHalfExtent;
            if (targetAnimator != null)
            {
                animator = targetAnimator;
            }

            if (_controller == null)
            {
                _controller = GetComponent<CharacterController>();
            }

            RefreshAnimator();
        }

        /// <summary>
        /// Re-caches Animator parameter availability after controller or hierarchy changes.
        /// </summary>
        public void RefreshAnimator()
        {
            if (animator == null)
            {
                animator = GetComponentInChildren<Animator>();
            }

            _hasSpeedParam = false;
            if (animator == null)
            {
                return;
            }

            animator.applyRootMotion = false;
            if (animator.runtimeAnimatorController == null)
            {
                return;
            }

            foreach (AnimatorControllerParameter param in animator.parameters)
            {
                if (param.nameHash == SpeedHash && param.type == AnimatorControllerParameterType.Float)
                {
                    _hasSpeedParam = true;
                    break;
                }
            }
        }

        /// <summary>
        /// Sets persistent world-space XZ directional input from player controls.
        /// Any non-zero directional input overrides active point-to-point navigation.
        /// </summary>
        public void SetManualInput(Vector2 worldDirXZ, bool sprint)
        {
            if (worldDirXZ.sqrMagnitude > 1f)
            {
                worldDirXZ.Normalize();
            }

            _manualMoveXZ = worldDirXZ;
            _manualSprint = sprint;

            if (worldDirXZ.sqrMagnitude > 0.0001f)
            {
                _hasDestination = false;
            }
        }

        /// <summary>
        /// Commands the character to walk or run toward a flat world-space destination clamped inside the arena.
        /// </summary>
        public void SetDestination(Vector3 worldPosition, bool sprint = false)
        {
            _destination = ClampToArena(worldPosition);
            _sprintToDestination = sprint;
            _hasDestination = true;
        }

        /// <summary>
        /// Clears both manual velocity input and any active point destination.
        /// </summary>
        public void Stop()
        {
            _manualMoveXZ = Vector2.zero;
            _manualSprint = false;
            _hasDestination = false;
        }

        private void Update()
        {
            Tick(Time.deltaTime);
        }

        /// <summary>Advances the character by <paramref name="dt"/> seconds (called every frame; also used by editor checks).</summary>
        public void Tick(float dt)
        {
            if (_controller == null)
            {
                _controller = GetComponent<CharacterController>();
            }

            if (dt <= 1e-6f)
            {
                return;
            }

            Vector3 desiredHorizontal = ComputeDesiredVelocity(dt);
            UpdateFacing(desiredHorizontal, dt);
            float actualPlanarSpeed = MoveCharacter(desiredHorizontal, dt);
            UpdateAnimatorSpeed(actualPlanarSpeed, dt);
        }

        private Vector3 ComputeDesiredVelocity(float dt)
        {
            if (_manualMoveXZ.sqrMagnitude > 0.0001f)
            {
                float speed = _manualSprint ? runSpeed : walkSpeed;
                return new Vector3(_manualMoveXZ.x, 0f, _manualMoveXZ.y) * speed;
            }

            if (!_hasDestination)
            {
                return Vector3.zero;
            }

            Vector3 pos = transform.position;
            Vector3 toTarget = new Vector3(_destination.x - pos.x, 0f, _destination.z - pos.z);
            float distance = toTarget.magnitude;
            if (distance <= arrivalTolerance)
            {
                _hasDestination = false;
                return Vector3.zero;
            }

            float targetSpeed = _sprintToDestination ? runSpeed : walkSpeed;
            // Ease speed slightly right before arrival so capsules settle cleanly on destinations.
            float ramp = Mathf.Clamp(distance / 0.45f, 0.35f, 1f);
            float clampedMax = distance / Mathf.Max(dt, 0.02f);
            float moveSpeed = Mathf.Min(targetSpeed * ramp, clampedMax);
            return (toTarget / distance) * moveSpeed;
        }

        private void UpdateFacing(Vector3 desiredHorizontal, float dt)
        {
            if (desiredHorizontal.sqrMagnitude <= 0.0004f)
            {
                return;
            }

            Quaternion targetRotation = Quaternion.LookRotation(desiredHorizontal.normalized, Vector3.up);
            transform.rotation = Quaternion.RotateTowards(transform.rotation, targetRotation, turnSpeedDeg * dt);
        }

        private float MoveCharacter(Vector3 desiredHorizontal, float dt)
        {
            if (_controller.isGrounded && _verticalVelocity < 0f)
            {
                _verticalVelocity = -2f;
            }
            else
            {
                _verticalVelocity += Physics.gravity.y * dt;
            }

            Vector3 before = transform.position;
            Vector3 step = desiredHorizontal * dt;
            step.y = _verticalVelocity * dt;

            if (walkableArea != null)
            {
                // Aim for the nearest allowed spot instead, which slides the character along the edge of the area.
                Vector3 allowed = walkableArea.Clamp(new Vector3(before.x + step.x, before.y, before.z + step.z));
                step.x = allowed.x - before.x;
                step.z = allowed.z - before.z;
            }
            else
            {
                // Block outward motion cleanly at arena boundaries instead of pulling transform outside CharacterController.
                if ((before.x <= -arenaHalfExtent && step.x < 0f) || (before.x >= arenaHalfExtent && step.x > 0f))
                {
                    step.x = 0f;
                }

                if ((before.z <= -arenaHalfExtent && step.z < 0f) || (before.z >= arenaHalfExtent && step.z > 0f))
                {
                    step.z = 0f;
                }
            }

            _controller.Move(step);

            Vector3 after = transform.position;
            float dx = after.x - before.x;
            float dz = after.z - before.z;
            return Mathf.Sqrt(dx * dx + dz * dz) / dt;
        }

        private void UpdateAnimatorSpeed(float measuredPlanarSpeed, float dt)
        {
            float target = measuredPlanarSpeed < 0.05f ? 0f : measuredPlanarSpeed;
            CurrentSpeed = Mathf.MoveTowards(CurrentSpeed, target, 18f * dt);

            if (animator != null && _hasSpeedParam && animator.isInitialized)
            {
                animator.SetFloat(SpeedHash, CurrentSpeed, speedDampTime, dt);
            }
        }

        private Vector3 ClampToArena(Vector3 worldPos)
        {
            if (walkableArea != null)
            {
                return walkableArea.Clamp(worldPos);
            }

            worldPos.x = Mathf.Clamp(worldPos.x, -arenaHalfExtent, arenaHalfExtent);
            worldPos.z = Mathf.Clamp(worldPos.z, -arenaHalfExtent, arenaHalfExtent);
            return worldPos;
        }
    }
}
