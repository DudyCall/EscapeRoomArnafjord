using System;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.SceneManagement;

namespace Arnafjordur.Intro
{
    /// <summary>
    /// Drives the car along a list of waypoints while a fixed roadside camera pans to follow it.
    /// Used for the intro beat where the car leaves the tunnel and turns down towards Árnafjørður.
    /// </summary>
    public sealed class IntroPathDrive : MonoBehaviour
    {
        [Header("Scene references")]
        [SerializeField] private Transform car;
        [SerializeField] private Transform[] wheels = Array.Empty<Transform>();
        [SerializeField] private Camera sequenceCamera;

        [Header("Drive")]
        [SerializeField, Tooltip("World-space centre line of the road, in driving order.")]
        private Vector3[] waypoints = Array.Empty<Vector3>();
        [SerializeField, Min(0.1f)] private float duration = 9f;
        [SerializeField, Tooltip("Metres per second.")] private float speed = 13f;
        [SerializeField, Tooltip("Metres along the path where the car starts.")] private float startDistance = 4f;
        [SerializeField, Min(0f), Tooltip("Optional seconds at the end of driving to smoothly brake to a stop.")]
        private float brakeDuration;
        [SerializeField, Min(0f), Tooltip("Optional seconds to remain parked after braking finishes.")]
        private float holdDuration;
        [SerializeField] private float wheelRadius = 0.316f;
        [SerializeField] private bool playOnStart = true;

        [Serializable]
        public struct Shot
        {
            public float startTime;
            public Vector3 position;
            public Vector3 lookOffset;
            public float lookLead;
            public float framingHeight;
            public float minFieldOfView;
            public float maxFieldOfView;
        }

        [Header("Camera (stays put and pans with the car)")]
        [SerializeField] private Vector3 cameraPosition;
        [SerializeField] private Vector3 lookOffset = new Vector3(0f, 0.8f, 0f);
        [SerializeField, Tooltip("Metres ahead of the car that the camera aims at.")] private float lookLead = 2f;
        [SerializeField, Tooltip("Height in metres the camera tries to keep in frame around the car; it zooms to hold this.")]
        private float framingHeight = 6.5f;
        [SerializeField] private float minFieldOfView = 16f;
        [SerializeField] private float maxFieldOfView = 44f;
        [SerializeField, Tooltip("Optional ordered multi-shot camera overrides; overrides single cameraPosition when non-empty.")]
        private Shot[] shots = Array.Empty<Shot>();

        [Header("When done")]
        [SerializeField, Tooltip("Optional scene to load when the duration has elapsed.")] private string nextSceneName = "";
        [SerializeField] private UnityEvent finished = new UnityEvent();

        private float[] _cumulative;
        private Quaternion[] _wheelRest;
        private float[] _wheelSign;
        private bool _cached, _playing, _finishedRaised;

        /// <summary>Seconds since the sequence started.</summary>
        public float Elapsed { get; private set; }

        /// <summary>Length of this beat in seconds.</summary>
        public float Duration => duration;

        /// <summary>Raised once when <see cref="Duration"/> has elapsed.</summary>
        public UnityEvent Finished => finished;

        /// <summary>Scene loaded when the beat ends; empty to stay in this scene.</summary>
        public string NextSceneName
        {
            get => nextSceneName;
            set => nextSceneName = value;
        }

        /// <summary>Wires references and the path (used by the editor scene builder).</summary>
        public void Configure(Transform carRoot, Transform[] wheelTransforms, Camera cam, Vector3[] path, Vector3 camPosition)
        {
            car = carRoot;
            wheels = wheelTransforms;
            sequenceCamera = cam;
            waypoints = path;
            cameraPosition = camPosition;
            shots = Array.Empty<Shot>();
            _cached = false;
        }

        /// <summary>Wires references, path, timing, and ordered multi-shot cameras.</summary>
        public void ConfigureShots(Transform carRoot, Transform[] wheelTransforms, Camera cam, Vector3[] path,
            float sequenceDuration, float driveSpeed, Shot[] cameraShots,
            float startDist = 4f, float brakeSecs = 0f, float holdSecs = 0f)
        {
            car = carRoot;
            wheels = wheelTransforms;
            sequenceCamera = cam;
            waypoints = path;
            duration = Mathf.Max(0.1f, sequenceDuration);
            speed = driveSpeed;
            startDistance = Mathf.Max(0f, startDist);
            brakeDuration = Mathf.Max(0f, brakeSecs);
            holdDuration = Mathf.Max(0f, holdSecs);
            shots = cameraShots ?? Array.Empty<Shot>();
            if (shots.Length > 0) cameraPosition = shots[0].position;
            _cached = false;
        }

        private void Start()
        {
            if (playOnStart) Play();
        }

        /// <summary>Restarts the drive from the beginning.</summary>
        public void Play()
        {
            Cache();
            Elapsed = 0f;
            _finishedRaised = false;
            _playing = true;
            Evaluate(0f);
        }

