using System;
using Arnafjordur.Intro;
using UnityEngine;
using UnityEngine.Rendering;

namespace Arnafjordur.Characters
{
    /// <summary>
    /// Lets a playable character hold one <see cref="PickupItem"/> in their right hand,
    /// keep it aligned with the fist each frame, drop it cleanly on the ground, and pick up items nearby.
    /// </summary>
    public sealed class CharacterItemHolder : MonoBehaviour
    {
        private static readonly float[] LateralDropOffsets = { 0f, 0.25f, -0.25f, 0.5f, -0.5f, 0.75f, -0.75f };

        [Header("Hand & Reach")]
        [SerializeField] private Transform upperArmBone;
        [SerializeField] private Transform lowerArmBone;
        [SerializeField] private Transform handBone;
        [SerializeField] private Transform headBone;
        [SerializeField] private Transform leftUpperArmBone;
        [SerializeField] private Transform leftLowerArmBone;
        [SerializeField] private Transform leftHandBone;
        [SerializeField] private Vector3 gripLocal;
        [SerializeField] private Vector3 mouthOffset = new Vector3(0f, -0.03f, 0.11f);
        [SerializeField] private PickupItem startingItem;
        [SerializeField, Min(0.4f)] private float pickupRange = 1.85f;
        [SerializeField, Min(0.2f)] private float dropForwardDistance = 0.55f;

        private bool _isDrinking;
        private bool _dryDrink;
        private float _sipElapsed;
        private float _currentSip;
        private bool _hasLastSolvedPose;
        private Quaternion _lastSolvedUpperRot;
        private Quaternion _lastSolvedLowerRot;
        private Vector3 _cachedAnimSwing;

        /// <summary>Fired whenever the held item changes (picked up or dropped).</summary>
        public event Action<PickupItem> HeldItemChanged;

        /// <summary>Fired whenever the held item is used.</summary>
        public event Action<PickupItem> ItemUsed;

        /// <summary>Item currently carried by this character, or null when empty-handed.</summary>
        public PickupItem HeldItem { get; private set; }

        /// <summary>True when this character is currently carrying an item.</summary>
        public bool HasItem => HeldItem != null;

        /// <summary>True while an item-use animation (such as taking a sip) is playing.</summary>
        public bool IsUsingItem => _isDrinking;

        /// <summary>Current normalized sip blend weight in [0, 1].</summary>
        public float CurrentSipWeight => _currentSip;

        /// <summary>Maximum planar reach in metres for picking up a world item.</summary>
        public float PickupRange => pickupRange;

        /// <summary>Hand bone transform used as the attachment parent.</summary>
        public Transform HandBone => handBone;

        /// <summary>Head bone transform used for mouth-targeted animations.</summary>
        public Transform HeadBone => headBone;

        /// <summary>Left upper arm bone transform.</summary>
        public Transform LeftUpperArmBone => leftUpperArmBone;

        /// <summary>Left lower arm bone transform.</summary>
        public Transform LeftLowerArmBone => leftLowerArmBone;

        /// <summary>Left hand bone transform.</summary>
        public Transform LeftHandBone => leftHandBone;

        /// <summary>World position of the character's mouth.</summary>
        public Vector3 GetMouthWorldPosition() =>
            headBone != null
                ? headBone.position + transform.right * mouthOffset.x + transform.up * mouthOffset.y + transform.forward * mouthOffset.z
                : transform.position + transform.up * 1.55f + transform.forward * 0.11f;

        /// <summary>Centre of the closed fist in <see cref="HandBone"/> local space.</summary>
        public Vector3 GripLocal => gripLocal;

        /// <summary>Assigns hand rig references and optionally attaches an initial held item.</summary>
        public void Configure(Transform hand, Vector3 fistLocal, PickupItem initialItem = null)
        {
            handBone = hand;
            gripLocal = fistLocal;
            startingItem = initialItem;
            ResolveRigBones();

            if (initialItem != null)
            {
                if (HeldItem == initialItem)
                {
                    PoseHeldItem();
                }
                else
                {
                    TryPickup(initialItem);
                }
            }
        }

