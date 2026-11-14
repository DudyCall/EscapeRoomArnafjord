using System;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.SceneManagement;

namespace Arnafjordur.Intro
{
    /// <summary>
    /// Final intro beat: the parked car's lights go out and the three players climb out one after another,
    /// walk round to the camera side of the car and stand looking up at the church.
    /// </summary>
    public sealed class IntroCarExit : MonoBehaviour
    {
        private static readonly int SpeedHash = Animator.StringToHash("Speed");
        private static readonly int StandHash = Animator.StringToHash("Stand");

        [Serializable]
        public struct Occupant
        {
            public Transform root;
            public Animator animator;
            [Tooltip("The car door this player uses; hinged at its own origin.")]
            public Transform door;
            [Tooltip("Degrees the door swings about the car's up axis when open (sign picks the direction).")]
            public float doorOpenAngle;
            [Tooltip("Seconds into the scene when this player's door starts to open.")]
            public float standTime;
            [Tooltip("World-space ground points: [0] is just outside their door, the last is where they end up standing.")]
            public Vector3[] route;
        }

        private enum Phase { Seated, ClimbingOut, ClosingDoor, Walking, Arrived }

        [Header("Players")]
        [SerializeField] private Occupant[] occupants = Array.Empty<Occupant>();
        [SerializeField, Min(0.05f), Tooltip("Seconds for a door to swing open.")] private float doorOpenSeconds = 0.7f;
        [SerializeField, Min(0.05f), Tooltip("Seconds for a door to swing shut.")] private float doorCloseSeconds = 0.5f;
        [SerializeField, Min(0f), Tooltip("Seconds after the door starts opening before the player moves.")]
        private float doorLeadSeconds = 0.45f;
        [SerializeField, Min(0f), Tooltip("Seconds the player stands by the door while it shuts, before walking off.")]
        private float walkDelaySeconds = 0.35f;
        [SerializeField, Min(0.1f), Tooltip("Seconds to get from the seat to standing outside the door.")]
        private float climbOutSeconds = 1.1f;
        [SerializeField, Tooltip("Metres per second.")] private float walkSpeed = 1.6f;
        [SerializeField, Tooltip("Animator Speed value that plays the full walk clip.")] private float walkAnimSpeed = 1.75f;
        [SerializeField] private float turnSpeedDeg = 300f;
        [SerializeField, Tooltip("World point everyone turns to face once they have stopped.")] private Vector3 faceTarget;

        [Header("Car")]
        [SerializeField] private GameObject headlights;
        [SerializeField] private Renderer bodyRenderer;
        [SerializeField, Tooltip("Body materials with the lamps switched off.")] private Material[] lightsOffMaterials = Array.Empty<Material>();
        [SerializeField] private float lightsOffTime = 0.6f;

        [Header("Camera (eases in from where the arrival shot ended)")]
        [SerializeField] private Camera sequenceCamera;
        [SerializeField] private Vector3 cameraEndPosition;
        [SerializeField] private Vector3 cameraEndLookAt;
        [SerializeField] private float cameraEndFieldOfView = 40f;
        [SerializeField, Min(0.1f)] private float cameraMoveSeconds = 8f;

        [Header("When done")]
        [SerializeField, Min(0f), Tooltip("Seconds to hold once everyone is standing.")] private float holdSeconds = 1.5f;
        [SerializeField, Tooltip("Optional scene to load when the beat ends.")] private string nextSceneName = "";
        [SerializeField] private UnityEvent finished = new UnityEvent();

        private Phase[] _phase;
        private Vector3[] _fromPosition;
        private Quaternion[] _fromRotation;
        private int[] _routeIndex;
        private Quaternion[] _doorRest;
        private float[] _outAt;
        private Vector3 _camStartPosition, _camStartLookAt;
        private float _camStartFov;
        private float _allArrivedAt = -1f;
        private bool _lightsOff, _finishedRaised;
        private Transform[] _occupantRoots = Array.Empty<Transform>();

        /// <summary>Seconds since the scene started.</summary>
        public float Elapsed { get; private set; }

        /// <summary>The players and their routes; each ends up on the last point of their route.</summary>
        public Occupant[] Occupants => occupants;

