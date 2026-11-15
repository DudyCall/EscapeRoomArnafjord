using UnityEngine;

namespace Arnafjordur.Intro
{
    /// <summary>
    /// Gives a seated player a beer in one hand and has them take a swig every few seconds.
    /// Runs after the Animator each frame and re-aims the arm, so it works on top of any pose.
    /// </summary>
    public sealed class BeerDrinker : MonoBehaviour
    {
        [Header("Rig")]
        [SerializeField] private Transform upperArm;
        [SerializeField] private Transform lowerArm;
        [SerializeField] private Transform hand;
        [SerializeField] private Transform head;
        [SerializeField] private Transform beer;

        [Header("Pose")]
        [SerializeField, Tooltip("Mouth position from the head bone: x right, y up, z forward (character axes).")]
        private Vector3 mouthOffset = new Vector3(0f, -0.03f, 0.11f);
        [SerializeField, Tooltip("Rest the beer at a fixed spot between sips instead of wherever the animation puts the hand.")]
        private bool useRestTarget;
        [SerializeField, Tooltip("Where the fist rests between sips, in this character's local space.")] private Vector3 restLocal;
        [SerializeField, Tooltip("Centre of the closed fist, in the hand bone's local space; the bottle passes through it.")]
        private Vector3 gripLocal;
        [SerializeField, Tooltip("Metres the fist sits below the middle of the bottle.")] private float gripBelowCentre = 0.02f;
        [SerializeField, Tooltip("Metres from the middle of the bottle to its mouth.")] private float topHeight = 0.16f;

        [Header("Timing")]
        [SerializeField, Min(1f), Tooltip("Seconds between the start of one sip and the next.")] private float period = 8f;
        [SerializeField, Tooltip("Seconds to shift this player's sips so they don't all drink at once.")] private float offset;
        [SerializeField, Min(0.1f)] private float raiseSeconds = 0.5f;
        [SerializeField, Min(0f)] private float holdSeconds = 1f;

        [Header("Out of the car")]
        [SerializeField, Tooltip("Start with the beer already lowered: it is just carried in the hand, with no sipping.")]
        private bool startLowered;

        private float _time, _sip, _restWeight = 1f;
        private bool _finishing;
        private bool _hasLastSolvedPose;
        private Quaternion _lastSolvedUpperRot;
        private Quaternion _lastSolvedLowerRot;
        private Vector3 _cachedAnimSwing;

        /// <summary>Start with the beer already lowered and carried (used by scenes that begin after the car).</summary>
        public bool StartLowered
        {
            get => startLowered;
            set => startLowered = value;
        }

        /// <summary>Hand bone that carries the beer.</summary>
        public Transform Hand => hand;

        /// <summary>Lower arm bone above the hand.</summary>
        public Transform LowerArm => lowerArm;

        /// <summary>Beer transform carried by this character.</summary>
        public Transform Beer => beer;

        /// <summary>Fist centre in hand-bone local space.</summary>
        public Vector3 GripLocal => gripLocal;

        /// <summary>Metres the fist sits below the middle of the bottle.</summary>
        public float GripBelowCentre => gripBelowCentre;

        private void Awake()
        {
            if (!startLowered) return;
            _finishing = true;
            _restWeight = 0f;
        }

        /// <summary>Wires the rig and timing (used by the editor prefab setup).</summary>
        public void Configure(Transform upperArmBone, Transform lowerArmBone, Transform handBone, Transform headBone,
            Transform beerProp, float sipPeriod, float sipOffset, bool holdAtRest, Vector3 restLocalPosition, Vector3 fistLocal)
        {
            gripLocal = fistLocal;
            upperArm = upperArmBone;
            lowerArm = lowerArmBone;
            hand = handBone;
            head = headBone;
            beer = beerProp;
            period = Mathf.Max(1f, sipPeriod);
            offset = sipOffset;
            useRestTarget = holdAtRest;
            restLocal = restLocalPosition;
        }

        /// <summary>Lowers the beer and stops sipping; the bottle stays in the hand.</summary>
        public void Finish()
        {
            _finishing = true;
        }