        private void Awake()
        {
            if (TryGetComponent(out BeerDrinker drinker) && drinker.StartLowered)
            {
                if (handBone == null && drinker.Hand != null)
                {
                    handBone = drinker.Hand;
                    gripLocal = drinker.GripLocal;
                }

                if (startingItem == null && drinker.Beer != null)
                {
                    if (!drinker.Beer.TryGetComponent(out PickupItem beerItem))
                    {
                        beerItem = drinker.Beer.gameObject.AddComponent<PickupItem>();
                        beerItem.Configure("Beer", drinker.GripBelowCentre, 0.075f, true, "Drink", true);
                    }

                    startingItem = beerItem;
                }

                drinker.enabled = false;
            }

            ResolveRigBones();

            if (handBone != null && gripLocal == Vector3.zero)
            {
                gripLocal = ComputeFistCentre(transform, handBone, lowerArmBone);
            }

            if (startingItem != null && HeldItem == null)
            {
                TryPickup(startingItem);
            }
        }

        private void ResolveRigBones()
        {
            if (upperArmBone == null) upperArmBone = FindDeepChild(transform, "RightUpperArm");
            if (lowerArmBone == null) lowerArmBone = FindDeepChild(transform, "RightLowerArm");
            if (handBone == null) handBone = FindDeepChild(transform, "RightHand");
            if (headBone == null) headBone = FindDeepChild(transform, "Head");
            if (leftUpperArmBone == null) leftUpperArmBone = FindDeepChild(transform, "LeftUpperArm");
            if (leftLowerArmBone == null) leftLowerArmBone = FindDeepChild(transform, "LeftLowerArm");
            if (leftHandBone == null) leftHandBone = FindDeepChild(transform, "LeftHand");
        }

        private void LateUpdate()
        {
            if (_isDrinking)
            {
                if (HeldItem == null || !HeldItem.DrinkSipOnUse)
                {
                    _isDrinking = false;
                    _sipElapsed = 0f;
                    _currentSip = 0f;
                }
                else
                {
                    _sipElapsed += Time.deltaTime;
                    float raise = Mathf.Max(0.1f, HeldItem.SipRaiseSeconds);
                    float hold = Mathf.Max(0f, HeldItem.SipHoldSeconds);
                    float total = 2f * raise + hold;

                    if (_sipElapsed >= total)
                    {
                        _isDrinking = false;
                        _sipElapsed = 0f;
                        _currentSip = 0f;
                        HeldItem.SyncShadowWithHolder(this);
                        if (_dryDrink)
                        {
                            _dryDrink = false;
                            ThrowAwayHeldItem();
                        }
                        else
                        {
                            HeldItemChanged?.Invoke(HeldItem);
                        }
                    }
                    else if (_sipElapsed < raise)
                    {
                        _currentSip = Mathf.SmoothStep(0f, 1f, _sipElapsed / raise);
                    }
                    else if (_sipElapsed < raise + hold)
                    {
                        _currentSip = 1f;
                    }
                    else
                    {
                        _currentSip = 1f - Mathf.SmoothStep(0f, 1f, (_sipElapsed - raise - hold) / raise);
                    }
                }
            }

            PoseHeldItem();
            if (TryGetComponent(out CharacterSmoker smoker))
            {
                smoker.TickAndPose(this);
            }
        }