        /// <summary>World point everyone faces once they have stopped (the church).</summary>
        public Vector3 FaceTarget => faceTarget;

        /// <summary>Raised once when everyone is out and the hold has elapsed.</summary>
        public UnityEvent Finished => finished;

        /// <summary>Scene loaded when the beat ends; empty to stay in this scene.</summary>
        public string NextSceneName
        {
            get => nextSceneName;
            set => nextSceneName = value;
        }

        /// <summary>Wires the sequence (used by the editor scene builder).</summary>
        public void Configure(Occupant[] players, Vector3 lookTarget, Camera cam, Vector3 camEndPosition, Vector3 camEndLookAt,
            float camEndFov, GameObject headlightRoot, Renderer body, Material[] bodyLightsOff)
        {
            occupants = players ?? Array.Empty<Occupant>();
            faceTarget = lookTarget;
            sequenceCamera = cam;
            cameraEndPosition = camEndPosition;
            cameraEndLookAt = camEndLookAt;
            cameraEndFieldOfView = camEndFov;
            headlights = headlightRoot;
            bodyRenderer = body;
            lightsOffMaterials = bodyLightsOff ?? Array.Empty<Material>();
        }

        private void Start()
        {
            int n = occupants.Length;
            _phase = new Phase[n];
            _fromPosition = new Vector3[n];
            _fromRotation = new Quaternion[n];
            _routeIndex = new int[n];
            _doorRest = new Quaternion[n];
            _outAt = new float[n];
            _occupantRoots = new Transform[n];
            for (int i = 0; i < n; i++)
            {
                _outAt[i] = -1f;
                // A saved link to a player inside the car prefab can drop out when the prefab is edited later;
                // the Animator sits on the same object, so recover the player from it rather than leave them seated.
                if (occupants[i].root == null && occupants[i].animator != null)
                    occupants[i].root = occupants[i].animator.transform;
                _occupantRoots[i] = occupants[i].root;
                if (occupants[i].root == null)
                    Debug.LogWarning($"[IntroCarExit] player {i} is not linked - run Tools/Intro/Rebuild Car Exit Scene", this);
                if (occupants[i].door != null) _doorRest[i] = occupants[i].door.localRotation;
            }

            if (sequenceCamera != null)
            {
                Transform cam = sequenceCamera.transform;
                _camStartPosition = cam.position;
                _camStartLookAt = cam.position + cam.forward * Vector3.Distance(cam.position, cameraEndLookAt);
                _camStartFov = sequenceCamera.fieldOfView;
            }
        }

        private void Update()
        {
            float dt = Time.deltaTime;
            Elapsed += dt;

            if (!_lightsOff && Elapsed >= lightsOffTime)
            {
                _lightsOff = true;
                IntroMusic.Stop();              // the radio dies with the ignition
                if (headlights != null) headlights.SetActive(false);
                if (bodyRenderer != null && lightsOffMaterials.Length > 0) bodyRenderer.sharedMaterials = lightsOffMaterials;
            }

            bool allArrived = true;
            for (int i = 0; i < occupants.Length; i++)
            {
                Step(i, dt);
                if (_phase[i] != Phase.Arrived) allArrived = false;
            }

            if (allArrived && _allArrivedAt < 0f) _allArrivedAt = Elapsed;
            if (!_finishedRaised && _allArrivedAt >= 0f && Elapsed >= _allArrivedAt + holdSeconds)
            {
                _finishedRaised = true;
                finished.Invoke();
                if (!string.IsNullOrEmpty(nextSceneName)) SceneManager.LoadScene(nextSceneName);
            }
        }

        private void LateUpdate()
        {
            if (sequenceCamera == null) return;
            float s = Mathf.SmoothStep(0f, 1f, Elapsed / cameraMoveSeconds);
            Vector3 position = Vector3.Lerp(_camStartPosition, cameraEndPosition, s);
            Vector3 lookAt = Vector3.Lerp(_camStartLookAt, cameraEndLookAt, s);
            sequenceCamera.transform.SetPositionAndRotation(position, Quaternion.LookRotation(lookAt - position, Vector3.up));
            sequenceCamera.fieldOfView = Mathf.Lerp(_camStartFov, cameraEndFieldOfView, s);

            if (Application.isPlaying)
            {
                IntroFirstPersonCamera.UpdateAndApply(sequenceCamera, _occupantRoots);
            }
        }

