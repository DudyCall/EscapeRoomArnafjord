using UnityEngine;

namespace Arnafjordur.Characters
{
    /// <summary>
    /// Marks a <see cref="PickupItem"/> as a key the players can try in the sea mine's Module 4 keyholes.
    /// Any of the three right keys turns in any slot; a key with <see cref="FitsBomb"/> off goes in but won't turn.
    /// </summary>
    public sealed class BombKey : MonoBehaviour
    {
        [SerializeField, Tooltip("True for the three keys that open the sea mine; false for decoy keys that won't turn.")]
        private bool fitsBomb = true;

        public bool FitsBomb => fitsBomb;

        public void Configure(bool fits) => fitsBomb = fits;
    }
}
