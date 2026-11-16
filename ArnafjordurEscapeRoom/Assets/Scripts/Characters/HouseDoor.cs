using System;
using System.Collections.Generic;
using UnityEngine;

namespace Arnafjordur.Characters
{
    /// <summary>
    /// A house door the party can knock on. Sits at the bottom centre of the door, its forward pointing to the side
    /// people stand on (out of the house for front doors, into the room for <see cref="IsInside"/> doors). A door with
    /// an <see cref="Interior"/> and an <see cref="OtherSide"/> can be gone through once someone has knocked, unless it
    /// is locked: then someone has to stand at it holding the right <see cref="HouseKey"/> and use it (U) first.
    /// <see cref="Arnafjordur.UI.HouseDoorUi"/> runs the prompt, input and fade.
    /// </summary>
    public sealed class HouseDoor : MonoBehaviour
    {
        /// <summary>Every enabled door, so the UI can find the nearest one without scene searches.</summary>
        public static readonly List<HouseDoor> All = new List<HouseDoor>();

        private const int SampleRate = 22050;

        [SerializeField, Tooltip("How close (m, ground plane) the active character must be to the door.")]
        private float reach = 2f;
        [SerializeField, Tooltip("World spot where people arrive when they come through this door.")]
        private Vector3 standPoint;
        [SerializeField] private HouseInterior interior;
        [SerializeField] private HouseDoor otherSide;
        [SerializeField] private bool isInside;
        [SerializeField, TextArea] private string knockReply = "Nobody answers.";
        [SerializeField, Tooltip("Empty for a door without a lock; otherwise the HouseKey lock id that opens it.")]
        private string lockId = "";
        [SerializeField] private bool locked;

        /// <summary>Things to tell the player about locks and keys (the door UI shows them).</summary>
        public static event Action<string> Notice;

        private AudioSource _audio;
        private static AudioClip _knockClip, _unlockClip;

        public bool HasKnocked { get; private set; }
        public bool IsInside => isInside;
        public HouseInterior Interior => interior;
        public HouseDoor OtherSide => otherSide;
        public Vector3 StandPoint => standPoint;
        /// <summary>The way people face after arriving at this door: away from it.</summary>
        public float StandYaw => transform.eulerAngles.y;
        public bool CanGoThrough => interior != null && otherSide != null;
        public string KnockReply => locked ? $"{knockReply} The door is locked." : knockReply;
        public bool IsLocked => locked;
        public string LockId => lockId;
        public float Reach => reach;

        /// <summary>Sets the door up (used by the editor scene builder).</summary>
        public void Configure(float reachMetres, Vector3 arrivePoint, string reply, HouseInterior leadsInto, HouseDoor partner, bool inside)
        {
            reach = reachMetres;
            standPoint = arrivePoint;
            knockReply = reply;
            interior = leadsInto;
            otherSide = partner;
            isInside = inside;
        }

        /// <summary>Gives the door a lock (used by the editor scene builder).</summary>
        public void SetLock(string id, bool isLocked)
        {
            lockId = id ?? "";
            locked = isLocked && lockId.Length > 0;
        }

        public static void Announce(string text) => Notice?.Invoke(text);

        /// <summary>Turns the lock with a key whose id is <paramref name="keyId"/>; false when it doesn't fit (or nothing is locked).</summary>
        public bool TryUnlock(string keyId)
        {
            if (!locked || keyId != lockId) return false;
            locked = false;
            HasKnocked = true;   // straight in from now on
            if (otherSide != null)
            {
                otherSide.locked = false;
                otherSide.HasKnocked = true;
            }

            if (Application.isPlaying)
            {
                if (_unlockClip == null) _unlockClip = BuildUnlockClip();
                PlayOneShot(_unlockClip, 1f);
            }

            Announce("*click* The key turns. The door is open.");
            return true;
        }

        /// <summary>The nearest locked door the character stands at, or null.</summary>
        public static HouseDoor LockedDoorInReach(CharacterMotor character)
        {
            HouseDoor best = null;
            float bestDistance = float.PositiveInfinity;
            foreach (HouseDoor door in All)
            {
                if (door.locked && door.InReach(character, out float d) && d < bestDistance)
                {
                    best = door;
                    bestDistance = d;
                }
            }

            return best;
        }

        public static bool AnyDoorInReach(CharacterMotor character)
        {
            foreach (HouseDoor door in All)
            {
                if (door.InReach(character, out _)) return true;
            }

            return false;
        }

        private void OnEnable() => All.Add(this);
        private void OnDisable() => All.Remove(this);

