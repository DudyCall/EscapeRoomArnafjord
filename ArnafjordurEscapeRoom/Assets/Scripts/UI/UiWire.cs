using Arnafjordur.Core;
using UnityEngine;
using UnityEngine.UI;

namespace Arnafjordur.UI
{
    /// <summary>
    /// One insulated bomb wire drawn as a UI mesh: a slightly wavy cable from the top terminal to the bottom one,
    /// shaded like a round tube (dark edges, light stripe) with a soft drop shadow. When cut, the middle is
    /// snipped out and the two ends spring apart showing bare copper. The Graphic's own colour stays white so a
    /// Button can tint it on hover.
    /// </summary>
    public sealed class UiWire : MaskableGraphic
    {
        private const int Segments = 40;
        private const float CutFrom = 0.44f, CutTo = 0.56f;

        [SerializeField] private Color insulation = Color.red;
        [SerializeField] private float thickness = 10f;
        [SerializeField] private float sway = 7f;
        [SerializeField] private float phase;
        [SerializeField] private bool isCut;

        public void Configure(Color wireColor, float swayAmount, float swayPhase)
        {
            insulation = wireColor;
            sway = swayAmount;
            phase = swayPhase;
            SetVerticesDirty();
        }

        public void SetCut(bool cut)
        {
            if (isCut == cut) return;
            isCut = cut;
            SetVerticesDirty();
        }

        private Vector2 PointAt(float t, Rect r)
        {
            // ends pinned to the terminals; a gentle S-bend in between so the wires don't look ruler-straight
            float x = r.center.x + sway * Mathf.Sin(Mathf.PI * t) * Mathf.Sin(2f * Mathf.PI * t + phase);
            float y = Mathf.Lerp(r.yMax, r.yMin, t);
            if (isCut)
            {
                // the snipped ends spring apart: upper end lifts and kicks left, lower end drops and kicks right
                if (t <= CutFrom) { float k = t / CutFrom; x -= 4f * k * k; y += 5f * k * k; }
                else if (t >= CutTo) { float k = (1f - t) / (1f - CutTo); x += 4f * k * k; y -= 5f * k * k; }
            }

            return new Vector2(x, y);
        }

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
            Rect r = GetPixelAdjustedRect();
            Color dark = Color.Lerp(insulation, Color.black, 0.45f);
            Color light = Color.Lerp(insulation, Color.white, 0.45f);
            Color copper = Color.Lerp(Palette.LanternAmber, Palette.BoathouseRust, 0.3f);
            Color shadow = new Color(0f, 0f, 0f, 0.45f);

            if (!isCut)
            {
                Strip(vh, r, 0f, 1f, thickness * 1.15f, new Vector2(2.5f, -2.5f), shadow, 0f);
                Strip(vh, r, 0f, 1f, thickness, Vector2.zero, dark, 0f);
                Strip(vh, r, 0f, 1f, thickness * 0.62f, Vector2.zero, insulation, -0.08f);
                Strip(vh, r, 0f, 1f, thickness * 0.22f, Vector2.zero, light, -0.24f);
                return;
            }

            // two halves of insulation, each ending in a short bare copper core
            const float bare = 0.035f;
            foreach (var (a, b, coreA, coreB) in new[] { (0f, CutFrom - bare, CutFrom - bare, CutFrom), (CutTo + bare, 1f, CutTo, CutTo + bare) })
            {
                Strip(vh, r, a, b, thickness * 1.15f, new Vector2(2.5f, -2.5f), shadow, 0f);
                Strip(vh, r, coreA, coreB, thickness * 0.45f, Vector2.zero, Color.Lerp(copper, Color.black, 0.25f), 0f);
                Strip(vh, r, coreA, coreB, thickness * 0.18f, Vector2.zero, Color.Lerp(copper, Color.white, 0.4f), -0.1f);
                Strip(vh, r, a, b, thickness, Vector2.zero, dark, 0f);
                Strip(vh, r, a, b, thickness * 0.62f, Vector2.zero, insulation, -0.08f);
                Strip(vh, r, a, b, thickness * 0.22f, Vector2.zero, light, -0.24f);
            }
        }

        /// <summary>Adds a ribbon of <paramref name="width"/> along the wire between t0 and t1; <paramref name="side"/> shifts it across the tube (fraction of thickness, negative = left) for the shading bands.</summary>
        private void Strip(VertexHelper vh, Rect r, float t0, float t1, float width, Vector2 offset, Color c, float side)
        {
            int start = vh.currentVertCount;
            int steps = Mathf.Max(1, Mathf.CeilToInt(Segments * (t1 - t0)));
            Color32 col = c * color;
            for (int i = 0; i <= steps; i++)
            {
                float t = Mathf.Lerp(t0, t1, i / (float)steps);
                Vector2 p = PointAt(t, r);
                Vector2 tangent = (PointAt(Mathf.Min(1f, t + 0.01f), r) - PointAt(Mathf.Max(0f, t - 0.01f), r)).normalized;
                Vector2 normal = new Vector2(-tangent.y, tangent.x);
                Vector2 centre = p + offset + normal * (side * thickness);
                vh.AddVert(centre - normal * width * 0.5f, col, Vector2.zero);
                vh.AddVert(centre + normal * width * 0.5f, col, Vector2.zero);
                if (i > 0)
                {
                    int v = start + i * 2;
                    vh.AddTriangle(v - 2, v - 1, v);
                    vh.AddTriangle(v, v - 1, v + 1);
                }
            }
        }
    }
}
