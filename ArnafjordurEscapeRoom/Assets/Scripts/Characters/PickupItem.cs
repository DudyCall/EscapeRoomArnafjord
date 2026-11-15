using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.Rendering;

namespace Arnafjordur.Characters
{
    /// <summary>
    /// Optional component interface for items that run custom logic when used by a character.
    /// </summary>
    public interface IUsableItem
    {
        void OnUseItem(CharacterItemHolder user, PickupItem item);
    }

    /// <summary>
    /// Marks a world object as pickable and droppable by a <see cref="CharacterItemHolder"/>.
    /// Keeps its interaction collider as a trigger so character locomotion and ground raycasts ignore it.
    /// </summary>
    public sealed class PickupItem : MonoBehaviour
    {
        private static readonly List<PickupItem> ActiveItems = new List<PickupItem>();

        [SerializeField] private string displayName = "Item";
        [SerializeField, Tooltip("Metres the fist sits below the item's origin when held.")]
        private float gripBelowCentre = 0.02f;
        [SerializeField, Tooltip("Optional local Euler offset applied while carried in the hand.")]
        private Vector3 heldEulerOffset = Vector3.zero;
        [SerializeField, Tooltip("Optional offset in the carried hand frame (X=forward, Y=up, Z=right) applied while held.")]
        private Vector3 heldLocalOffset = Vector3.zero;
        [SerializeField, Tooltip("Keep the item's +Y axis aligned with the character's up axis while carried.")]
        private bool keepUpright = true;
        [SerializeField, Tooltip("Carried across the body in both hands (wood, planks) instead of in the right fist.")]
        private bool twoHanded;
        [SerializeField, Tooltip("Height of the item's origin above the ground when dropped. Negative auto-measures from renderers.")]
        private float groundClearance = -1f;
        [SerializeField] private float interactionRadius = 0.16f;
        [SerializeField] private float interactionHeight = 0.36f;

        [Header("Use")]
        [SerializeField] private string useVerb = "Use";
        [SerializeField, Tooltip("When true, using this item plays a drink/sip arm animation to the character's mouth.")]
        private bool drinkSipOnUse;
        [SerializeField, Tooltip("Metres from the item's origin to its mouth/top when drinking.")]
        private float sipTopHeight = 0.16f;
        [SerializeField, Min(0.1f)] private float sipRaiseSeconds = 0.5f;
        [SerializeField, Min(0f)] private float sipHoldSeconds = 1.0f;
        [SerializeField] private UnityEvent<CharacterItemHolder> onUsed = new UnityEvent<CharacterItemHolder>();

        /// <summary>All currently enabled <see cref="PickupItem"/> instances in the loaded scene.</summary>
        public static IReadOnlyList<PickupItem> All => ActiveItems;

        /// <summary>Human-readable item name shown in the HUD.</summary>
        public string DisplayName => string.IsNullOrWhiteSpace(displayName) ? gameObject.name : displayName;

        /// <summary>Metres the fist sits below the item's origin when held.</summary>
        public float GripBelowCentre => gripBelowCentre;

        /// <summary>Optional local Euler offset applied while carried in the hand.</summary>
        public Vector3 HeldEulerOffset => heldEulerOffset;

        /// <summary>Optional offset in the carried hand frame (X=forward, Y=up, Z=right) applied while held.</summary>
        public Vector3 HeldLocalOffset => heldLocalOffset;

        /// <summary>Builder hook for fine-tuning how an item sits and faces in the hand.</summary>
        public void SetHeldPose(Vector3 localOffset, Vector3 eulerOffset)
        {
            heldLocalOffset = localOffset;
            heldEulerOffset = eulerOffset;
        }

        /// <summary>True when the item stays upright relative to the character while carried.</summary>
        public bool KeepUpright => keepUpright;

        /// <summary>True for long, heavy things held crosswise in both hands.</summary>
        public bool TwoHanded => twoHanded;

        /// <summary>Builder hook for <see cref="TwoHanded"/>.</summary>
        public void SetTwoHanded(bool value) => twoHanded = value;

        /// <summary>Character currently holding this item, or null when resting in the world.</summary>
        public CharacterItemHolder Holder { get; private set; }

        /// <summary>True while this item is carried by a character.</summary>
        public bool IsHeld => Holder != null;

        /// <summary>Action verb shown in the HUD button when holding this item (e.g., "Drink", "Use").</summary>
        public string UseVerb => string.IsNullOrWhiteSpace(useVerb) ? "Use" : useVerb;

        /// <summary>True when using this item raises it to the character's mouth for a sip.</summary>
        public bool DrinkSipOnUse => drinkSipOnUse;

        /// <summary>Metres from the item's origin to its mouth/top when drinking.</summary>
        public float SipTopHeight => sipTopHeight;

