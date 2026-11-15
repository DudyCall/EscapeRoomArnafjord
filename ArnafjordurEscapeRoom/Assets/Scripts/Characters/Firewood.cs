using UnityEngine;

namespace Arnafjordur.Characters
{
    /// <summary>
    /// Marks a pickable piece of driftwood or plank as fuel, so a future fire can find what the players bring it.
    /// </summary>
    public sealed class Firewood : MonoBehaviour
    {
        [SerializeField, Min(0f), Tooltip("How long this piece keeps a fire going, in seconds.")]
        private float burnSeconds = 60f;

        public float BurnSeconds => burnSeconds;

        public void Configure(float seconds) => burnSeconds = Mathf.Max(0f, seconds);
    }
}
