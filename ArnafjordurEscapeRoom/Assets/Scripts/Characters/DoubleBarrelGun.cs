using Arnafjordur.Core;
using Arnafjordur.Intro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;

namespace Arnafjordur.Characters
{
    /// <summary>
    /// The old side-by-side double-barrelled shotgun on the boathouse workbench. Loaded with two shells: U / Space
    /// fires one barrel (left click too while aiming), then the other, then it only clicks. Carried at the hip in both
    /// hands, it comes up to the shoulder for a shot. In first person the gun shows in the lower right of the view, and
    /// holding the right mouse button (or toggling Z) raises it to the eye with a zoomed view and a crosshair; the shot
    /// goes where the crosshair points. Runs after <see cref="CharacterItemHolder"/> so its pose wins.
    /// </summary>
    [DefaultExecutionOrder(25)]
    public sealed class DoubleBarrelGun : MonoBehaviour, IUsableItem
    {
        private const int Pellets = 9;
        private const float SpreadDeg = 2.2f, Range = 90f, Cooldown = 0.35f;
        private const int SampleRate = 22050;

        [SerializeField, Min(0)] private int shells = 2;
        [SerializeField, Tooltip("Between the two muzzles, +Z pointing out of the barrels.")] private Transform muzzle;
        [SerializeField] private Transform rightGrip, leftGrip;
        [SerializeField] private GameObject flash;
        [SerializeField] private Light flashLight;

        private PickupItem _item;
        private ThirdPersonCameraRig _rig;
        private bool _drivingZoom, _aimToggle;
        private float _aimBlend, _shoulder, _kick, _cooldown, _flashTimer, _messageUntil;
        private string _message;
        private CharacterItemHolder _bonesFor;
        private Transform _rUpper, _rLower;
        private Vector3 _lGripLocal;
        private bool _hasLGripLocal;
        private AudioSource _audio;
        private static AudioClip _boomClip, _clickClip;
        private Puff[] _puffs;
        private Transform[] _marks;
        private int _nextPuff, _nextMark;
        private static Texture2D _white;

        private struct Puff
        {
            public Transform T;
            public Material M;
            public Vector3 Velocity;
            public Color Color;
            public float Age, Life, Size;
        }

        public int Shells => shells;
        public bool IsAiming { get; private set; }

        /// <summary>Sets the gun up (used by the editor scene builder).</summary>
        public void Configure(Transform muzzlePoint, Transform rightHand, Transform leftHand, GameObject flashObject, Light light, int loadedShells = 2)
        {
            muzzle = muzzlePoint;
            rightGrip = rightHand;
            leftGrip = leftHand;
            flash = flashObject;
            flashLight = light;
            shells = loadedShells;
        }

        /// <summary>Loads the gun with <paramref name="count"/> shells (builder and tests).</summary>
        public void Reload(int count)
        {
            shells = Mathf.Max(0, count);
            if (_item == null) _item = GetComponent<PickupItem>();
            RefreshLabel();
        }

        private void Awake()
        {
            _item = GetComponent<PickupItem>();
            if (flash != null) flash.SetActive(false);
            RefreshLabel();
        }

        private void OnDisable()
        {
            if (_drivingZoom && _rig != null) _rig.AimZoom = 0f;
            _drivingZoom = false;
        }

        private CharacterItemHolder Holder => _item != null ? _item.Holder : null;

        private bool InFirstPerson(CharacterItemHolder holder)
        {
            if (_rig == null) _rig = FindAnyObjectByType<ThirdPersonCameraRig>();
            return holder != null && _rig != null && _rig.IsFirstPerson && !_rig.IsInspectingOverride && _rig.Target == holder.transform;
        }

