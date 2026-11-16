using UnityEngine;

namespace Arnafjordur.Characters
{
    /// <summary>
    /// A disposable lighter: U/Space while held flicks a small flame for a moment. <see cref="IsLit"/> is what a
    /// cigarette or nearby <see cref="Firewood"/> / <see cref="WoodBonfire"/> can ignite from.
    /// </summary>
    public sealed class Lighter : MonoBehaviour, IUsableItem
    {
        public const float DefaultIgnitionReach = 2.2f;

        [SerializeField] private GameObject flame;
        [SerializeField] private Light glow;
        [SerializeField, Min(0.2f)] private float flameSeconds = 2.5f;

        private float _litUntil;
        private CharacterItemHolder _activeUser;

        public bool IsLit => Time.time < _litUntil;

        public void Configure(GameObject flameObject, Light flameLight)
        {
            flame = flameObject;
            glow = flameLight;
            SetLit(false);
        }

        public void ExtendFlame(float minSeconds)
        {
            _litUntil = Mathf.Max(_litUntil, Time.time + minSeconds);
            SetLit(true);
        }

        public void OnUseItem(CharacterItemHolder user, PickupItem item)
        {
            _activeUser = user;
            _litUntil = Time.time + flameSeconds;
            SetLit(true);
            if (user != null && user.TryGetComponent(out CharacterSmoker smoker))
            {
                smoker.TryLightCigarette(user, this);
            }

            Vector3 origin = user != null ? user.transform.position : transform.position;
            TryIgniteNearbyWood(origin, DefaultIgnitionReach);
        }

        /// <summary>Ignites the nearest unlit <see cref="WoodBonfire"/> or <see cref="Firewood"/> within <paramref name="reach"/>.</summary>
        public static WoodBonfire TryIgniteNearbyWood(Vector3 position, float reach = DefaultIgnitionReach)
        {
            if (!WoodBonfire.TryFindIgnitionTarget(position, reach, out WoodBonfire unlitBonfire, out Firewood looseWood))
            {
                return null;
            }

            if (unlitBonfire != null)
            {
                unlitBonfire.Ignite();
                return unlitBonfire;
            }

            return WoodBonfire.IgniteSingle(looseWood);
        }

        private void Update()
        {
            bool lit = IsLit;
            if (flame != null && flame.activeSelf != lit) SetLit(lit);
            if (lit)
            {
                if (glow != null) glow.intensity = 0.35f + 0.08f * Mathf.PerlinNoise(Time.time * 12f, 0.3f);
                if (flame != null) flame.transform.localScale = new Vector3(1f, 1f + 0.15f * Mathf.Sin(Time.time * 31f), 1f);
                Vector3 origin = _activeUser != null ? _activeUser.transform.position : transform.position;
                TryIgniteNearbyWood(origin, 1.35f);
            }
        }

        private void OnDisable()
        {
            _litUntil = 0f;
            _activeUser = null;
            SetLit(false);
        }

        private void SetLit(bool lit)
        {
            if (flame != null) flame.SetActive(lit);
            if (glow != null) glow.enabled = lit;
        }
    }
}