        /// <summary>Uses the currently held item (e.g., takes a swig when holding a drinkable item).</summary>
        public bool TryUseHeldItem()
        {
            if (HeldItem == null)
            {
                return false;
            }

            if (TryGetComponent(out CharacterDrunk drunkBefore) && drunkBefore.IsIncapacitated)
            {
                return false;
            }

            if (HeldItem.DrinkSipOnUse)
            {
                if (_isDrinking)
                {
                    return false;
                }

                // Count the sip; from an empty bottle the arm still goes up, comes up dry, and the bottle is tossed.
                if (!HeldItem.TryGetComponent(out BeerBottle bottle)) bottle = HeldItem.gameObject.AddComponent<BeerBottle>();
                _dryDrink = !bottle.TakeSip();
                if (!_dryDrink)
                {
                    if (!TryGetComponent(out CharacterDrunk drunk)) drunk = gameObject.AddComponent<CharacterDrunk>();
                    drunk.RecordSip(1f / bottle.MaxSips);
                }

                _isDrinking = true;
                _sipElapsed = 0f;
                _currentSip = 0f;
                HeldItem.SetShadowCastingMode(ShadowCastingMode.On);
            }

            HeldItem.NotifyUsed(this);
            ItemUsed?.Invoke(HeldItem);
            HeldItemChanged?.Invoke(HeldItem);
            return true;
        }

        /// <summary>Aligns the currently carried item with the character's fist and orientation.</summary>
        public void PoseHeldItem()
        {
            if (HeldItem == null || handBone == null)
            {
                return;
            }

            Vector3 up = transform.up;
            Vector3 forward = transform.forward;
            Vector3 right = transform.right;

            bool hasArmRig = upperArmBone != null && lowerArmBone != null && headBone != null;
            bool inspectingBomb = SeaMineBomb.Active != null && TryGetComponent(out CharacterMotor motor) && SeaMineBomb.Active.IsInspecting(motor);
            bool drunkOverrides = TryGetComponent(out CharacterDrunk drunk) && drunk.OverridesArmPose;
            bool overrideArms = inspectingBomb || drunkOverrides;
            if (HeldItem.TwoHanded && hasArmRig && !overrideArms && leftUpperArmBone != null && leftLowerArmBone != null && leftHandBone != null)
            {
                PoseTwoHanded(up, forward, right);
                return;
            }

            Vector3 baseAxis = HeldItem.KeepUpright ? up : handBone.up;
            if (drunkOverrides && drunk != null)
            {
                baseAxis = drunk.IsFallen
                    ? (forward + right * 0.35f + up * 0.15f).normalized
                    : (up + forward * 0.55f).normalized;
            }
            Vector3 axis = baseAxis;

            if (hasArmRig && !overrideArms && (HeldItem.KeepUpright || _currentSip > 0.001f))
            {
                float side = Mathf.Sign(Vector3.Dot(upperArmBone.position - headBone.position, right));
                if (side == 0f)
                {
                    side = 1f;
                }

                Vector3 sipPole = -up + right * (0.6f * side) + forward * 0.2f;
                Vector3 baseFist;
                Vector3 pole = sipPole;

                if (HeldItem.KeepUpright)
                {
                    RefreshCarryAnimSwing(side);
                    BeerDrinker.ComputeStandingCarryPose(
                        transform, upperArmBone, lowerArmBone, handBone, side, _cachedAnimSwing,
                        out baseFist, out pole, out baseAxis);
                }
                else
                {
                    baseFist = handBone.TransformPoint(gripLocal);
                }

                Vector3 fistTarget = baseFist;
                axis = baseAxis;

                if (_currentSip > 0.001f)
                {
                    Vector3 mouth = headBone.position + right * mouthOffset.x + up * mouthOffset.y + forward * mouthOffset.z;
                    Vector3 sipAxis = (-forward - up * 0.2f).normalized;
                    Vector3 sipFist = mouth - sipAxis * (HeldItem.SipTopHeight + HeldItem.GripBelowCentre);
                    float arc = HeldItem.KeepUpright ? 0.08f : 0.10f;
                    fistTarget = Vector3.Lerp(baseFist, sipFist, _currentSip) + forward * (arc * Mathf.Sin(Mathf.PI * _currentSip));
                    pole = Vector3.Slerp(pole, sipPole, _currentSip).normalized;
                    axis = Vector3.Slerp(baseAxis, sipAxis, _currentSip).normalized;
                }
                else if (TryGetComponent(out CharacterSmoker smoker)
                    && smoker.TryGetRightArmLighterPose(this, baseFist, pole, baseAxis,
                        out Vector3 lightFist, out Vector3 lightPole, out Vector3 lightAxis, out float lightWeight)
                    && lightWeight > 0.001f)
                {
                    fistTarget = lightFist;
                    pole = lightPole;
                    axis = lightAxis;
                }

                for (int pass = 0; pass < 3; pass++)
                {
                    BeerDrinker.SolveArm(
                        upperArmBone, lowerArmBone, handBone,
                        handBone.position + (fistTarget - handBone.TransformPoint(gripLocal)), pole);
                }

                if (HeldItem.KeepUpright)
                {
                    _lastSolvedUpperRot = upperArmBone.localRotation;
                    _lastSolvedLowerRot = lowerArmBone.localRotation;
                    _hasLastSolvedPose = true;
                }
            }

            Vector3 across = Vector3.ProjectOnPlane(right, axis);
            if (across.sqrMagnitude < 1e-6f)
            {
                across = Vector3.ProjectOnPlane(forward, axis);
            }

            if (across.sqrMagnitude < 1e-6f)
            {
                across = Vector3.forward;
            }

            Quaternion baseRot = Quaternion.LookRotation(across.normalized, axis);
            Quaternion rot = baseRot * Quaternion.Euler(HeldItem.HeldEulerOffset);
            Vector3 pos = handBone.TransformPoint(gripLocal) + axis * HeldItem.GripBelowCentre + baseRot * HeldItem.HeldLocalOffset;
            HeldItem.transform.SetPositionAndRotation(pos, rot);
        }

