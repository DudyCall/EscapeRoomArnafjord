using System.Collections.Generic;
using UnityEngine;

namespace Arnafjordur.Characters
{
    /// <summary>
    /// A house door the party can knock on. Sits at the bottom centre of the door, its forward pointing to the side
    /// people stand on (out of the house for front doors, into the room for <see cref="IsInside"/> doors). A door with
    /// an <see cref="Interior"/> and an <see cref="OtherSide"/> can be gone through once someone has knocked.
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

        private AudioSource _audio;
        private static AudioClip _knockClip;

        public bool HasKnocked { get; private set; }
        public bool IsInside => isInside;
        public HouseInterior Interior => interior;
        public HouseDoor OtherSide => otherSide;
        public Vector3 StandPoint => standPoint;
        /// <summary>The way people face after arriving at this door: away from it.</summary>
        public float StandYaw => transform.eulerAngles.y;
        public bool CanGoThrough => interior != null && otherSide != null;
        public string KnockReply => knockReply;
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
            if (_audio == null)
            {
                _audio = gameObject.AddComponent<AudioSource>();
                _audio.playOnAwake = false;
                _audio.spatialBlend = 0.85f;
                _audio.minDistance = 2f;
                _audio.maxDistance = 25f;
            }

            if (_knockClip == null) _knockClip = BuildKnockClip();
            _audio.pitch = Random.Range(0.94f, 1.06f);
            _audio.PlayOneShot(_knockClip, 0.9f);
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
