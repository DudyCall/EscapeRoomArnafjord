using System.Collections.Generic;
using UnityEngine;

namespace Arnafjordur.Characters
{
    /// <summary>
    /// The beer box. A character standing right by it with an empty hand presses U/Space to take a bottle
    /// (see <see cref="PartyController.UseActiveItem"/>). Each bottle taken hides one of the bottle tops
    /// showing in the box; when they are all gone the box is empty.
    /// </summary>
    public sealed class BeerCrate : MonoBehaviour
    {
        private static readonly List<BeerCrate> Active = new List<BeerCrate>();

        [SerializeField, Tooltip("Inactive bottle copied for every beer taken.")] private PickupItem bottleTemplate;
        [SerializeField, Tooltip("Bottle tops visible in the box, hidden one by one.")] private GameObject[] bottles = new GameObject[0];
        [SerializeField, Tooltip("Planar metres from the box within which a character can reach in.")] private float reach = 1.3f;

        private int _taken;
        private PickupItem _item;

        public int BottlesLeft => Mathf.Max(0, bottles.Length - _taken);

        /// <summary>Wires the crate (used by the editor scene builder).</summary>
        public void Configure(PickupItem template, GameObject[] bottleTops)
        {
            bottleTemplate = template;
            bottles = bottleTops ?? new GameObject[0];
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetRegistry() => Active.Clear();

        private void Awake() => _item = GetComponent<PickupItem>();
        private void OnEnable() { if (!Active.Contains(this)) Active.Add(this); }
        private void OnDisable() => Active.Remove(this);

        /// <summary>The nearest crate with bottles left that <paramref name="position"/> can reach, or null.</summary>
        public static BeerCrate FindReachable(Vector3 position)
        {
            BeerCrate best = null;
            float bestDist = float.PositiveInfinity;
            foreach (BeerCrate crate in Active)
            {
                if (crate == null || !crate.CanTake) continue;
                Vector3 d = crate.transform.position - position;
                d.y = 0f;
                float dist = d.magnitude;
                if (dist <= crate.reach && dist < bestDist)
                {
                    best = crate;
                    bestDist = dist;
                }
            }

            return best;
        }

        /// <summary>
        /// Bottles left, a template to copy, and the box lying where it can be reached: not being carried,
        /// and not shut in the trunk (its pickup is switched off while the lid is closed).
        /// </summary>
        private bool CanTake => BottlesLeft > 0 && bottleTemplate != null && (_item == null || (_item.enabled && !_item.IsHeld));

        /// <summary>Hands a fresh bottle to <paramref name="holder"/>.</summary>
        public bool GiveBeer(CharacterItemHolder holder)
        {
            if (holder == null || holder.HasItem || !CanTake) return false;

            PickupItem beer = Instantiate(bottleTemplate, transform.position + Vector3.up * 0.2f, Quaternion.identity);
            beer.name = "Beer";
            beer.gameObject.SetActive(true);
            if (!holder.TryPickup(beer))
            {
                Destroy(beer.gameObject);
                return false;
            }

            if (_taken < bottles.Length && bottles[_taken] != null) bottles[_taken].SetActive(false);
            _taken++;
            return true;
        }
    }
}
