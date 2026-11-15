using UnityEngine;

namespace Arnafjordur.Characters
{
    /// <summary>
    /// How much is left in a drinkable item. <see cref="CharacterItemHolder"/> adds it on the first sip and
    /// counts sips down; a sip from an empty bottle comes up dry, and the character then tosses the bottle away.
    /// </summary>
    public sealed class BeerBottle : MonoBehaviour
    {
        [SerializeField, Min(1)] private int sips = 5;
        [SerializeField, Min(0f), Tooltip("Seconds a thrown-away bottle lies on the ground before it is cleared.")]
        private float litterSeconds = 30f;

        private int _left = -1;
        private bool _thrown;
        private Vector3 _from, _to, _spinAxis;
        private float _flightTime, _t;

        public int MaxSips => Mathf.Max(1, sips);
        public int SipsLeft => _left < 0 ? sips : _left;
        public bool IsEmpty => SipsLeft <= 0;
        public bool IsThrown => _thrown;

        /// <summary>Drinks one sip; false when the bottle was already empty.</summary>
        public bool TakeSip()
        {
            if (IsEmpty) return false;
            _left = SipsLeft - 1;
            return true;
        }

        /// <summary>Flies the (already released) bottle from where it is to a spot ahead of <paramref name="thrower"/>.</summary>
        public void ThrowFrom(Transform thrower)
        {
            _thrown = true;
            if (TryGetComponent(out PickupItem item))
            {
                item.SetCollidersEnabled(false);
                item.enabled = false;           // litter, not something to pick up again
            }

            _from = transform.position;
            Vector3 fwd = thrower.forward;
            fwd.y = 0f;
            fwd = fwd.sqrMagnitude > 0.001f ? fwd.normalized : Vector3.forward;
            Vector3 side = Vector3.Cross(Vector3.up, fwd);
            Vector3 target = thrower.position + fwd * 3.2f + side * Random.Range(-0.6f, 0.6f);
            _to = new Vector3(target.x, GroundY(target, thrower.position.y) + 0.035f, target.z);
            _flightTime = 0.75f;
            _t = 0f;
            _spinAxis = side;
            Destroy(gameObject, _flightTime + litterSeconds);
        }

        private void Update()
        {
            if (!_thrown || _t >= _flightTime) return;

            _t = Mathf.Min(_flightTime, _t + Time.deltaTime);
            float u = _t / _flightTime;
            Vector3 p = Vector3.Lerp(_from, _to, u);
            p.y += Mathf.Sin(u * Mathf.PI) * 1.1f;     // a lob, not a fastball
            transform.position = p;
            if (u < 1f) transform.Rotate(_spinAxis, 900f * Time.deltaTime, Space.World);
            else transform.rotation = Quaternion.FromToRotation(Vector3.up, Vector3.Cross(_spinAxis, Vector3.up)); // lies on its side
        }

        private static float GroundY(Vector3 at, float fallback)
        {
            RaycastHit[] hits = Physics.RaycastAll(at + Vector3.up * 4f, Vector3.down, 12f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            float best = float.NegativeInfinity;
            foreach (RaycastHit hit in hits)
            {
                if (hit.collider.GetComponentInParent<CharacterMotor>() != null) continue;
                if (hit.point.y > best) best = hit.point.y;
            }

            return float.IsNegativeInfinity(best) ? fallback : best;
        }
    }
}