        private void Update()
        {
            float dt = Time.deltaTime;
            _cooldown = Mathf.Max(0f, _cooldown - dt);
            _kick = Mathf.MoveTowards(_kick, 0f, dt * 5f);
            _shoulder = Mathf.Max(0f, _shoulder - dt);
            if (_flashTimer > 0f)
            {
                _flashTimer -= dt;
                if (_flashTimer <= 0f && flash != null) flash.SetActive(false);
            }

            TickPuffs(dt);

            CharacterItemHolder holder = Holder;
            bool fp = InFirstPerson(holder);
            if (!fp) _aimToggle = false;
            else if (Keyboard.current != null && Keyboard.current.zKey.wasPressedThisFrame) _aimToggle = !_aimToggle;

            bool rmb = Mouse.current != null && Mouse.current.rightButton.isPressed;
            IsAiming = fp && (rmb || _aimToggle);
            _aimBlend = Mathf.MoveTowards(_aimBlend, IsAiming ? 1f : 0f, dt * 6f);

            if (fp)
            {
                _rig.AimZoom = Mathf.SmoothStep(0f, 1f, _aimBlend);
                _drivingZoom = true;
            }
            else if (_drivingZoom)
            {
                if (_rig != null) _rig.AimZoom = 0f;
                _drivingZoom = false;
                _aimBlend = 0f;
            }

            // Shown in full while carried, also in first person, where the camera turns the character's body (and
            // anything parented to its hand) into shadow-only renderers.
            if (holder != null && _item != null)
            {
                if (_firstRenderer == null) _firstRenderer = GetComponentInChildren<Renderer>(true);
                if (_firstRenderer != null && _firstRenderer.shadowCastingMode != ShadowCastingMode.On) _item.SetShadowCastingMode(ShadowCastingMode.On);
            }
        }

        private Renderer _firstRenderer;

        private void LateUpdate()
        {
            CharacterItemHolder holder = Holder;
            if (holder == null) return;
            if (holder.TryGetComponent(out CharacterDrunk drunk) && drunk.OverridesArmPose) return;
            if (SeaMineBomb.Active != null && holder.TryGetComponent(out CharacterMotor motor) && SeaMineBomb.Active.IsInspecting(motor)) return;

            ApplyThirdPersonPose(holder);

            if (!InFirstPerson(holder)) return;

            // First person: the gun is drawn from the eye, low right at the hip, or up the middle along the sight line.
            Transform cam = _rig.transform;
            float a = Mathf.SmoothStep(0f, 1f, _aimBlend);
            Vector3 local = Vector3.Lerp(new Vector3(0.2f, -0.24f, 0.42f), new Vector3(0f, -0.034f, 0.3f), a)
                            + new Vector3(0f, 0.012f, -0.07f) * _kick;
            Quaternion tilt = Quaternion.Slerp(Quaternion.Euler(3f, -4f, -4f), Quaternion.identity, a) * Quaternion.Euler(-7f * _kick, 0f, 0f);
            transform.SetPositionAndRotation(cam.TransformPoint(local), cam.rotation * tilt);
        }

        /// <summary>
        /// Poses the shotgun and both arms in the character's world frame (low-ready carry across the front, or raised
        /// to the shoulder after firing). Runs arm IK against fixed grip snapshots before re-applying the gun transform,
        /// because the gun is parented to <c>RightHand</c> while held.
        /// </summary>
        public void ApplyThirdPersonPose(CharacterItemHolder holder)
        {
            if (holder == null) return;
            ResolveArmBones(holder);

            Transform root = holder.transform;
            Vector3 up = root.up, fwd = root.forward, right = root.right;

            Vector3 rShoulder = _rUpper != null ? _rUpper.position : root.position + up * 1.39f + right * 0.20f - fwd * 0.05f;
            float raise = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(_shoulder / 0.25f));

            // Carry: cross-body low-ready in front of the waist/ribs so both hands reach comfortably, the stock clears
            // the right forearm, and the gun reads clearly from top-down and third-person views.
            // Raised: pulled up near the right shoulder for the shot.
            Vector3 lowRightGrip = rShoulder - up * 0.30f + fwd * 0.23f - right * 0.03f;
            Vector3 highRightGrip = rShoulder - up * 0.11f + fwd * 0.21f - right * 0.05f;
            Vector3 lowDir = (fwd * 0.64f - right * 0.77f + up * 0.04f).normalized;
            Vector3 highDir = (fwd * 0.90f - right * 0.43f).normalized;

