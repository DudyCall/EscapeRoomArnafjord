using UnityEngine;

namespace Arnafjordur.Characters
{
    /// <summary>
    /// Keeps a procedural sheep (built by VillageHousesSetup for the boathouse loft) looking alive: its head dips to
    /// chew hay, comes up, and now and then turns to look around. Only the head moves, so it costs next to nothing,
    /// and it only runs while the loft is switched on (someone is inside).
    /// </summary>
    public sealed class SheepIdle : MonoBehaviour
    {
        [SerializeField] private Transform head;
        [SerializeField, Tooltip("Head pitch while eating, degrees down.")] private float grazePitch = 40f;
        [SerializeField] private int seed;

        private Quaternion _rest;
        private float _phase, _nextChange, _yawTarget, _yaw, _pitchTarget, _pitch;
        private System.Random _rng;

        /// <summary>Sets the sheep up (used by the editor scene builder).</summary>
        public void Configure(Transform headPivot, float eatPitch, int randomSeed)
        {
            head = headPivot;
            grazePitch = eatPitch;
            seed = randomSeed;
        }

        private void Awake()
        {
            if (head != null) _rest = head.localRotation;
            _rng = new System.Random(seed);
            _phase = (float)_rng.NextDouble() * 10f;
            _pitch = _pitchTarget = grazePitch;
        }

        private void Update()
        {
            if (head == null) return;
            float t = Time.time;
            if (t >= _nextChange)
            {
                // Mostly eating; sometimes the head comes up for a look round.
                bool look = _rng.NextDouble() < 0.35;
                _pitchTarget = look ? -8f : grazePitch;
                _yawTarget = look ? ((float)_rng.NextDouble() - 0.5f) * 70f : ((float)_rng.NextDouble() - 0.5f) * 16f;
                _nextChange = t + 2.5f + (float)_rng.NextDouble() * (look ? 3f : 6f);
            }

            float k = 1f - Mathf.Exp(-Time.deltaTime * 2.2f);
            _pitch = Mathf.Lerp(_pitch, _pitchTarget, k);
            _yaw = Mathf.Lerp(_yaw, _yawTarget, k);
            // chewing: a small fast nod while the head is down
            float chew = Mathf.Sin((t + _phase) * 7.5f) * 2.5f * Mathf.InverseLerp(10f, grazePitch, _pitch);
            head.localRotation = _rest * Quaternion.Euler(_pitch + chew, _yaw, 0f);
        }
    }
}