        /// <summary>
        /// Long items (wood, planks) are carried crosswise in front of the hips with both hands,
        /// forearms forward and elbows tucked in, the way you'd haul firewood.
        /// </summary>
        private void PoseTwoHanded(Vector3 up, Vector3 forward, Vector3 right)
        {
            const float gripHalfWidth = 0.19f;
            Vector3 centre = transform.position + up * 0.98f + forward * 0.34f;
            Vector3 rightFist = centre + right * gripHalfWidth;
            Vector3 leftFist = centre - right * gripHalfWidth;
            Vector3 rightPole = -up + right * 0.5f - forward * 0.3f;
            Vector3 leftPole = -up - right * 0.5f - forward * 0.3f;

            for (int pass = 0; pass < 3; pass++)
            {
                BeerDrinker.SolveArm(upperArmBone, lowerArmBone, handBone,
                    handBone.position + (rightFist - handBone.TransformPoint(gripLocal)), rightPole);
                // the left hand has no measured fist offset; the right one mirrored across the body is close enough
                Vector3 mirroredOffset = Vector3.Reflect(handBone.TransformPoint(gripLocal) - handBone.position, right);
                Vector3 leftFistNow = leftHandBone.position + mirroredOffset;
                BeerDrinker.SolveArm(leftUpperArmBone, leftLowerArmBone, leftHandBone,
                    leftHandBone.position + (leftFist - leftFistNow), leftPole);
            }

            // lay the item's longest local axis across the body, its thinnest facing up
            Vector3 s = HeldItem.transform.localScale;
            Quaternion rot;
            if (s.y >= s.x && s.y >= s.z) rot = Quaternion.LookRotation(forward, right);   // log: +Y runs left-right
            else if (s.z >= s.x) rot = Quaternion.LookRotation(right, up);                 // plank: +Z runs left-right, flat
            else rot = Quaternion.LookRotation(forward, up);                                // +X already runs left-right
            HeldItem.transform.SetPositionAndRotation(centre + up * 0.03f, rot);
        }

        private void RefreshCarryAnimSwing(float side)
        {
            if (!Application.isPlaying)
            {
                _cachedAnimSwing = Vector3.zero;
                return;
            }

            if (!_hasLastSolvedPose
                || upperArmBone.localRotation != _lastSolvedUpperRot
                || lowerArmBone.localRotation != _lastSolvedLowerRot)
            {
                _cachedAnimSwing = BeerDrinker.MeasureCarryAnimSwing(
                    transform, upperArmBone, lowerArmBone, handBone, gripLocal, side);
            }
        }

