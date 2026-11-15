using UnityEngine;

namespace Arnafjordur.Characters
{
    /// <summary>
    /// Flip-top cigarette pack: using it with U/Space opens the lid, reaches in with the other hand,
    /// pulls out a <see cref="Cigarette"/>, and places it in the character's mouth.
    /// </summary>
    public sealed class CigarettePack : MonoBehaviour, IUsableItem
    {
        [SerializeField] private Transform lidHinge;
        [SerializeField] private Cigarette cigaretteTemplate;

        public void Configure(Transform hinge, Cigarette template)
        {
            lidHinge = hinge;
            cigaretteTemplate = template;
            if (cigaretteTemplate != null)
            {
                cigaretteTemplate.gameObject.SetActive(false);
            }
            SetLidOpen(0f);
        }

        public void SetLidOpen(float normalized01)
        {
            if (lidHinge != null)
            {
                lidHinge.localRotation = Quaternion.Euler(108f * Mathf.Clamp01(normalized01), 0f, 0f);
            }
        }

        public Cigarette SpawnCigarette()
        {
            if (cigaretteTemplate == null) return null;
            Cigarette copy = Instantiate(cigaretteTemplate);
            copy.name = "Cigarette";
            copy.SetLit(false);
            copy.gameObject.SetActive(false);
            return copy;
        }

        public void OnUseItem(CharacterItemHolder user, PickupItem item)
        {
            if (user == null) return;
            if (!user.TryGetComponent(out CharacterSmoker smoker))
            {
                smoker = user.gameObject.AddComponent<CharacterSmoker>();
            }

            smoker.TryTakeCigarette(user, this);
        }

        private void OnDisable()
        {
            SetLidOpen(0f);
        }
    }
}
