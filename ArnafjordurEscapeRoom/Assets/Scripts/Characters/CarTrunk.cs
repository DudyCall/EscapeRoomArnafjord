using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

namespace Arnafjordur.Characters
{
    /// <summary>
    /// Opens and closes the Golf's tailgate. Sits on the "Tailgate" transform, whose origin is the roof hinge
    /// (see build_golf.py). The active character toggles it with T, or by clicking the hatch, when close enough.
    /// The trunk is empty for now; <see cref="IsOpen"/> and <see cref="Toggled"/> are there for later puzzles.
    /// </summary>
    public sealed class CarTrunk : MonoBehaviour
    {
        [SerializeField] private float openAngle = 78f;
        [SerializeField] private float openSeconds = 0.9f;
        [SerializeField, Tooltip("How close (m, ground plane) the active character must be to the rear of the car.")]
        private float reach = 2.2f;

        [SerializeField, Tooltip("Items lying in the trunk: only reachable while it is open, until someone takes them.")]
        private PickupItem[] contents = new PickupItem[0];

        private Quaternion _closedLocal;
        private Vector3 _hingeAxisLocal;
        private Vector3 _reachPoint;
        private float _open;          // 0 closed .. 1 open (animated)
        private PartyController _party;

        public bool IsOpen { get; private set; }
        public event System.Action<bool> Toggled;

        private void Awake()
        {
            _closedLocal = transform.localRotation;
            // Hinge runs across the car. Pick the sign that lifts the hatch upward rather than into the cabin.
            _hingeAxisLocal = Vector3.right;
            Renderer r = GetComponentInChildren<Renderer>();
            Vector3 centre = r != null ? r.bounds.center : transform.position;
            _reachPoint = centre;   // the car is parked, so the closed hatch centre stays put
            Vector3 lever = centre - transform.position;
            Vector3 axisWorld = transform.TransformDirection(_hingeAxisLocal);
            if ((Quaternion.AngleAxis(30f, axisWorld) * lever).y < lever.y) _hingeAxisLocal = -_hingeAxisLocal;
        }

        private void Update()
        {
            if (WantsToggle()) Toggle();

            float target = IsOpen ? 1f : 0f;
            if (!Mathf.Approximately(_open, target))
            {
                _open = Mathf.MoveTowards(_open, target, Time.deltaTime / Mathf.Max(0.01f, openSeconds));
                float eased = _open * _open * (3f - 2f * _open);
                transform.localRotation = _closedLocal * Quaternion.AngleAxis(openAngle * eased, _hingeAxisLocal);
            }
        }

        public void Toggle()
        {
            IsOpen = !IsOpen;
            UpdateContents();
            Toggled?.Invoke(IsOpen);
        }

        /// <summary>Wires the items lying in the trunk (used by the editor scene builder).</summary>
        public void Configure(PickupItem[] items)
        {
            contents = items ?? new PickupItem[0];
        }

        private void Start() => UpdateContents();

        /// <summary>Items still in the trunk can only be picked up while the lid is open.</summary>
        private void UpdateContents()
        {
            for (int i = 0; i < contents.Length; i++)
            {
                PickupItem item = contents[i];
                if (item == null) continue;
                if (item.IsHeld || item.transform.parent != transform.parent)
                {
                    contents[i] = null;     // taken out: it is an ordinary item from now on
                    continue;
                }

                item.enabled = IsOpen;      // a disabled item is invisible to "nearest item" lookups
                item.SetCollidersEnabled(IsOpen);
            }
        }

        private bool WantsToggle()
        {
            if (!CharacterInReach()) return false;

            Keyboard kb = Keyboard.current;
            if (kb != null && kb.tKey.wasPressedThisFrame) return true;

            Vector2 pos;
            if (Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame) pos = Mouse.current.position.ReadValue();
            else if (Touchscreen.current != null && Touchscreen.current.primaryTouch.press.wasPressedThisFrame) pos = Touchscreen.current.primaryTouch.position.ReadValue();
            else return false;

            if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject()) return false;
            Camera cam = Camera.main;
            if (cam == null) return false;
            return Physics.Raycast(cam.ScreenPointToRay(pos), out RaycastHit hit, 60f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Collide)
                   && hit.collider.transform.IsChildOf(transform);
        }

        private bool CharacterInReach()
        {
            if (_party == null) _party = FindAnyObjectByType<PartyController>();
            CharacterMotor active = _party != null ? _party.ActiveCharacter : null;
            if (active == null) return false;
            // Measure to the middle of the closed hatch (behind the car), not to the roof hinge.
            Vector3 d = active.transform.position - _reachPoint;
            d.y = 0f;
            return d.magnitude <= reach;
        }
    }
}