            Vector3 dir = Vector3.Slerp(lowDir, highDir, raise);
            dir = Quaternion.AngleAxis(-8f * _kick, right) * dir;
            Vector3 gunUp = Vector3.ProjectOnPlane(up, dir).normalized;
            if (gunUp.sqrMagnitude < 1e-4f) gunUp = up;
            Quaternion gunRot = Quaternion.LookRotation(dir, gunUp);

            Vector3 targetRightGrip = Vector3.Lerp(lowRightGrip, highRightGrip, raise) - dir * (0.05f * _kick);
            Vector3 rightGripOffset = rightGrip != null ? rightGrip.localPosition : new Vector3(0f, -0.045f, -0.10f);
            Vector3 gunPos = targetRightGrip - gunRot * Vector3.Scale(rightGripOffset, transform.localScale);

            // Snapshot both grip world targets at the exact gun pose before moving any arm bones.
            transform.SetPositionAndRotation(gunPos, gunRot);
            Vector3 rightTarget = rightGrip != null ? rightGrip.position : targetRightGrip;
            Vector3 leftTarget = leftGrip != null ? leftGrip.position : gunPos + dir * 0.22f;

            SolveArms(holder, up, right, fwd, dir, gunUp, rightTarget, leftTarget);

            // Re-apply after arm IK since the shotgun is parented to RightHand and would otherwise rotate with the wrist.
            transform.SetPositionAndRotation(gunPos, gunRot);
        }

        private void ResolveArmBones(CharacterItemHolder holder)
        {
            if (_bonesFor == holder) return;
            _bonesFor = holder;
            _rUpper = FindDeep(holder.transform, "RightUpperArm");
            _rLower = FindDeep(holder.transform, "RightLowerArm");
            _hasLGripLocal = false;
            if (holder.LeftHandBone != null)
            {
                Vector3 measured = CharacterItemHolder.ComputeFistCentre(holder.transform, holder.LeftHandBone, holder.LeftLowerArmBone);
                float palmDist = holder.GripLocal.magnitude > 0.01f ? holder.GripLocal.magnitude : 0.062f;
                // ComputeFistCentre falls back to 0.08 m along the forearm when the mesh isn't readable; match the right palm scale.
                if (measured.sqrMagnitude > 0.0001f)
                {
                    _lGripLocal = measured.normalized * Mathf.Min(measured.magnitude, palmDist * 1.15f);
                    _hasLGripLocal = true;
                }
            }
        }