        private void LateUpdate()
        {
            float dt = Time.deltaTime;
            if (_finishing)
            {
                _sip = Mathf.MoveTowards(_sip, 0f, dt / raiseSeconds);
                _restWeight = Mathf.MoveTowards(_restWeight, 0f, dt / 0.4f);
            }
            else
            {
                _time += dt;
                _sip = Cycle(_time + offset);
            }
            Pose(_sip, _restWeight);
        }

        private float Cycle(float t)
        {
            float u = Mathf.Repeat(t, period);
            if (u < raiseSeconds) return Mathf.SmoothStep(0f, 1f, u / raiseSeconds);
            u -= raiseSeconds;
            if (u < holdSeconds) return 1f;
            u -= holdSeconds;
            return u < raiseSeconds ? 1f - Mathf.SmoothStep(0f, 1f, u / raiseSeconds) : 0f;
        }

        /// <summary>Poses the arm and bottle: sip 0 = resting, 1 = bottle at the mouth. Safe to call in edit mode.</summary>
        public void Pose(float sip, float restWeight = 1f)
        {
            if (upperArm == null || lowerArm == null || hand == null || head == null || beer == null || !beer.IsChildOf(transform)) return;
            Vector3 up = transform.up, forward = transform.forward, right = transform.right;
            float side = Mathf.Sign(Vector3.Dot(upperArm.position - head.position, right));
            if (side == 0f) side = 1f;

            Vector3 sipPole = -up + right * (0.6f * side) + forward * 0.2f;
            float clampedRest = Mathf.Clamp01(restWeight);
            Vector3 baseFist;
            Vector3 pole = sipPole;
            Vector3 baseAxis = up;
            bool shouldSolve;

            if (clampedRest < 0.999f)
            {
                if (!Application.isPlaying)
                {
                    _cachedAnimSwing = Vector3.zero;
                }
                else if (!_hasLastSolvedPose || upperArm.localRotation != _lastSolvedUpperRot || lowerArm.localRotation != _lastSolvedLowerRot)
                {
                    _cachedAnimSwing = MeasureCarryAnimSwing(transform, upperArm, lowerArm, hand, gripLocal, side);
                }

                ComputeStandingCarryPose(
                    transform, upperArm, lowerArm, hand, side, _cachedAnimSwing,
                    out Vector3 carryFist, out Vector3 carryPole, out Vector3 carryAxis);

                Vector3 seatedFist = useRestTarget ? transform.TransformPoint(restLocal) : hand.TransformPoint(gripLocal);
                baseFist = Vector3.Lerp(carryFist, seatedFist, clampedRest);
                pole = Vector3.Slerp(carryPole, sipPole, clampedRest).normalized;
                baseAxis = Vector3.Slerp(carryAxis, up, clampedRest).normalized;
                shouldSolve = true;
            }
            else
            {
                baseFist = useRestTarget ? transform.TransformPoint(restLocal) : hand.TransformPoint(gripLocal);
                shouldSolve = useRestTarget;
            }

            Vector3 fistTarget = baseFist;
            Vector3 axis = baseAxis; // bottle base -> mouth of the bottle
            if (sip > 0.001f)
            {
                Vector3 mouth = head.position + right * mouthOffset.x + up * mouthOffset.y + forward * mouthOffset.z;
                Vector3 sipAxis = (-forward - up * 0.2f).normalized; // tipped back so it pours
                Vector3 sipFist = mouth - sipAxis * (topHeight + gripBelowCentre);
                float arc = clampedRest < 0.999f ? 0.08f : 0.10f;
                fistTarget = Vector3.Lerp(baseFist, sipFist, sip) + forward * (arc * Mathf.Sin(Mathf.PI * sip));
                pole = Vector3.Slerp(pole, sipPole, sip).normalized;
                axis = Vector3.Slerp(baseAxis, sipAxis, sip).normalized;
                shouldSolve = true;
            }

            if (shouldSolve)
            {
                for (int pass = 0; pass < 3; pass++) // the fist's offset from the wrist turns with the arm
                    SolveArm(upperArm, lowerArm, hand, hand.position + (fistTarget - hand.TransformPoint(gripLocal)), pole);

                if (clampedRest < 0.999f)
                {
                    _lastSolvedUpperRot = upperArm.localRotation;
                    _lastSolvedLowerRot = lowerArm.localRotation;
                    _hasLastSolvedPose = true;
                }
            }

            Vector3 across = Vector3.ProjectOnPlane(right, axis);
            if (across.sqrMagnitude < 1e-6f) across = Vector3.ProjectOnPlane(forward, axis);
            beer.SetPositionAndRotation(hand.TransformPoint(gripLocal) + axis * gripBelowCentre,
                Quaternion.LookRotation(across.normalized, axis));
        }

