using System.Collections.Generic;
using UnityEngine;

namespace Arnafjordur.Characters
{
    /// <summary>
    /// Personal things a character carries on them (Andrias: cigarettes and a lighter). They stay hidden in the
    /// pocket until taken out; I cycles pocket → hand → next item → empty hand. A pocket item that is dropped
    /// becomes a normal world item, and goes back in this pocket if its owner picks it up and puts it away.
    /// </summary>
    public sealed class CharacterPockets : MonoBehaviour
    {
        [SerializeField] private List<PickupItem> items = new List<PickupItem>();

        public IReadOnlyList<PickupItem> Items => items;

        /// <summary>Builder hook: stores <paramref name="item"/> hidden in this pocket.</summary>
        public void Add(PickupItem item)
        {
            if (item == null) return;
            if (!items.Contains(item)) items.Add(item);
            Stow(item);
        }

        /// <summary>Puts the held pocket item away and takes out the next one; false when nothing changed.</summary>
        public bool Cycle(CharacterItemHolder holder)
        {
            if (holder == null || items.Count == 0) return false;

            int start = 0;
            if (holder.HasItem)
            {
                int heldIndex = items.IndexOf(holder.HeldItem);
                if (heldIndex < 0) return false;       // hands full with something that isn't ours
                PickupItem held = holder.ReleaseHeldItem();
                Stow(held);
                start = heldIndex + 1;
            }

            for (int i = start; i < items.Count; i++)
            {
                PickupItem next = items[i];
                if (next == null || !IsStowed(next)) continue;   // lying somewhere in the world
                next.gameObject.SetActive(true);
                return holder.TryPickup(next) || true;
            }

            return start > 0;   // the last item went back: hand is now empty
        }

        private bool IsStowed(PickupItem item) => !item.gameObject.activeSelf && item.transform.parent == transform;

        private void Stow(PickupItem item)
        {
            item.transform.SetParent(transform, false);
            item.transform.localPosition = new Vector3(0.12f, 0.95f, 0f);   // roughly the trouser pocket
            item.gameObject.SetActive(false);
        }
    }
}