        /// <summary>Right hand on the stock wrist, left hand under the fore-end.</summary>
        private void SolveArms(CharacterItemHolder holder, Vector3 up, Vector3 right, Vector3 fwd,
            Vector3 gunDir, Vector3 gunUp, Vector3 rightTarget, Vector3 leftTarget)
        {
            if (holder.HandBone == null || holder.LeftHandBone == null
                || holder.LeftUpperArmBone == null || holder.LeftLowerArmBone == null) return;
            if (_rUpper == null || _rLower == null) return;

            Transform hand = holder.HandBone;
            Transform left = holder.LeftHandBone;
            Vector3 gunRight = Vector3.Cross(gunUp, gunDir).normalized;

            Vector3 rightPole = -up + right * 0.38f - fwd * 0.48f;
            Vector3 leftPole = -up - right * 0.55f - fwd * 0.30f;

            // Right hand: +X = palm (faces left/inward onto the stock wrist), +Y = fingers (wrap forward-down), +Z = thumb (along wrist).
            // Point the fingers slightly forward-down along the natural forearm line so the wrist doesn't kink.
            Vector3 rThumb = (gunUp * 0.85f + gunDir * 0.52f).normalized;
            Vector3 rPalm = (-gunRight * 0.88f + gunUp * 0.35f - gunDir * 0.30f).normalized;
            Vector3 rFingers = Vector3.Cross(rThumb, rPalm).normalized;
            Quaternion rightHandRot = rFingers.sqrMagnitude > 1e-4f
                ? Quaternion.LookRotation(rThumb, rFingers)
                : hand.rotation;

            // Left hand: -X = palm (faces up-right cradling fore-end), +Y = fingers (wrap up right side), +Z = thumb (along barrel).
            Vector3 lThumb = (gunDir + gunUp * 0.15f).normalized;
            Vector3 lPalmOut = -(gunUp + gunRight * 0.35f).normalized; // +X (back of hand)
            Vector3 lFingers = Vector3.Cross(lThumb, lPalmOut).normalized;
            Quaternion leftHandRot = lFingers.sqrMagnitude > 1e-4f
                ? Quaternion.LookRotation(lThumb, lFingers)
                : left.rotation;

            float palmDist = holder.GripLocal.magnitude > 0.01f ? holder.GripLocal.magnitude : 0.062f;
            for (int pass = 0; pass < 3; pass++)
            {
                hand.rotation = rightHandRot;
                BeerDrinker.SolveArm(_rUpper, _rLower, hand,
                    hand.position + (rightTarget - hand.TransformPoint(holder.GripLocal)), rightPole);
                hand.rotation = rightHandRot;

                left.rotation = leftHandRot;
                Vector3 leftFist = _hasLGripLocal
                    ? left.TransformPoint(_lGripLocal)
                    : left.position + (left.position - holder.LeftLowerArmBone.position).normalized * palmDist;
                BeerDrinker.SolveArm(holder.LeftUpperArmBone, holder.LeftLowerArmBone, left,
                    left.position + (leftTarget - leftFist), leftPole);
                left.rotation = leftHandRot;
            }
        }

        private static Transform FindDeep(Transform root, string name)
        {
            foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
            {
                if (t.name == name) return t;
            }

            return null;
        }

        public void OnUseItem(CharacterItemHolder user, PickupItem item)
        {
            if (user == null || (Application.isPlaying && _cooldown > 0f)) return;
            _cooldown = Cooldown;
            if (shells <= 0)
            {
                if (_clickClip == null) _clickClip = BuildClickClip();
                Play(_clickClip, 0.8f);
                Say("*click* Both barrels are empty.");
                return;
            }

            shells--;
            bool fp = InFirstPerson(user);
            Transform root = user.transform;
            Vector3 origin, dir;
            if (fp)
            {
                origin = _rig.transform.position;
                dir = _rig.transform.forward;
                _rig.AddRecoil(4.5f);
            }
            else
            {
                _shoulder = 0.9f;   // up to the shoulder, aimed straight ahead
                origin = root.position + root.up * 1.42f + root.forward * 0.4f;
                dir = root.forward;
            }

            _kick = 1f;
            if (!fp) ApplyThirdPersonPose(user);
            var rng = new System.Random(Time.frameCount);
            Vector3 side = Vector3.Cross(dir, Vector3.up).sqrMagnitude > 1e-4f ? Vector3.Cross(dir, Vector3.up).normalized : Vector3.right;
            Vector3 lift = Vector3.Cross(side, dir);
            for (int p = 0; p < Pellets; p++)
            {
                float ang = (float)rng.NextDouble() * Mathf.PI * 2f, r = Mathf.Sqrt((float)rng.NextDouble()) * SpreadDeg;
                Vector3 d = Quaternion.AngleAxis(Mathf.Cos(ang) * r, lift) * Quaternion.AngleAxis(Mathf.Sin(ang) * r, side) * dir;
                if (TryHit(origin, d, root, out RaycastHit hit)) Impact(hit, p < 4);
            }

            Vector3 muzzlePos = muzzle != null ? muzzle.position : origin + dir * 1f;
            Vector3 muzzleDir = muzzle != null ? muzzle.forward : dir;
            if (flash != null)
            {
                flash.SetActive(true);
                flash.transform.localRotation = Quaternion.Euler(0f, 0f, Random.Range(0f, 90f));
                _flashTimer = 0.06f;
            }

            for (int i = 0; i < 7; i++)
            {
                SpawnPuff(muzzlePos + muzzleDir * (0.05f + i * 0.06f), muzzleDir * (0.6f + i * 0.15f) + Vector3.up * 0.15f,
                    new Color(0.78f, 0.78f, 0.76f, 0.5f), 0.07f + i * 0.015f, 1.6f + i * 0.15f);
            }

            if (_boomClip == null) _boomClip = BuildBoomClip();
            Play(_boomClip, 1f);
            Say(shells == 1 ? "BOOM! One shell left." : "BOOM! Both barrels are empty now.");
            RefreshLabel();
        }

