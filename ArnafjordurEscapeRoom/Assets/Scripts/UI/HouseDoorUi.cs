using System.Collections;
using System.Collections.Generic;
using Arnafjordur.Characters;
using Arnafjordur.Core;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace Arnafjordur.UI
{
    /// <summary>
    /// Knocking on village doors and going in and out of houses that have rooms (<see cref="HouseInterior"/>).
    /// Shows a tappable prompt when the active character stands at a <see cref="HouseDoor"/>, takes E or a click on
    /// the door, shows what happens after a knock, and fades to black while the party is moved through a door. A locked
    /// door stays shut until someone at it uses its <see cref="HouseKey"/> (U); key messages show as toasts here.
    /// </summary>
    public sealed class HouseDoorUi : MonoBehaviour
    {
        [SerializeField] private PartyController party;
        [SerializeField, Tooltip("Companions in Follow mode within this distance come through the door too.")]
        private float bringAlongRadius = 12f;

        private Font _font;
        private GameObject _promptRoot;
        private Text _promptLabel;
        private GameObject _toastRoot;
        private Text _toastLabel;
        private Image _fade;
        private float _toastUntil;
        private bool _busy;
        private bool _autoFirstPerson;
        private HouseDoor _current;
        private ThirdPersonCameraRig _rig;
        private static readonly RaycastHit[] Hits = new RaycastHit[16];

        public void Configure(PartyController partyController) => party = partyController;

        private void Awake()
        {
            if (party == null) party = FindAnyObjectByType<PartyController>();
            BuildUi();
        }

        private void OnEnable() => HouseDoor.Notice += OnDoorNotice;
        private void OnDisable() => HouseDoor.Notice -= OnDoorNotice;
        private void OnDoorNotice(string text) => ShowToast(text, 3.5f);

        private void Update()
        {
            _current = _busy ? null : FindDoorInReach();
            if (_current != null && WantsInteract(_current)) Interact(_current);

            _promptRoot.SetActive(_current != null);
            if (_current != null) _promptLabel.text = PromptText(_current);
            _toastRoot.SetActive(Time.time < _toastUntil);
        }

        private HouseDoor FindDoorInReach()
        {
            CharacterMotor active = party != null ? party.ActiveCharacter : null;
            if (active == null) return null;
            SeaMineBomb bomb = SeaMineBomb.Active;
            if (bomb != null && (bomb.IsActiveCharacterInspecting || bomb.IsCharacterInReach(active))) return null;

            HouseDoor best = null;
            float bestDistance = float.PositiveInfinity;
            foreach (HouseDoor door in HouseDoor.All)
            {
                if (door.InReach(active, out float d) && d < bestDistance)
                {
                    best = door;
                    bestDistance = d;
                }
            }

            return best;
        }

        private string PromptText(HouseDoor door)
        {
            if (door.IsInside) return "[E] Go outside";
            if (door.IsLocked)
            {
                PickupItem held = party != null && party.ActiveItemHolder != null ? party.ActiveItemHolder.HeldItem : null;
                if (held != null && held.GetComponent<HouseKey>() != null) return "[U] Unlock with the key";
                return door.HasKnocked ? "Locked. Hold the key and press [U]" : "[E] Knock on the door";
            }

            if (door.HasKnocked && door.CanGoThrough) return "[E] Go in";
            return door.HasKnocked ? "[E] Knock again" : "[E] Knock on the door";
        }

        private bool WantsInteract(HouseDoor door)
        {
            Keyboard kb = Keyboard.current;
            if (kb != null && kb.eKey.wasPressedThisFrame) return true;

            Vector2 pos;
            if (Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame) pos = Mouse.current.position.ReadValue();
            else if (Touchscreen.current != null && Touchscreen.current.primaryTouch.press.wasPressedThisFrame) pos = Touchscreen.current.primaryTouch.position.ReadValue();
            else return false;

            if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject()) return false;
            Camera cam = Camera.main;
            if (cam == null) return false;
            int n = Physics.RaycastNonAlloc(cam.ScreenPointToRay(pos), Hits, 40f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Collide);
            for (int i = 0; i < n; i++)
            {
                if (Hits[i].collider.GetComponentInParent<HouseDoor>() == door) return true;
            }

            return false;
        }

        private void Interact(HouseDoor door)
        {
            if (door.IsInside || (door.HasKnocked && door.CanGoThrough && !door.IsLocked))
            {
                StartCoroutine(GoThrough(door));
                return;
            }

            StartCoroutine(KnockRoutine(door));
        }

        private IEnumerator KnockRoutine(HouseDoor door)
        {
            _busy = true;
            CharacterMotor knocker = party.ActiveCharacter;
            knocker.Stop();
            bool turned = party != null && !party.IsFirstPerson;
            if (turned) knocker.LookYawOverride = door.StandYaw + 180f;   // face the door
            door.Knock();
            ShowToast("*knock knock knock*", 1.4f);
            yield return new WaitForSeconds(1.5f);
            if (turned && !party.IsFirstPerson) knocker.LookYawOverride = null;
            ShowToast(door.KnockReply, 4.5f);
            _busy = false;
        }

        /// <summary>Fades out, moves the active character (and companions following them) to the door's other side, fades in.</summary>
        private IEnumerator GoThrough(HouseDoor door)
        {
            _busy = true;
            HouseDoor target = door.OtherSide;
            HouseInterior interior = door.Interior;
            bool entering = !door.IsInside;
            CharacterMotor leader = party.ActiveCharacter;
            HouseInterior leaderIn = HouseInterior.Containing(leader);

            var group = new List<CharacterMotor> { leader };
            if (party.CurrentCompanionMode == PartyController.CompanionMode.Follow)
            {
                foreach (CharacterMotor m in party.Characters)
                {
                    if (m == null || m == leader || HouseInterior.Containing(m) != leaderIn) continue;
                    Vector3 d = m.transform.position - leader.transform.position;
                    d.y = 0f;
                    if (d.magnitude <= bringAlongRadius) group.Add(m);
                }
            }

            yield return Fade(1f, 0.35f);

            for (int i = 0; i < group.Count; i++)
            {
                interior.SetInside(group[i], entering);
            }

            Physics.SyncTransforms();
            for (int i = 0; i < group.Count; i++)
            {
                Place(group[i], ArrivalSpot(target, i, group[i]), target.StandYaw);
            }

            Physics.SyncTransforms();
            if (_rig == null) _rig = FindAnyObjectByType<ThirdPersonCameraRig>();
            if (_rig != null)
            {
                // Rooms are small, so look through the character's eyes indoors and give the old view back outside.
                if (entering && !_rig.IsFirstPerson)
                {
                    party.ToggleFirstPerson();
                    _autoFirstPerson = true;
                }
                else if (!entering && _autoFirstPerson)
                {
                    if (_rig.IsFirstPerson) party.ToggleFirstPerson();
                    _autoFirstPerson = false;
                }

                _rig.SetTarget(leader.transform, snap: true);
                _rig.SetOrbit(target.StandYaw, _rig.IsFirstPerson ? 0f : 18f, _rig.Distance, snap: true);
            }

            if (entering) ShowToast($"Inside the {interior.HouseName}", 2.5f);
            yield return new WaitForSeconds(0.15f);
            yield return Fade(0f, 0.45f);
            _busy = false;
        }

        /// <summary>The leader lands on the door's stand point; the others a little further in, either side.</summary>
        private static Vector3 ArrivalSpot(HouseDoor door, int slot, CharacterMotor motor)
        {
            Vector3 p = door.StandPoint;
            if (slot > 0)
            {
                Quaternion facing = Quaternion.Euler(0f, door.StandYaw, 0f);
                Vector3 side = facing * Vector3.right * (slot == 1 ? -0.6f : 0.6f);
                p += facing * Vector3.forward * 0.6f + side;
                if (motor.Area != null) p = motor.Area.Clamp(p);
            }

            // Settle on the floor (or ground) under the spot, starting below any ceiling.
            float best = float.NegativeInfinity;
            int n = Physics.RaycastNonAlloc(p + Vector3.up * 1.1f, Vector3.down, Hits, 3.5f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n; i++)
            {
                if (Hits[i].collider.GetComponentInParent<CharacterMotor>() != null) continue;
                best = Mathf.Max(best, Hits[i].point.y);
            }

            if (!float.IsNegativeInfinity(best)) p.y = best + 0.03f;
            return p;
        }

        private static void Place(CharacterMotor motor, Vector3 position, float yaw)
        {
            motor.Stop();
            CharacterController controller = motor.GetComponent<CharacterController>();
            bool wasEnabled = controller != null && controller.enabled;
            if (controller != null) controller.enabled = false;
            motor.transform.SetPositionAndRotation(position, Quaternion.Euler(0f, yaw, 0f));
            if (controller != null) controller.enabled = wasEnabled;
        }

        private IEnumerator Fade(float to, float seconds)
        {
            _fade.gameObject.SetActive(true);
            float from = _fade.color.a;
            for (float t = 0f; t < seconds; t += Time.unscaledDeltaTime)
            {
                SetFade(Mathf.Lerp(from, to, t / seconds));
                yield return null;
            }

            SetFade(to);
            _fade.gameObject.SetActive(to > 0.001f);
        }

        private void SetFade(float alpha)
        {
            Color c = _fade.color;
            c.a = alpha;
            _fade.color = c;
        }

        private void ShowToast(string message, float seconds)
        {
            _toastLabel.text = message;
            _toastUntil = Time.time + seconds;
            _toastRoot.SetActive(true);
        }

        private void BuildUi()
        {
            _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            var canvasGo = new GameObject("HouseDoorUi_Canvas", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvasGo.transform.SetParent(transform, false);
            Canvas canvas = canvasGo.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 20;
            CanvasScaler scaler = canvasGo.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;
            var root = (RectTransform)canvasGo.transform;

            _promptRoot = Panel("DoorPrompt", root, new Vector2(0.5f, 1f), new Vector2(0f, -132f), new Vector2(340f, 42f), Palette.UiAccentAmber);
            _promptRoot.AddComponent<Button>().onClick.AddListener(() =>
            {
                if (_current != null && !_busy) Interact(_current);
            });
            _promptLabel = Label(_promptRoot.transform, 17, Palette.DeepFjordSlate, FontStyle.Bold);
            _promptRoot.SetActive(false);

            _toastRoot = Panel("DoorToast", root, new Vector2(0.5f, 0f), new Vector2(0f, 150f), new Vector2(760f, 54f), Palette.UiPanelSlate);
            _toastLabel = Label(_toastRoot.transform, 19, Palette.UiTextParchment, FontStyle.Normal);
            _toastRoot.GetComponent<Image>().raycastTarget = false;
            _toastLabel.raycastTarget = false;
            _toastRoot.SetActive(false);

            var fadeGo = new GameObject("DoorFade", typeof(RectTransform), typeof(Image));
            fadeGo.transform.SetParent(root, false);
            var fadeRt = (RectTransform)fadeGo.transform;
            fadeRt.anchorMin = Vector2.zero;
            fadeRt.anchorMax = Vector2.one;
            fadeRt.offsetMin = fadeRt.offsetMax = Vector2.zero;
            _fade = fadeGo.GetComponent<Image>();
            _fade.color = new Color(0.02f, 0.03f, 0.04f, 0f);
            _fade.raycastTarget = false;
            fadeGo.SetActive(false);
        }

        private static GameObject Panel(string name, RectTransform parent, Vector2 anchor, Vector2 offset, Vector2 size, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = rt.anchorMax = anchor;
            rt.pivot = new Vector2(0.5f, anchor.y);
            rt.anchoredPosition = offset;
            rt.sizeDelta = size;
            go.GetComponent<Image>().color = color;
            return go;
        }

        private Text Label(Transform parent, int size, Color color, FontStyle style)
        {
            var go = new GameObject("Label", typeof(RectTransform), typeof(Text));
            go.transform.SetParent(parent, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(10f, 4f);
            rt.offsetMax = new Vector2(-10f, -4f);
            Text text = go.GetComponent<Text>();
            text.font = _font;
            text.fontSize = size;
            text.color = color;
            text.fontStyle = style;
            text.alignment = TextAnchor.MiddleCenter;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            return text;
        }
    }
}