        /// <summary>Picks up <paramref name="item"/> into this character's hand, dropping any currently held item first.</summary>
        public bool TryPickup(PickupItem item)
        {
            if (item == null || item.IsHeld || handBone == null)
            {
                return false;
            }

            if (HasItem)
            {
                DropHeldItem();
            }

            _isDrinking = false;
            _sipElapsed = 0f;
            _currentSip = 0f;
            _hasLastSolvedPose = false;
            _cachedAnimSwing = Vector3.zero;

            item.transform.SetParent(handBone, true);
            item.OnPickedUp(this);
            HeldItem = item;
            PoseHeldItem();
            HeldItemChanged?.Invoke(HeldItem);
            return true;
        }

        /// <summary>Lets go of the empty bottle and lobs it away in front of the character.</summary>
        private void ThrowAwayHeldItem()
        {
            PickupItem item = HeldItem;
            if (item == null) return;

            Vector3 handPos = item.transform.position;
            Quaternion handRot = item.transform.rotation;
            DropHeldItem();
            item.transform.SetPositionAndRotation(handPos, handRot);
            if (!item.TryGetComponent(out BeerBottle bottle)) bottle = item.gameObject.AddComponent<BeerBottle>();
            bottle.ThrowFrom(transform);
        }

        /// <summary>Drops the currently held item onto the ground in front of the character.</summary>
        /// <summary>Lets go of the held item without putting it on the ground (e.g. back into a pocket).</summary>
        public PickupItem ReleaseHeldItem()
        {
            if (HeldItem == null) return null;
            _isDrinking = false;
            _sipElapsed = 0f;
            _currentSip = 0f;
            _hasLastSolvedPose = false;
            _cachedAnimSwing = Vector3.zero;
            PickupItem item = HeldItem;
            item.OnDropped(transform);
            HeldItem = null;
            HeldItemChanged?.Invoke(null);
            return item;
        }

        public PickupItem DropHeldItem()
        {
            if (HeldItem == null)
            {
                return null;
            }

            _isDrinking = false;
            _sipElapsed = 0f;
            _currentSip = 0f;
            _hasLastSolvedPose = false;
            _cachedAnimSwing = Vector3.zero;

            PickupItem item = HeldItem;
            Vector3 dropXZ = FindDropSpotXZ(item);
            float groundY = SampleGroundY(dropXZ);

            Quaternion dropRot = Quaternion.Euler(0f, transform.eulerAngles.y, 0f);
            item.transform.rotation = dropRot;
            float groundOffset = item.GetGroundOffset();
            Vector3 finalPos = new Vector3(dropXZ.x, groundY + groundOffset, dropXZ.z);

            item.OnDropped(GetOrCreateWorldItemsRoot());
            item.transform.SetPositionAndRotation(finalPos, dropRot);

            HeldItem = null;
            HeldItemChanged?.Invoke(null);
            return item;
        }

        private Vector3 FindDropSpotXZ(PickupItem droppingItem)
        {
            const float MinItemSeparationSqr = 0.18f * 0.18f;
            Vector3 fallback = ClampDropPosition(transform.position + transform.forward * dropForwardDistance);

            for (int i = 0; i < LateralDropOffsets.Length; i++)
            {
                Vector3 candidate = ClampDropPosition(
                    transform.position + transform.forward * dropForwardDistance + transform.right * LateralDropOffsets[i]);

                bool clear = true;
                var items = PickupItem.All;
                for (int j = 0; j < items.Count; j++)
                {
                    PickupItem other = items[j];
                    if (other == null || other == droppingItem || other.IsHeld)
                    {
                        continue;
                    }

                    float dx = other.transform.position.x - candidate.x;
                    float dz = other.transform.position.z - candidate.z;
                    if (dx * dx + dz * dz < MinItemSeparationSqr)
                    {
                        clear = false;
                        break;
                    }
                }

                if (clear)
                {
                    return candidate;
                }
            }

            return fallback;
        }

