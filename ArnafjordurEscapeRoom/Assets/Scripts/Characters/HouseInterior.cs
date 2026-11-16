using System.Collections.Generic;
using UnityEngine;

namespace Arnafjordur.Characters
{
    /// <summary>
    /// The rooms built inside a village house. The house's outer shell is one baked mesh with no way in, so people
    /// are moved through its doors (<see cref="HouseDoor"/>) and, while indoors, stop colliding with that shell: the
    /// interior's own walls hold them in. The rooms (and their lamps, which would otherwise glow through the outer
    /// walls) are only switched on while somebody is inside.
    /// </summary>
    public sealed class HouseInterior : MonoBehaviour
    {
        private static readonly List<HouseInterior> Interiors = new List<HouseInterior>();

        [SerializeField] private string houseName = "house";
        [SerializeField, Tooltip("Walls, furniture and lamps: active only while someone is inside.")]
        private GameObject contents;
        [SerializeField, Tooltip("The outside of the house (and its neighbours): ignored by people who are inside.")]
        private Collider[] shell = new Collider[0];

        private readonly HashSet<CharacterMotor> _inside = new HashSet<CharacterMotor>();

        public string HouseName => houseName;
        public bool AnyoneInside => _inside.Count > 0;

        /// <summary>Sets the interior up (used by the editor scene builder).</summary>
        public void Configure(string name, GameObject roomsRoot, Collider[] outerShell)
        {
            houseName = name;
            contents = roomsRoot;
            shell = outerShell ?? new Collider[0];
        }

        private void Awake()
        {
            if (contents != null) contents.SetActive(false);
        }

        private void OnEnable() => Interiors.Add(this);
        private void OnDisable() => Interiors.Remove(this);

        /// <summary>The interior the character is in, or null when outdoors.</summary>
        public static HouseInterior Containing(CharacterMotor character)
        {
            foreach (HouseInterior interior in Interiors)
            {
                if (interior._inside.Contains(character)) return interior;
            }

            return null;
        }

        public bool Contains(CharacterMotor character) => _inside.Contains(character);

        /// <summary>Marks the character as indoors or outdoors and updates collisions and the rooms' visibility.</summary>
        public void SetInside(CharacterMotor character, bool inside)
        {
            if (character == null) return;
            if (inside) _inside.Add(character);
            else _inside.Remove(character);

            if (character.TryGetComponent(out CharacterController controller))
            {
                foreach (Collider c in shell)
                {
                    if (c != null) Physics.IgnoreCollision(controller, c, inside);
                }
            }

            if (contents != null && contents.activeSelf != AnyoneInside)
            {
                contents.SetActive(AnyoneInside);
            }
        }
    }
}