        private void LateUpdate()
        {
            if (!_playing) return;
            Elapsed += Time.deltaTime;
            Evaluate(Elapsed);
            if (!_finishedRaised && Elapsed >= duration)
            {
                _finishedRaised = true;
                finished.Invoke();
                if (!string.IsNullOrEmpty(nextSceneName)) SceneManager.LoadScene(nextSceneName);
            }
        }

        private void Cache()
        {
            if (_cached || car == null || waypoints.Length < 2) return;
            _cumulative = new float[waypoints.Length];
            for (int i = 1; i < waypoints.Length; i++)
                _cumulative[i] = _cumulative[i - 1] + Vector3.Distance(waypoints[i - 1], waypoints[i]);

            // Wheel rest poses are captured with the car un-rotated so mirrored wheels get the right spin direction.
            _wheelRest = new Quaternion[wheels.Length];
            _wheelSign = new float[wheels.Length];
            for (int i = 0; i < wheels.Length; i++)
            {
                _wheelRest[i] = wheels[i].localRotation;
                _wheelSign[i] = Mathf.Sign(Vector3.Dot(wheels[i].right, car.right));
            }
            _cached = true;
        }

        private Vector3 Sample(float distance)
        {
            float total = _cumulative[_cumulative.Length - 1];
            distance = Mathf.Clamp(distance, 0f, total);
            int hi = Array.BinarySearch(_cumulative, distance);
            if (hi < 0) hi = ~hi;
            hi = Mathf.Clamp(hi, 1, waypoints.Length - 1);
            float span = _cumulative[hi] - _cumulative[hi - 1];
            float t = span > 0f ? (distance - _cumulative[hi - 1]) / span : 0f;
            return Vector3.Lerp(waypoints[hi - 1], waypoints[hi], t);
        }

        private float TravelledDistance(float time)
        {
            if (brakeDuration <= 0f && holdDuration <= 0f) return speed * time;
            float driveEnd = Mathf.Max(0f, duration - holdDuration);
            float cruiseEnd = Mathf.Max(0f, driveEnd - brakeDuration);
            float t = Mathf.Min(time, driveEnd);
            if (t <= cruiseEnd || brakeDuration <= 0f) return speed * t;
            float s = Mathf.Clamp01((t - cruiseEnd) / brakeDuration);
            // Smoothstep velocity decay from speed -> 0 gives zero acceleration jump at brake start and stop.
            float brakeFrac = s - s * s * s + 0.5f * s * s * s * s;
            return speed * cruiseEnd + speed * brakeDuration * brakeFrac;
        }

        /// <summary>Poses the car, wheels and camera for a given time. Safe to call in edit mode for previews.</summary>
        public void Evaluate(float time)
        {
            Cache();
            if (!_cached) return;

            float travelled = TravelledDistance(Mathf.Max(0f, time));
            float distance = startDistance + travelled;
            Vector3 position = Sample(distance);
            Vector3 ahead = Sample(distance + 2.6f);        // roughly one wheelbase ahead, so the body follows the bend
            Vector3 forward = ahead - position;
            if (forward.sqrMagnitude < 1e-6f) forward = car.forward;
            car.SetPositionAndRotation(position, Quaternion.LookRotation(forward.normalized, Vector3.up));

            float spin = travelled / wheelRadius * Mathf.Rad2Deg;
            for (int i = 0; i < wheels.Length; i++)
                wheels[i].localRotation = _wheelRest[i] * Quaternion.AngleAxis(spin * _wheelSign[i], Vector3.right);

            if (sequenceCamera == null) return;
            Vector3 cPos = cameraPosition;
            Vector3 cOff = lookOffset;
            float cLead = lookLead;
            float cFrame = framingHeight;
            float cMinFov = minFieldOfView;
            float cMaxFov = maxFieldOfView;
            if (shots != null && shots.Length > 0)
            {
                Shot active = shots[0];
                for (int i = 1; i < shots.Length; i++)
                {
                    if (time >= shots[i].startTime) active = shots[i];
                }
                cPos = active.position;
                cOff = active.lookOffset;
                cLead = active.lookLead;
                cFrame = active.framingHeight > 0.1f ? active.framingHeight : framingHeight;
                cMinFov = active.minFieldOfView > 1f ? active.minFieldOfView : minFieldOfView;
                cMaxFov = active.maxFieldOfView > 1f ? active.maxFieldOfView : maxFieldOfView;
            }
            Transform cam = sequenceCamera.transform;
            cam.position = cPos;
            Vector3 target = position + cOff + car.forward * cLead;
            cam.rotation = Quaternion.LookRotation(target - cPos, Vector3.up);
            float range = Mathf.Max(1f, Vector3.Distance(cPos, target));
            float fov = 2f * Mathf.Atan(cFrame * 0.5f / range) * Mathf.Rad2Deg;
            sequenceCamera.fieldOfView = Mathf.Clamp(fov, cMinFov, cMaxFov);
        }
    }
}