        /// <summary>
        /// Extracts dampened locomotion arm swing relative to the shoulder from the current pre-IK hand bone pose.
        /// </summary>
        public static Vector3 MeasureCarryAnimSwing(
            Transform characterRoot,
            Transform upperArmBone,
            Transform lowerArmBone,
            Transform handBone,
            Vector3 gripLocal,
            float side)
        {
            Vector3 shoulder = upperArmBone.position;
            float a = (lowerArmBone.position - shoulder).magnitude;
            float c = (handBone.position - lowerArmBone.position).magnitude;
            Vector3 up = characterRoot.up;
            Vector3 forward = characterRoot.forward;
            Vector3 right = characterRoot.right;

            Vector3 animOffset = handBone.TransformPoint(gripLocal) - shoulder;
            float animFwd = Vector3.Dot(animOffset, forward);
            float animUp = Vector3.Dot(animOffset, up) + (a + c * 0.92f);
            float animRight = Vector3.Dot(animOffset, right) - side * 0.05f;

            return forward * Mathf.Clamp(animFwd * 0.35f, -0.07f, 0.09f)
                 + up * Mathf.Clamp(animUp * 0.25f, -0.04f, 0.05f)
                 + right * Mathf.Clamp(animRight * 0.20f, -0.03f, 0.03f);
        }

        /// <summary>
        /// Computes a natural bent-elbow standing or walking carry pose in front of the waist for an upright item.
        /// </summary>
        public static void ComputeStandingCarryPose(
            Transform characterRoot,
            Transform upperArmBone,
            Transform lowerArmBone,
            Transform handBone,
            float side,
            Vector3 animSwing,
            out Vector3 carryFist,
            out Vector3 carryPole,
            out Vector3 carryAxis)
        {
            Vector3 shoulder = upperArmBone.position;
            float a = (lowerArmBone.position - shoulder).magnitude;
            float c = (handBone.position - lowerArmBone.position).magnitude;
            Vector3 up = characterRoot.up;
            Vector3 forward = characterRoot.forward;
            Vector3 right = characterRoot.right;

            carryFist = shoulder
                - up * (a * 0.88f + c * 0.22f)
                + forward * (a * 0.10f + c * 0.88f)
                + right * (side * (a * 0.14f - c * 0.18f))
                + animSwing;
            carryPole = -up + right * (0.45f * side) - forward * 0.25f;
            carryAxis = (up + forward * 0.18f - right * (0.04f * side)).normalized;
        }

        /// <summary>Analytically solves the 2-bone arm IK chain so the wrist reaches <paramref name="target"/>.</summary>
        public static void SolveArm(Transform upperArmBone, Transform lowerArmBone, Transform handBone, Vector3 target, Vector3 pole)
        {
            Vector3 shoulder = upperArmBone.position;
            float a = (lowerArmBone.position - shoulder).magnitude;
            float c = (handBone.position - lowerArmBone.position).magnitude;
            Vector3 toTarget = target - shoulder;
            float d = Mathf.Clamp(toTarget.magnitude, Mathf.Abs(a - c) + 0.01f, a + c - 0.005f);
            Vector3 dir = toTarget.normalized;
            float x = (a * a - c * c + d * d) / (2f * d);
            float h = Mathf.Sqrt(Mathf.Max(0f, a * a - x * x));
            Vector3 elbow = shoulder + dir * x + Vector3.ProjectOnPlane(pole, dir).normalized * h;
            upperArmBone.rotation = Quaternion.FromToRotation(lowerArmBone.position - shoulder, elbow - shoulder) * upperArmBone.rotation;
            Vector3 wrist = shoulder + dir * d;
            lowerArmBone.rotation = Quaternion.FromToRotation(handBone.position - lowerArmBone.position, wrist - lowerArmBone.position) * lowerArmBone.rotation;
        }
    }
}