        /// <summary>Seconds to raise or lower the item when sipping.</summary>
        public float SipRaiseSeconds => sipRaiseSeconds;

        /// <summary>Seconds to hold the item at the mouth while sipping.</summary>
        public float SipHoldSeconds => sipHoldSeconds;

        /// <summary>Invoked whenever a character uses this item.</summary>
        public UnityEvent<CharacterItemHolder> OnUsed => onUsed;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetRegistry()
        {
            ActiveItems.Clear();
        }

        private void Awake()
        {
            EnsureInteractionCollider();
        }

        private void OnEnable()
        {
            if (!ActiveItems.Contains(this))
            {
                ActiveItems.Add(this);
            }
        }

        private void OnDisable()
        {
            ActiveItems.Remove(this);
        }

        /// <summary>Configures item metadata and ensures a trigger interaction collider exists.</summary>
        public void Configure(string itemName, float fistBelowCentre = 0.02f, float groundOffset = 0.075f, bool upright = true,
            string itemUseVerb = "Use", bool sipOnUse = false)
        {
            displayName = itemName;
            gripBelowCentre = fistBelowCentre;
            groundClearance = groundOffset;
            keepUpright = upright;
            useVerb = itemUseVerb;
            drinkSipOnUse = sipOnUse;
            EnsureInteractionCollider();
        }

        /// <summary>Updates the action verb shown in the HUD while this item is held.</summary>
        public void SetUseVerb(string itemUseVerb)
        {
            useVerb = string.IsNullOrWhiteSpace(itemUseVerb) ? "Use" : itemUseVerb;
        }

        /// <summary>Ensures a trigger collider exists for pointer selection without blocking movement or ground checks.</summary>
        public void EnsureInteractionCollider()
        {
            Collider[] existing = GetComponentsInChildren<Collider>(true);
            if (existing.Length == 0)
            {
                CapsuleCollider capsule = gameObject.AddComponent<CapsuleCollider>();
                capsule.isTrigger = true;
                capsule.radius = interactionRadius;
                capsule.height = interactionHeight;
                capsule.center = new Vector3(0f, 0.04f, 0f);
                capsule.enabled = !IsHeld;
                return;
            }

            for (int i = 0; i < existing.Length; i++)
            {
                if (existing[i] != null)
                {
                    existing[i].isTrigger = true;
                }
            }
        }

        /// <summary>Enables or disables interaction colliders on this item.</summary>
        public void SetCollidersEnabled(bool enabled)
        {
            EnsureInteractionCollider();
            foreach (Collider col in GetComponentsInChildren<Collider>(true))
            {
                if (col != null)
                {
                    col.isTrigger = true;
                    col.enabled = enabled;
                }
            }
        }

        /// <summary>Vertical distance from this transform's origin down to its resting base.</summary>
        public float GetGroundOffset()
        {
            if (groundClearance >= 0f)
            {
                return groundClearance;
            }

            Renderer[] renderers = GetComponentsInChildren<Renderer>(true);
            if (renderers.Length == 0)
            {
                return 0.075f;
            }

            float minY = float.PositiveInfinity;
            for (int i = 0; i < renderers.Length; i++)
            {
                if (renderers[i] != null)
                {
                    minY = Mathf.Min(minY, renderers[i].bounds.min.y);
                }
            }

            if (float.IsPositiveInfinity(minY))
            {
                return 0.075f;
            }

            return Mathf.Max(0.01f, transform.position.y - minY);
        }

        internal void NotifyUsed(CharacterItemHolder user)
        {
            onUsed?.Invoke(user);
            foreach (IUsableItem usable in GetComponents<IUsableItem>())
            {
                usable?.OnUseItem(user, this);
            }
        }

        internal void OnPickedUp(CharacterItemHolder newHolder)
        {
            Holder = newHolder;
            SetCollidersEnabled(false);
            SyncShadowWithHolder(newHolder);
        }

        internal void OnDropped(Transform worldParent)
        {
            Holder = null;
            transform.SetParent(worldParent, true);
            SetCollidersEnabled(true);
            SetShadowCastingMode(ShadowCastingMode.On);
        }

        internal void SyncShadowWithHolder(CharacterItemHolder holder)
        {
            ShadowCastingMode mode = ShadowCastingMode.On;
            if (holder != null)
            {
                SkinnedMeshRenderer holderMesh = holder.GetComponentInChildren<SkinnedMeshRenderer>(true);
                if (holderMesh != null)
                {
                    mode = holderMesh.shadowCastingMode;
                }
            }

            SetShadowCastingMode(mode);
        }

        internal void SetShadowCastingMode(ShadowCastingMode mode)
        {
            foreach (Renderer r in GetComponentsInChildren<Renderer>(true))
            {
                if (r != null)
                {
                    r.shadowCastingMode = mode;
                }
            }
        }
    }
}
