using UnityEngine;

namespace Arnafjordur.Characters
{
    /// <summary>
    /// A key for a locked <see cref="HouseDoor"/> (Leivur carries the boathouse key). Using it (U / Space) while standing
    /// at the door it fits turns the lock and opens the way in; at any other door, or away from doors, it says so.
    /// </summary>
    public sealed class HouseKey : MonoBehaviour, IUsableItem
    {
        private static readonly Quaternion HeldTagRotation = Quaternion.Euler(-106f, 0f, 0f);

        [SerializeField, Tooltip("Matches the lock id of the door this key opens.")]
        private string lockId = "boathouse";
        [SerializeField, Tooltip("Optional dangling tag pivot rotated down while held and flat when dropped.")]
        private Transform tagPivot;

        private PickupItem _item;

        public string LockId => lockId;

        /// <summary>Sets the key up (used by the editor scene builder).</summary>
        public void Configure(string id, Transform tagPivotTransform = null)
        {
            lockId = id;
            tagPivot = tagPivotTransform;
            SyncTagPose();
        }

        private void Awake()
        {
            _item = GetComponent<PickupItem>();
            SyncTagPose();
        }

        private void OnEnable()
        {
            SyncTagPose();
        }

        private void LateUpdate()
        {
            SyncTagPose();
        }

        /// <summary>Hangs the wooden tag down when held in hand and flattens it when resting in the world.</summary>
        public void SyncTagPose()
        {
            if (tagPivot == null) return;
            if (_item == null) _item = GetComponent<PickupItem>();
            bool held = _item != null && _item.IsHeld;
            tagPivot.localRotation = held ? HeldTagRotation : Quaternion.identity;
        }

        public void OnUseItem(CharacterItemHolder user, PickupItem item)
        {
            CharacterMotor who = user != null ? user.GetComponent<CharacterMotor>() : null;
            HouseDoor door = HouseDoor.LockedDoorInReach(who);
            if (door == null)
            {
                HouseDoor.Announce(HouseDoor.AnyDoorInReach(who) ? "That door isn't locked." : "There's no lock here for this key.");
                return;
            }

            if (!door.TryUnlock(lockId)) HouseDoor.Announce("The key goes in, but it won't turn. Wrong lock.");
        }
    }
}
