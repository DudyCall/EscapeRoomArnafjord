using System;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.SceneManagement;

namespace Arnafjordur.Intro
{
    /// <summary>
    /// First beat of the intro: the car drives through the old single-lane tunnel for a fixed duration
    /// while the camera cuts between a few car-relative shots.
    /// </summary>
    public sealed class IntroTunnelDrive : MonoBehaviour
    {
        [Serializable]
        public struct Shot
        {
            [Tooltip("Seconds into the sequence at which this shot starts.")]
            public float startTime;
            [Tooltip("Camera position relative to the car (x right, y up, z forward).")]
            public Vector3 position;
            [Tooltip("Point the camera looks at, relative to the car.")]
            public Vector3 lookAt;
            [Tooltip("Camera movement relative to the car, in metres per second.")]
            public Vector3 drift;
            public float fieldOfView;
        }

        [Header("Scene references")]
        [SerializeField] private Transform car;
        [SerializeField] private Transform[] wheels = Array.Empty<Transform>();
        [SerializeField] private Camera sequenceCamera;

        [Header("Drive")]
        [SerializeField, Min(0.1f)] private float duration = 20f;
        [SerializeField, Tooltip("Metres per second (14 m/s is about 50 km/h).")] private float speed = 14f;
        [SerializeField] private float wheelRadius = 0.316f;
        [SerializeField, Tooltip("The car stops here so it never leaves the tunnel.")] private float maxDistance = 360f;
        [SerializeField] private bool playOnStart = true;

        [Header("Camera")]
        [SerializeField] private Shot[] shots = Array.Empty<Shot>();
        [SerializeField, Tooltip("Hand-held wobble in metres.")] private float cameraShake = 0.012f;

        [Header("When done")]
        [SerializeField, Tooltip("Optional scene to load when the duration has elapsed.")] private string nextSceneName = "";
        [SerializeField] private UnityEvent finished = new UnityEvent();

        private Vector3 _startPosition;
        private Quaternion[] _wheelRest;
        private float[] _wheelSign;
        private bool _cached, _playing, _finishedRaised;

        /// <summary>Seconds since the sequence started.</summary>
        public float Elapsed { get; private set; }

        /// <summary>Length of this beat in seconds.</summary>
        public float Duration => duration;

        /// <summary>Raised once when <see cref="Duration"/> has elapsed. The car keeps driving afterwards.</summary>
        public UnityEvent Finished => finished;

        /// <summary>Scene loaded when the beat ends; empty to stay in this scene.</summary>
        public string NextSceneName
        {
            get => nextSceneName;
            set => nextSceneName = value;
        }

        /// <summary>Wires the scene references and shot list (used by the editor scene builder).</summary>
        public void Configure(Transform carRoot, Transform[] wheelTransforms, Camera cam, Shot[] shotList)
        {
            car = carRoot;
            wheels = wheelTransforms;
            sequenceCamera = cam;
            shots = shotList;
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

            if (Application.isPlaying)
            {
                IntroFirstPersonCamera.UpdateAndApply(sequenceCamera, car);
            }
        }

        private void Cache()
        {
            if (_cached || car == null) return;
            _startPosition = car.position;
            _wheelRest = new Quaternion[wheels.Length];
            _wheelSign = new float[wheels.Length];
            for (int i = 0; i < wheels.Length; i++)
            {
                _wheelRest[i] = wheels[i].localRotation;
                // Right-hand wheels are mirrored, so their local X axis points the other way.
                _wheelSign[i] = Mathf.Sign(Vector3.Dot(wheels[i].right, car.right));
            }
            _cached = true;
        }

        /// <summary>Poses the car, wheels and camera for a given time. Safe to call in edit mode for previews.</summary>
        public void Evaluate(float time)
        {
            Cache();
            if (car == null) return;

            float distance = Mathf.Min(speed * Mathf.Max(0f, time), maxDistance);
            float bob = (Mathf.PerlinNoise(time * 2.3f, 0.37f) - 0.5f) * 0.012f;
            car.position = _startPosition + car.forward * distance + Vector3.up * bob;

            float spin = distance / wheelRadius * Mathf.Rad2Deg;
            for (int i = 0; i < wheels.Length; i++)
                wheels[i].localRotation = _wheelRest[i] * Quaternion.AngleAxis(spin * _wheelSign[i], Vector3.right);

            if (sequenceCamera == null || shots.Length == 0) return;
            int index = 0;
            for (int i = 0; i < shots.Length; i++)
                if (time >= shots[i].startTime) index = i;
            Shot shot = shots[index];
            float local = time - shot.startTime;
            var shake = new Vector3(
                Mathf.PerlinNoise(time * 1.7f, 1.3f) - 0.5f,
                Mathf.PerlinNoise(time * 2.1f, 5.1f) - 0.5f,
                0f) * (cameraShake * 2f);
            Transform cam = sequenceCamera.transform;
            cam.position = car.TransformPoint(shot.position + shot.drift * local + shake);
            cam.rotation = Quaternion.LookRotation(car.TransformPoint(shot.lookAt) - cam.position, Vector3.up);
            if (shot.fieldOfView > 1f) sequenceCamera.fieldOfView = shot.fieldOfView;
        }
    }
}
