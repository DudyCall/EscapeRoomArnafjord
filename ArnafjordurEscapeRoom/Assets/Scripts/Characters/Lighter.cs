using UnityEngine;

namespace Arnafjordur.Characters
{
    /// <summary>
    /// A disposable lighter: U/Space while held flicks a small flame for a moment. <see cref="IsLit"/> is what a
    /// fire (or a cigarette) can check later.
    /// </summary>
    public sealed class Lighter : MonoBehaviour, IUsableItem
    {
        [SerializeField] private GameObject flame;
        [SerializeField] private Light glow;
        [SerializeField, Min(0.2f)] private float flameSeconds = 2.5f;

        private float _litUntil;

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
            _litUntil = Time.time + flameSeconds;
            SetLit(true);
            if (user != null && user.TryGetComponent(out CharacterSmoker smoker))
            {
                smoker.TryLightCigarette(user, this);
            }
        }

        private void Update()
        {
            bool lit = IsLit;
            if (flame != null && flame.activeSelf != lit) SetLit(lit);
            if (lit && glow != null) glow.intensity = 0.35f + 0.08f * Mathf.PerlinNoise(Time.time * 12f, 0.3f);
            if (lit && flame != null) flame.transform.localScale = new Vector3(1f, 1f + 0.15f * Mathf.Sin(Time.time * 31f), 1f);
        }

        private void OnDisable()
        {
            _litUntil = 0f;
            SetLit(false);
        }

        private void SetLit(bool lit)
        {
            if (flame != null) flame.SetActive(lit);
            if (glow != null) glow.enabled = lit;
        }
    }
}