        private Vector3 ClampDropPosition(Vector3 worldPos)
        {
            if (TryGetComponent(out CharacterMotor motor))
            {
                if (motor.Area != null)
                {
                    return motor.Area.Clamp(worldPos);
                }

                worldPos.x = Mathf.Clamp(worldPos.x, -motor.ArenaHalfExtent, motor.ArenaHalfExtent);
                worldPos.z = Mathf.Clamp(worldPos.z, -motor.ArenaHalfExtent, motor.ArenaHalfExtent);
            }

            return worldPos;
        }

        private float SampleGroundY(Vector3 worldXZ)
        {
            Vector3 origin = new Vector3(worldXZ.x, transform.position.y + 1.5f, worldXZ.z);
            RaycastHit[] hits = Physics.RaycastAll(origin, Vector3.down, 12f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            if (hits != null && hits.Length > 0)
            {
                Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
                for (int i = 0; i < hits.Length; i++)
                {
                    Collider col = hits[i].collider;
                    if (col == null)
                    {
                        continue;
                    }

                    if (col.gameObject.layer == 2 || col.gameObject.name == "CarObstacle")
                    {
                        continue;
                    }

                    if (col.GetComponentInParent<CharacterMotor>() != null || col.GetComponentInParent<PickupItem>() != null)
                    {
                        continue;
                    }

                    return hits[i].point.y;
                }
            }

            return transform.position.y;
        }

        private static Transform GetOrCreateWorldItemsRoot()
        {
            GameObject root = GameObject.Find("_WorldItems");
            if (root == null)
            {
                root = new GameObject("_WorldItems");
            }

            return root.transform;
        }

        private static Transform FindDeepChild(Transform parent, string targetName)
        {
            for (int i = 0; i < parent.childCount; i++)
            {
                Transform child = parent.GetChild(i);
                if (string.Equals(child.name, targetName, StringComparison.Ordinal))
                {
                    return child;
                }

                Transform found = FindDeepChild(child, targetName);
                if (found != null)
                {
                    return found;
                }
            }

            return null;
        }

        /// <summary>
        /// Measures the centre of the hand's own skin in <paramref name="hand"/> local space.
        /// Falls back to a forward wrist offset if the mesh is unavailable.
        /// </summary>
        public static Vector3 ComputeFistCentre(Transform characterRoot, Transform hand, Transform lowerArm)
        {
            if (characterRoot == null || hand == null)
            {
                return Vector3.zero;
            }

            foreach (SkinnedMeshRenderer smr in characterRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                Transform[] bones = smr.bones;
                int handIndex = Array.IndexOf(bones, hand);
                if (handIndex < 0 || smr.sharedMesh == null || (!Application.isEditor && !smr.sharedMesh.isReadable))
                {
                    continue;
                }

                bool[] inHand = new bool[bones.Length];
                for (int b = 0; b < bones.Length; b++)
                {
                    inHand[b] = bones[b] != null && (bones[b] == hand || bones[b].IsChildOf(hand));
                }

                Mesh mesh = smr.sharedMesh;
                Vector3[] verts = mesh.vertices;
                BoneWeight[] weights = mesh.boneWeights;
                Matrix4x4 toHand = mesh.bindposes[handIndex];
                Vector3 sum = Vector3.zero;
                int count = 0;
                for (int v = 0; v < weights.Length; v++)
                {
                    BoneWeight w = weights[v];
                    float h = (inHand[w.boneIndex0] ? w.weight0 : 0f) + (inHand[w.boneIndex1] ? w.weight1 : 0f) +
                              (inHand[w.boneIndex2] ? w.weight2 : 0f) + (inHand[w.boneIndex3] ? w.weight3 : 0f);
                    if (h < 0.6f)
                    {
                        continue;
                    }

                    sum += toHand.MultiplyPoint3x4(verts[v]);
                    count++;
                }

                if (count > 0)
                {
                    return sum / count;
                }
            }

            if (lowerArm != null)
            {
                return hand.InverseTransformPoint(hand.position + (hand.position - lowerArm.position).normalized * 0.08f);
            }

            return new Vector3(0f, 0.08f, 0f);
        }
    }
}