        private bool TryHit(Vector3 origin, Vector3 dir, Transform shooter, out RaycastHit best)
        {
            best = default;
            float bestDistance = float.PositiveInfinity;
            RaycastHit[] hits = Physics.RaycastAll(origin, dir, Range, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            foreach (RaycastHit h in hits)
            {
                if (h.distance >= bestDistance || h.collider.transform.IsChildOf(shooter) || h.collider.transform.IsChildOf(transform)) continue;
                best = h;
                bestDistance = h.distance;
            }

            return !float.IsPositiveInfinity(bestDistance);
        }

        private void Impact(RaycastHit hit, bool dust)
        {
            if (dust)
            {
                SpawnPuff(hit.point + hit.normal * 0.05f, hit.normal * 0.5f + Vector3.up * 0.2f, new Color(0.45f, 0.4f, 0.33f, 0.55f), 0.06f, 0.9f);
            }

            // A dark shot mark on whatever stands still (not on people).
            if (hit.collider.GetComponentInParent<CharacterMotor>() != null || hit.collider is CharacterController) return;
            if (_marks == null) _marks = new Transform[40];
            Transform mark = _marks[_nextMark];
            if (mark == null)
            {
                var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
                go.name = "ShotMark";
                go.hideFlags = HideFlags.DontSave;
                Kill(go.GetComponent<Collider>());
                var mr = go.GetComponent<MeshRenderer>();
                mr.sharedMaterial = MarkMaterial;
                mr.shadowCastingMode = ShadowCastingMode.Off;
                mark = go.transform;
                _marks[_nextMark] = mark;
            }

            _nextMark = (_nextMark + 1) % _marks.Length;
            mark.SetParent(hit.collider.transform, true);
            mark.SetPositionAndRotation(hit.point + hit.normal * 0.004f, Quaternion.LookRotation(-hit.normal) * Quaternion.Euler(0f, 0f, Random.Range(0f, 360f)));
            float s = Random.Range(0.018f, 0.03f);
            Vector3 ls = mark.parent != null ? mark.parent.lossyScale : Vector3.one;
            mark.localScale = new Vector3(s / Mathf.Max(1e-4f, ls.x), s / Mathf.Max(1e-4f, ls.y), s / Mathf.Max(1e-4f, ls.z));
        }

        private static Material _markMaterial;

        private static Material MarkMaterial
        {
            get
            {
                if (_markMaterial != null) return _markMaterial;
                Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
                _markMaterial = new Material(shader);
                _markMaterial.SetColor("_BaseColor", Palette.DeepFjordSlate * 0.35f);
                return _markMaterial;
            }
        }

        // ------------------------------------------------------------------ smoke and dust puffs

        private void SpawnPuff(Vector3 at, Vector3 velocity, Color color, float size, float life)
        {
            if (_puffs == null)
            {
                _puffs = new Puff[24];
                Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
                for (int i = 0; i < _puffs.Length; i++)
                {
                    GameObject go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                    go.name = "GunSmoke";
                    go.hideFlags = HideFlags.DontSave;
                    Kill(go.GetComponent<Collider>());
                    var mr = go.GetComponent<MeshRenderer>();
                    var mat = new Material(shader);
                    mat.SetFloat("_Surface", 1f);
                    mat.SetFloat("_Blend", 0f);
                    mat.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
                    mat.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
                    mat.SetFloat("_ZWrite", 0f);
                    mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                    mat.renderQueue = (int)RenderQueue.Transparent;
                    mat.SetOverrideTag("RenderType", "Transparent");
                    mr.sharedMaterial = mat;
                    mr.shadowCastingMode = ShadowCastingMode.Off;
                    mr.receiveShadows = false;
                    go.SetActive(false);
                    _puffs[i] = new Puff { T = go.transform, M = mat };
                }
            }

            ref Puff p = ref _puffs[_nextPuff];
            _nextPuff = (_nextPuff + 1) % _puffs.Length;
            p.T.gameObject.SetActive(true);
            p.T.position = at;
            p.T.localScale = Vector3.one * size;
            p.M.SetColor("_BaseColor", color);
            p.Velocity = velocity;
            p.Color = color;
            p.Size = size;
            p.Age = 0f;
            p.Life = life;
        }

        private void TickPuffs(float dt)
        {
            if (_puffs == null) return;
            for (int i = 0; i < _puffs.Length; i++)
            {
                ref Puff p = ref _puffs[i];
                if (!p.T.gameObject.activeSelf) continue;
                p.Age += dt;
                if (p.Age >= p.Life)
                {
                    p.T.gameObject.SetActive(false);
                    continue;
                }

                float u = p.Age / p.Life;
                p.Velocity = Vector3.Lerp(p.Velocity, Vector3.up * 0.25f, dt * 2.5f);
                p.T.position += p.Velocity * dt;
                p.T.localScale = Vector3.one * (p.Size * (1f + u * 5f));
                Color c = p.Color;
                c.a *= 1f - u;
                p.M.SetColor("_BaseColor", c);
            }
        }

        /// <summary>Removes smoke, shot marks and the flash (after the builder's test shots).</summary>
        public void ClearEffects()
        {
            if (_puffs != null)
            {
                foreach (Puff p in _puffs)
                {
                    if (p.T != null) Kill(p.T.gameObject);
                    if (p.M != null) Kill(p.M);
                }
            }

            if (_marks != null)
            {
                foreach (Transform m in _marks)
                {
                    if (m != null) Kill(m.gameObject);
                }
            }

            _puffs = null;
            _marks = null;
            _nextPuff = _nextMark = 0;
            _kick = _shoulder = 0f;
            if (flash != null) flash.SetActive(false);
        }

        private static void Kill(Object o)
        {
            if (Application.isPlaying) Destroy(o);
            else DestroyImmediate(o);
        }

        private void OnDestroy()
        {
            if (_puffs != null)
            {
                foreach (Puff p in _puffs)
                {
                    if (p.T != null) Kill(p.T.gameObject);
                    if (p.M != null) Kill(p.M);
                }
            }

            if (_marks != null)
            {
                foreach (Transform m in _marks)
                {
                    if (m != null) Kill(m.gameObject);
                }
            }
        }

        // ------------------------------------------------------------------ label, message, crosshair

        private void RefreshLabel()
        {
            if (_item == null) _item = GetComponent<PickupItem>();
            if (_item == null) return;
            _item.SetUseVerb(shells > 0 ? "Fire" : "Dry-fire");
            _item.SetDisplayName(shells > 0 ? $"Double-barrel ({shells} {(shells == 1 ? "shell" : "shells")})" : "Double-barrel (empty)");
        }

        private void Say(string text)
        {
            _message = text;
            _messageUntil = Time.time + 2.5f;
        }

        private void OnGUI()
        {
            CharacterItemHolder holder = Holder;
            if (holder == null || _rig == null || _rig.Target != holder.transform) return;
            if (_white == null)
            {
                _white = new Texture2D(1, 1);
                _white.SetPixel(0, 0, Color.white);
                _white.Apply();
            }

            float cx = Screen.width * 0.5f, cy = Screen.height * 0.5f, k = Screen.height / 720f;
            if (IsAiming && _aimBlend > 0.6f)
            {
                GUI.color = new Color(1f, 1f, 1f, 0.85f);
                float gap = 5f * k, len = 9f * k, th = Mathf.Max(1f, 2f * k);
                GUI.DrawTexture(new Rect(cx - gap - len, cy - th * 0.5f, len, th), _white);
                GUI.DrawTexture(new Rect(cx + gap, cy - th * 0.5f, len, th), _white);
                GUI.DrawTexture(new Rect(cx - th * 0.5f, cy - gap - len, th, len), _white);
                GUI.DrawTexture(new Rect(cx - th * 0.5f, cy + gap, th, len), _white);
                GUI.DrawTexture(new Rect(cx - th * 0.5f, cy - th * 0.5f, th, th), _white);
            }

            var style = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontSize = Mathf.RoundToInt(17f * k) };
            GUI.color = Palette.UiTextParchment;
            string hint = InFirstPerson(holder)
                ? (IsAiming ? $"Shells: {shells}   [U] / Click fire" : $"Shells: {shells}   Hold right mouse (or Z) to aim")
                : $"Shells: {shells}   [U] fire   [V] first person to aim";
            GUI.Label(new Rect(0f, Screen.height - 110f * k, Screen.width, 26f * k), hint, style);
            if (Time.time < _messageUntil)
            {
                style.fontSize = Mathf.RoundToInt(22f * k);
                GUI.Label(new Rect(0f, cy + 60f * k, Screen.width, 34f * k), _message, style);
            }

            GUI.color = Color.white;
        }

