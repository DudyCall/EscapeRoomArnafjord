using UnityEngine;

namespace Arnafjordur.Intro
{
    /// <summary>
    /// The song on the car radio during the intro. It starts part-way into the track, keeps playing across the
    /// intro scene changes, and is cut with <see cref="Stop"/> when the car is switched off.
    /// Every intro scene carries one; only the first to wake up plays, so any scene can be started on its own.
    /// </summary>
    public sealed class IntroMusic : MonoBehaviour
    {
        private static IntroMusic _instance;

        [SerializeField] private AudioClip clip;
        [SerializeField, Min(0f), Tooltip("Seconds into the song where playback starts.")] private float startAtSeconds = 62f;
        [SerializeField, Range(0f, 1f), Tooltip("Kept low so dialogue can sit on top.")] private float volume = 0.22f;
        [SerializeField, Min(0f)] private float fadeInSeconds = 1.5f;

        private AudioSource _source;
        private float _gain;
        private float _fadeOutSeconds = -1f;

        /// <summary>Wires the track (used by the editor scene setup).</summary>
        public void Configure(AudioClip song, float startSeconds, float playVolume)
        {
            clip = song;
            startAtSeconds = Mathf.Max(0f, startSeconds);
            volume = Mathf.Clamp01(playVolume);
        }

        /// <summary>Fades the music out and removes it. Does nothing if no intro music is playing.</summary>
        public static void Stop(float fadeSeconds = 0.25f)
        {
            if (_instance != null && _instance._fadeOutSeconds < 0f) _instance._fadeOutSeconds = Mathf.Max(0.01f, fadeSeconds);
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            _instance = null;
        }

        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);            // the music from an earlier intro scene is already playing
                return;
            }
            _instance = this;
            transform.SetParent(null);
            DontDestroyOnLoad(gameObject);

            _source = gameObject.AddComponent<AudioSource>();
            _source.clip = clip;
            _source.playOnAwake = false;
            _source.loop = true;
            _source.spatialBlend = 0f;
            _source.volume = 0f;
            if (clip == null) return;
            _source.Play();
            _source.time = Mathf.Clamp(startAtSeconds, 0f, Mathf.Max(0f, clip.length - 1f));
        }

        private void Update()
        {
            if (_source == null) return;
            float dt = Time.unscaledDeltaTime;
            if (_fadeOutSeconds >= 0f)
            {
                _gain = Mathf.MoveTowards(_gain, 0f, dt / _fadeOutSeconds);
                if (_gain <= 0f)
                {
                    Destroy(gameObject);
                    return;
                }
            }
            else
            {
                _gain = fadeInSeconds > 0f ? Mathf.MoveTowards(_gain, 1f, dt / fadeInSeconds) : 1f;
            }
            _source.volume = volume * _gain;
        }

        private void OnDestroy()
        {
            if (_instance == this) _instance = null;
        }
    }
}
