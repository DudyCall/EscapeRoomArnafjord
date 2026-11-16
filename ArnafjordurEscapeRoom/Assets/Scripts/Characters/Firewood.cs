using System.Collections.Generic;
using UnityEngine;

namespace Arnafjordur.Characters
{
    /// <summary>
    /// Marks a pickable piece of driftwood or plank as fuel (5 minutes per piece by default) that can be
    /// ignited with a <see cref="Lighter"/> or stacked with other wood into a <see cref="WoodBonfire"/>.
    /// </summary>
    public sealed class Firewood : MonoBehaviour, IUsableItem
    {
        public const float DefaultBurnSeconds = 300f;

        private static readonly List<Firewood> ActiveWood = new List<Firewood>();
        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");

        [SerializeField, Min(0f), Tooltip("How long this piece keeps a fire going, in seconds.")]
        private float burnSeconds = DefaultBurnSeconds;

        private Renderer _renderer;
        private PickupItem _pickup;
        private Color _originalBaseColor = Color.white;
        private bool _hasOriginalColor;
        private MaterialPropertyBlock _propBlock;
        private Vector3 _originalScale;
        private bool _hasOriginalScale;

        /// <summary>All currently enabled <see cref="Firewood"/> pieces in the scene.</summary>
        public static IReadOnlyList<Firewood> All => ActiveWood;

        /// <summary>Seconds of burn time this wood contributes.</summary>
        public float BurnSeconds => burnSeconds;

        /// <summary>The bonfire stack this wood currently belongs to, or null when loose/held.</summary>
        public WoodBonfire Stack { get; internal set; }

        /// <summary>Associated <see cref="PickupItem"/> component, if present.</summary>
        public PickupItem Pickup => _pickup != null ? _pickup : (_pickup = GetComponent<PickupItem>());

        /// <summary>True while carried by a character.</summary>
        public bool IsHeld => Pickup != null && Pickup.IsHeld;

        /// <summary>True when this piece is part of an actively burning fire.</summary>
        public bool IsLit => Stack != null && Stack.IsLit;

        /// <summary>True once this piece has completely burned away.</summary>
        public bool IsConsumed { get; internal set; }

        /// <summary>True for cylindrical logs whose length runs along local +Y (2 m primitive cylinder).</summary>
        public bool IsLog
        {
            get
            {
                Vector3 s = BaseScale;
                return s.y >= s.x && s.y >= s.z;
            }
        }

        /// <summary>Approximate length of this piece in metres.</summary>
        public float Length
        {
            get
            {
                Vector3 s = BaseScale;
                return IsLog ? s.y * 2f : Mathf.Max(s.x, s.z);
            }
        }

        /// <summary>Half-thickness in metres when resting flat on the ground.</summary>
        public float HalfThickness
        {
            get
            {
                Vector3 s = BaseScale;
                return IsLog ? Mathf.Max(s.x, s.z) * 0.5f : s.y * 0.5f;
            }
        }

        internal Vector3 BaseScale
        {
            get
            {
                if (!_hasOriginalScale)
                {
                    _originalScale = transform.localScale;
                    _hasOriginalScale = true;
                }

                return _originalScale;
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetRegistry() => ActiveWood.Clear();

        public void Configure(float seconds) => burnSeconds = Mathf.Max(0f, seconds);

        private void Awake()
        {
            _pickup = GetComponent<PickupItem>();
            _renderer = GetComponentInChildren<Renderer>(true);
            if (!_hasOriginalScale)
            {
                _originalScale = transform.localScale;
                _hasOriginalScale = true;
            }

            if (_renderer != null && _renderer.sharedMaterial != null && _renderer.sharedMaterial.HasProperty(BaseColorId))
            {
                _originalBaseColor = _renderer.sharedMaterial.GetColor(BaseColorId);
                _hasOriginalColor = true;
            }
        }

        private void OnEnable()
        {
            if (!ActiveWood.Contains(this))
            {
                ActiveWood.Add(this);
            }

            if (Pickup != null)
            {
                Pickup.PickedUp -= HandlePickedUp;
                Pickup.PickedUp += HandlePickedUp;
            }
        }

        private void OnDisable()
        {
            ActiveWood.Remove(this);
            if (Pickup != null)
            {
                Pickup.PickedUp -= HandlePickedUp;
            }
        }

        public void OnUseItem(CharacterItemHolder user, PickupItem item)
        {
            if (user == null || IsConsumed) return;

            Vector3 dropOrigin = user.transform.position + user.transform.forward * 0.65f;
            if (WoodBonfire.TryFindStackTarget(dropOrigin, user.transform.position, this, 1.85f, 2.3f,
                out WoodBonfire bonfire, out Firewood targetWood))
            {
                user.StackHeldWoodOnto(bonfire, targetWood);
            }
        }

        /// <summary>Rotation that lays this piece flat on the ground, crosswise to <paramref name="characterYaw"/>.</summary>
        public Quaternion GetFlatDropRotation(float characterYaw)
        {
            Vector3 across = Quaternion.Euler(0f, characterYaw, 0f) * Vector3.right;
            return ComputeOrientation(across, Vector3.up);
        }

        /// <summary>Computes a world rotation aligning the piece's long axis with <paramref name="lengthDir"/>.</summary>
        public Quaternion ComputeOrientation(Vector3 lengthDir, Vector3 upHint)
        {
            lengthDir = lengthDir.sqrMagnitude > 1e-6f ? lengthDir.normalized : Vector3.forward;
            upHint = upHint.sqrMagnitude > 1e-6f ? upHint.normalized : Vector3.up;

            Vector3 s = BaseScale;
            if (IsLog)
            {
                Vector3 fwd = Vector3.ProjectOnPlane(upHint, lengthDir);
                if (fwd.sqrMagnitude < 1e-5f) fwd = Vector3.ProjectOnPlane(Vector3.forward, lengthDir);
                if (fwd.sqrMagnitude < 1e-5f) fwd = Vector3.ProjectOnPlane(Vector3.right, lengthDir);
                return Quaternion.LookRotation(fwd.normalized, lengthDir);
            }

            if (s.z >= s.x)
            {
                return Quaternion.LookRotation(lengthDir, upHint);
            }

            Vector3 sideFwd = Vector3.Cross(lengthDir, upHint);
            if (sideFwd.sqrMagnitude < 1e-5f) sideFwd = Vector3.Cross(lengthDir, Vector3.forward);
            return Quaternion.LookRotation(sideFwd.normalized, upHint);
        }

        internal void SetCharProgress(float charFraction)
        {
            if (_renderer == null) return;
            charFraction = Mathf.Clamp01(charFraction);

            if (charFraction <= 0.001f)
            {
                _renderer.SetPropertyBlock(null);
                transform.localScale = BaseScale;
                return;
            }

            if (_propBlock == null) _propBlock = new MaterialPropertyBlock();
            Color baseCol = _hasOriginalColor ? _originalBaseColor : new Color(0.54f, 0.42f, 0.31f, 1f);
            Color charCol = new Color(0.11f, 0.09f, 0.08f, 1f);
            _propBlock.SetColor(BaseColorId, Color.Lerp(baseCol, charCol, charFraction));
            _renderer.SetPropertyBlock(_propBlock);

            float shrink = Mathf.Lerp(1f, 0.72f, Mathf.InverseLerp(0.35f, 1f, charFraction));
            transform.localScale = BaseScale * shrink;
        }

        private void HandlePickedUp(CharacterItemHolder _)
        {
            if (Stack != null)
            {
                WoodBonfire stack = Stack;
                stack.RemoveWood(this);
            }

            SetCharProgress(0f);
        }
    }
}
