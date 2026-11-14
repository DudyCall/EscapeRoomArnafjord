using UnityEngine;
using UnityEngine.InputSystem;

namespace Arnafjordur.Characters
{
    /// <summary>
    /// Smooth third-person orbit camera that follows the active character and supports right-drag/Q-E orbit and wheel zoom.
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

        private static readonly RaycastHit[] Hits = new RaycastHit[16];

        private Vector3 _smoothedFocus;
        private Vector3 _focusVelocity;
        private bool _hasInitializedFocus;

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
            target = newTarget;
            if (target == null)
            {
                return;
            }

            if (snap || !_hasInitializedFocus)
            {
                _smoothedFocus = target.position + Vector3.up * targetHeight;
                _focusVelocity = Vector3.zero;
                _hasInitializedFocus = true;
                ApplyTransform();
            }
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

        private void Start()
        {
            if (target != null && !_hasInitializedFocus)
            {
                _smoothedFocus = target.position + Vector3.up * targetHeight;
                _hasInitializedFocus = true;
                ApplyTransform();
            }
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
            Mouse mouse = Mouse.current;
            if (mouse != null)
            {
                if (mouse.rightButton.isPressed)
                {
                    Vector2 delta = mouse.delta.ReadValue();
                    yaw += delta.x * orbitSensitivity;
                    pitch = Mathf.Clamp(pitch - delta.y * orbitSensitivity, minPitch, maxPitch);
                }

                float scrollY = mouse.scroll.ReadValue().y;
                if (Mathf.Abs(scrollY) > 0.01f)
                {
                    // Normalize both standard mouse wheel notches (~120) and smooth trackpad deltas.
                    float normalized = Mathf.Abs(scrollY) > 20f ? Mathf.Sign(scrollY) : scrollY;
                    distance = Mathf.Clamp(distance - normalized * zoomStep, minDistance, maxDistance);
                }
            }

            Keyboard kb = Keyboard.current;
            if (kb != null)
            {
                if (kb.qKey.isPressed) yaw -= keyOrbitSpeedDeg * dt;
                if (kb.eKey.isPressed) yaw += keyOrbitSpeedDeg * dt;
            }
        }

        private void ApplyTransform()
        {
            Quaternion rot = Quaternion.Euler(pitch, yaw, 0f);
            Vector3 back = rot * Vector3.back;
            float reach = distance;
            if (avoidObstacles)
            {
                // Only the static world (mesh colliders) blocks the view: characters and the car do not.
                int n = Physics.SphereCastNonAlloc(_smoothedFocus, 0.25f, back, Hits, distance,
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

            Vector3 desiredPosition = _smoothedFocus + back * reach;

            // Stay above whatever ground is under the camera (the flat test floor sits at 0, the village far below it).
            float floor = float.NegativeInfinity;
            Vector3 probe = new Vector3(desiredPosition.x, Mathf.Max(desiredPosition.y, _smoothedFocus.y) + 3f, desiredPosition.z);
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
        }
    }
}