        /// <summary>
        /// True when the character stands close in front of the door. Standing behind it (on the other side of the
        /// wall) or on a different floor doesn't count.
        /// </summary>
        public bool InReach(CharacterMotor character, out float distance)
        {
            distance = float.PositiveInfinity;
            if (character == null) return false;
            Vector3 d = character.transform.position - transform.position;
            if (Mathf.Abs(d.y) > 1.4f) return false;
            d.y = 0f;
            distance = d.magnitude;
            return distance <= reach && Vector3.Dot(d, transform.forward) > 0.05f;
        }

        /// <summary>Three knocks on the door. Remembers it, so the way in opens for doors that lead somewhere.</summary>
        public void Knock()
        {
            HasKnocked = true;
            if (otherSide != null) otherSide.HasKnocked = true;
            if (_knockClip == null) _knockClip = BuildKnockClip();
            PlayOneShot(_knockClip, 0.9f);
        }

        private void PlayOneShot(AudioClip clip, float volume)
        {
            if (_audio == null)
            {
                _audio = gameObject.AddComponent<AudioSource>();
                _audio.playOnAwake = false;
                _audio.spatialBlend = 0.85f;
                _audio.minDistance = 2f;
                _audio.maxDistance = 25f;
            }

            _audio.pitch = UnityEngine.Random.Range(0.94f, 1.06f);
            _audio.PlayOneShot(clip, volume);
        }

        /// <summary>An old lock turning: a scrape, then two sharp metallic clicks as the bolt goes back.</summary>
        private static AudioClip BuildUnlockClip()
        {
            int length = Mathf.CeilToInt(SampleRate * 0.55f);
            var data = new float[length];
            var rng = new System.Random(11);
            for (int i = 0; i < SampleRate * 0.22f; i++)
            {
                float t = i / (float)SampleRate;
                float env = Mathf.Sin(Mathf.PI * t / 0.22f);
                data[i] += ((float)rng.NextDouble() * 2f - 1f) * 0.12f * env * (0.6f + 0.4f * Mathf.Sin(2f * Mathf.PI * 31f * t));
            }

            foreach ((float start, float pitch) in new[] { (0.24f, 2350f), (0.33f, 1650f) })
            {
                int s0 = Mathf.RoundToInt(start * SampleRate);
                for (int i = 0; i < SampleRate * 0.12f && s0 + i < length; i++)
                {
                    float t = i / (float)SampleRate;
                    float ring = Mathf.Sin(2f * Mathf.PI * pitch * t) * Mathf.Exp(-t * 60f) + 0.5f * Mathf.Sin(2f * Mathf.PI * pitch * 1.47f * t) * Mathf.Exp(-t * 90f);
                    float tick = ((float)rng.NextDouble() * 2f - 1f) * Mathf.Exp(-t * 400f);
                    data[s0 + i] += (ring * 0.45f + tick * 0.6f);
                }
            }

            AudioClip clip = AudioClip.Create("DoorUnlock", length, 1, SampleRate, false);
            clip.SetData(data, 0);
            return clip;
        }

        /// <summary>
        /// Synthesises a knuckles-on-wood knock (three dull thuds) at runtime, so no audio asset is needed and it
        /// works the same in WebGL.
        /// </summary>
        private static AudioClip BuildKnockClip()
        {
            float[] hits = { 0f, 0.21f, 0.40f };
            int length = Mathf.CeilToInt(SampleRate * 0.62f);
            var data = new float[length];
            var rng = new System.Random(7);
            foreach (float start in hits)
            {
                int s0 = Mathf.RoundToInt(start * SampleRate);
                for (int i = 0; i < SampleRate * 0.16f && s0 + i < length; i++)
                {
                    float t = i / (float)SampleRate;
                    float body = Mathf.Sin(2f * Mathf.PI * 118f * t) * Mathf.Exp(-t * 38f)
                                 + 0.55f * Mathf.Sin(2f * Mathf.PI * 236f * t + 0.6f) * Mathf.Exp(-t * 55f);
                    float click = ((float)rng.NextDouble() * 2f - 1f) * Mathf.Exp(-t * 260f) * 0.7f;
                    data[s0 + i] += (body + click) * 0.6f;
                }
            }

            AudioClip clip = AudioClip.Create("DoorKnock", length, 1, SampleRate, false);
            clip.SetData(data, 0);
            return clip;
        }

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(1f, 0.75f, 0.3f);
            Gizmos.DrawWireSphere(transform.position + Vector3.up * 0.05f, reach);
            Gizmos.color = Color.cyan;
            Gizmos.DrawSphere(standPoint, 0.12f);
        }
    }
}
