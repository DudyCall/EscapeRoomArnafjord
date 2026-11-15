using UnityEngine;

namespace Arnafjordur.Characters
{
    /// <summary>
    /// A portable radio the party can carry. Using it (U/Space while held) starts the next song, or stops it.
    /// Songs go in <see cref="songs"/>; with none set it just clicks and does nothing yet.
    /// Plays through a 3D AudioSource on the radio, so it is heard wherever it is carried or put down.
    /// </summary>
    [RequireComponent(typeof(AudioSource))]
    public sealed class RadioPlayer : MonoBehaviour, IUsableItem
    {
        [SerializeField] private AudioClip[] songs = new AudioClip[0];
        [SerializeField, Range(0f, 1f)] private float volume = 0.8f;

        private AudioSource _source;
        private int _next;
        private bool _on;

        public bool IsPlaying => _on;

        private void Awake()
        {
            _source = GetComponent<AudioSource>();
            _source.playOnAwake = false;
            _source.loop = false;
            _source.spatialBlend = 1f;
            _source.minDistance = 2f;
            _source.maxDistance = 30f;
            _source.rolloffMode = AudioRolloffMode.Linear;
            _source.volume = volume;
        }

        /// <summary>Sets the playlist (used by the editor scene builder, from Assets/Audio/Radio/).</summary>
        public void Configure(AudioClip[] playlist)
        {
            songs = playlist ?? new AudioClip[0];
        }

        public void OnUseItem(CharacterItemHolder user, PickupItem item) => Toggle();

        public void Toggle()
        {
            if (_on) Stop();
            else PlayNext();
        }

        public void Stop()
        {
            _on = false;
            _source.Stop();
        }

        private void PlayNext()
        {
            if (songs == null || songs.Length == 0)
            {
                Debug.Log("[RadioPlayer] no songs yet - add AudioClips to RadioPlayer.songs", this);
                return;
            }

            _on = true;
            _source.clip = songs[_next % songs.Length];
            _next = (_next + 1) % songs.Length;
            _source.Play();
        }

        private void Update()
        {
            // Carry on to the next song when one ends (not when the player paused it).
            if (_on && !_source.isPlaying && Application.isFocused) PlayNext();
        }
    }
}