        private void Step(int i, float dt)
        {
            Occupant occ = occupants[i];
            if (occ.root == null || occ.route == null || occ.route.Length == 0)
            {
                _phase[i] = Phase.Arrived;
                return;
            }

            SwingDoor(i);

            float climbStart = occ.standTime + (occ.door != null ? doorLeadSeconds : 0f);
            float animSpeed = 0f;
            switch (_phase[i])
            {
                case Phase.Seated:
                    if (Elapsed < climbStart) return;
                    _phase[i] = Phase.ClimbingOut;
                    _fromPosition[i] = occ.root.position;
                    _fromRotation[i] = occ.root.rotation;
                    if (occ.animator != null) occ.animator.SetTrigger(StandHash);
                    if (occ.root.TryGetComponent(out BeerDrinker drinker)) drinker.Finish();   // they take the beer with them
                    break;

                case Phase.ClimbingOut:
                {
                    float s = Mathf.SmoothStep(0f, 1f, (Elapsed - climbStart) / climbOutSeconds);
                    // Straighten up from the car's tilt on the slope while sliding out of the door.
                    Quaternion upright = Quaternion.Euler(0f, _fromRotation[i].eulerAngles.y, 0f);
                    occ.root.SetPositionAndRotation(
                        Vector3.Lerp(_fromPosition[i], occ.route[0], s),
                        Quaternion.Slerp(_fromRotation[i], upright, s));
                    if (s >= 1f)
                    {
                        _outAt[i] = Elapsed;
                        _routeIndex[i] = 1;
                        _phase[i] = Phase.ClosingDoor;
                    }
                    break;
                }

                case Phase.ClosingDoor:
                    if (occ.door != null && Elapsed < _outAt[i] + walkDelaySeconds) break;
                    _phase[i] = occ.route.Length > 1 ? Phase.Walking : Phase.Arrived;
                    break;

                case Phase.Walking:
                {
                    Vector3 target = occ.route[_routeIndex[i]];
                    Vector3 position = Vector3.MoveTowards(occ.root.position, target, walkSpeed * dt);
                    Face(occ.root, target - occ.root.position, dt);
                    occ.root.position = position;
                    animSpeed = walkAnimSpeed;
                    if ((target - position).sqrMagnitude < 0.0004f && ++_routeIndex[i] >= occ.route.Length)
                        _phase[i] = Phase.Arrived;
                    break;
                }

                case Phase.Arrived:
                    Face(occ.root, faceTarget - occ.root.position, dt);
                    break;
            }

            if (occ.animator != null) occ.animator.SetFloat(SpeedHash, animSpeed, 0.12f, dt);
        }

        /// <summary>Opens the player's door ahead of them and shuts it again once they are standing outside.</summary>
        private void SwingDoor(int i)
        {
            Transform door = occupants[i].door;
            if (door == null || door.parent == null) return;
            float open = Mathf.SmoothStep(0f, 1f, (Elapsed - occupants[i].standTime) / doorOpenSeconds);
            float shut = _outAt[i] < 0f ? 0f : Mathf.SmoothStep(0f, 1f, (Elapsed - _outAt[i]) / doorCloseSeconds);
            float angle = occupants[i].doorOpenAngle * Mathf.Clamp01(open - shut);
            // Swing about the car's up axis whatever orientation the model's own axes were exported with.
            Quaternion parent = door.parent.rotation;
            Vector3 hingeAxis = Quaternion.Inverse(parent) * door.parent.root.up;
            door.localRotation = Quaternion.AngleAxis(angle, hingeAxis) * _doorRest[i];
        }

        private void Face(Transform t, Vector3 direction, float dt)
        {
            direction.y = 0f;
            if (direction.sqrMagnitude < 1e-6f) return;
            t.rotation = Quaternion.RotateTowards(t.rotation, Quaternion.LookRotation(direction.normalized, Vector3.up), turnSpeedDeg * dt);
        }
    }
}