        // ------------------------------------------------------------------ sound

        private void Play(AudioClip clip, float volume)
        {
            if (_audio == null)
            {
                _audio = gameObject.AddComponent<AudioSource>();
                _audio.playOnAwake = false;
                _audio.spatialBlend = 0.5f;
                _audio.minDistance = 4f;
                _audio.maxDistance = 120f;
            }

            _audio.PlayOneShot(clip, volume);
        }

        /// <summary>A shotgun blast synthesised at runtime: a sharp crack, a low thump, and two echoes back off the fjord's sides.</summary>
        private static AudioClip BuildBoomClip()
        {
            int length = Mathf.CeilToInt(SampleRate * 2.2f);
            var dry = new float[Mathf.CeilToInt(SampleRate * 0.6f)];
            var rng = new System.Random(3);
            float lp = 0f;
            for (int i = 0; i < dry.Length; i++)
            {
                float t = i / (float)SampleRate;
                float noise = (float)rng.NextDouble() * 2f - 1f;
                lp += (noise - lp) * Mathf.Lerp(0.9f, 0.08f, Mathf.Clamp01(t * 6f));   // bright crack darkening into a rumble
                float thump = Mathf.Sin(2f * Mathf.PI * 52f * t) * Mathf.Exp(-t * 9f);
                dry[i] = (lp * Mathf.Exp(-t * 11f) * 1.1f + thump * 0.8f) * Mathf.Min(1f, t * 2000f);
            }

            var data = new float[length];
            foreach ((float delay, float gain) in new[] { (0f, 1f), (0.62f, 0.22f), (1.15f, 0.1f) })
            {
                int s0 = Mathf.RoundToInt(delay * SampleRate);
                for (int i = 0; i < dry.Length && s0 + i < length; i++) data[s0 + i] += dry[i] * gain;
            }

            for (int i = 0; i < length; i++) data[i] = Mathf.Clamp(data[i], -1f, 1f);
            AudioClip clip = AudioClip.Create("ShotgunBlast", length, 1, SampleRate, false);
            clip.SetData(data, 0);
            return clip;
        }

        private static AudioClip BuildClickClip()
        {
            int length = Mathf.CeilToInt(SampleRate * 0.12f);
            var data = new float[length];
            var rng = new System.Random(5);
            for (int i = 0; i < length; i++)
            {
                float t = i / (float)SampleRate;
                data[i] = (((float)rng.NextDouble() * 2f - 1f) * Mathf.Exp(-t * 500f) + Mathf.Sin(2f * Mathf.PI * 3100f * t) * Mathf.Exp(-t * 120f) * 0.4f) * 0.7f;
            }

            AudioClip clip = AudioClip.Create("ShotgunClick", length, 1, SampleRate, false);
            clip.SetData(data, 0);
            return clip;
        }
    }
}
