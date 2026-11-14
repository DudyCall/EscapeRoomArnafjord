using System;
using UnityEngine;

namespace Arnafjordur.Characters
{
    /// <summary>
    /// The ground the players are allowed on, seen from above: corridors (a line with a width, for roads and
    /// paths) and zones (a closed outline, for open ground such as the beach). Anything outside is off limits;
    /// <see cref="CharacterMotor"/> slides along the edge instead of leaving.
    /// </summary>
    public sealed class WalkableArea : MonoBehaviour
    {
        [Serializable]
        public struct Corridor
        {
            public string name;
            [Tooltip("Metres either side of the line the players may stand.")] public float halfWidth;
            public Vector3[] points;
        }

        [Serializable]
        public struct Zone
        {
            public string name;
            [Tooltip("Corners of the outline in order; it closes itself.")] public Vector3[] outline;
        }

        private const float EdgeInset = 0.002f;     // clamped points land just inside, so they test as inside next frame

        [SerializeField] private Corridor[] corridors = Array.Empty<Corridor>();
        [SerializeField] private Zone[] zones = Array.Empty<Zone>();

        public Corridor[] Corridors => corridors;
        public Zone[] Zones => zones;

        /// <summary>Replaces the whole area (used by the editor scene builder).</summary>
        public void Configure(Corridor[] newCorridors, Zone[] newZones)
        {
            corridors = newCorridors ?? Array.Empty<Corridor>();
            zones = newZones ?? Array.Empty<Zone>();
        }

        /// <summary>True when the point is on walkable ground. Height is ignored.</summary>
        public bool Contains(Vector3 position)
        {
            return Resolve(position, out _);
        }

        /// <summary>The point itself when it is walkable, otherwise the nearest walkable point. Height is kept.</summary>
        public Vector3 Clamp(Vector3 position)
        {
            return Resolve(position, out Vector3 nearest) ? position : nearest;
        }

        private bool Resolve(Vector3 position, out Vector3 nearest)
        {
            var p = new Vector2(position.x, position.z);
            float best = float.PositiveInfinity;
            Vector2 bestPoint = p;

            for (int c = 0; c < corridors.Length; c++)
            {
                Vector3[] pts = corridors[c].points;
                if (pts == null || pts.Length == 0) continue;
                float halfWidth = corridors[c].halfWidth;
                Vector2 onLine = NearestOnPolyline(pts, false, p, out float distance);
                if (distance <= halfWidth)
                {
                    nearest = position;
                    return true;
                }
                float outside = distance - halfWidth;
                if (outside < best)
                {
                    best = outside;
                    bestPoint = onLine + (p - onLine) * (Mathf.Max(0f, halfWidth - EdgeInset) / distance);
                }
            }

            for (int z = 0; z < zones.Length; z++)
            {
                Vector3[] outline = zones[z].outline;
                if (outline == null || outline.Length < 3) continue;
                if (InsideOutline(outline, p))
                {
                    nearest = position;
                    return true;
                }
                Vector2 onEdge = NearestOnPolyline(outline, true, p, out float distance);
                if (distance < best)
                {
                    best = distance;
                    // step a hair past the edge so the result is inside whichever way the outline winds
                    bestPoint = distance > 1e-5f ? onEdge + (onEdge - p) * (EdgeInset / distance) : onEdge;
                }
            }

            nearest = new Vector3(bestPoint.x, position.y, bestPoint.y);
            return false;
        }

        private static Vector2 NearestOnPolyline(Vector3[] pts, bool closed, Vector2 p, out float distance)
        {
            Vector2 bestPoint = new Vector2(pts[0].x, pts[0].z);
            float bestSqr = (p - bestPoint).sqrMagnitude;
            int segments = closed ? pts.Length : pts.Length - 1;
            for (int i = 0; i < segments; i++)
            {
                Vector3 a3 = pts[i], b3 = pts[(i + 1) % pts.Length];
                var a = new Vector2(a3.x, a3.z);
                Vector2 ab = new Vector2(b3.x, b3.z) - a;
                float len = ab.sqrMagnitude;
                float t = len < 1e-10f ? 0f : Mathf.Clamp01(Vector2.Dot(p - a, ab) / len);
                Vector2 q = a + ab * t;
                float sqr = (p - q).sqrMagnitude;
                if (sqr < bestSqr)
                {
                    bestSqr = sqr;
                    bestPoint = q;
                }
            }
            distance = Mathf.Sqrt(bestSqr);
            return bestPoint;
        }

        private static bool InsideOutline(Vector3[] outline, Vector2 p)
        {
            bool inside = false;
            for (int i = 0, j = outline.Length - 1; i < outline.Length; j = i++)
            {
                Vector3 a = outline[i], b = outline[j];
                if ((a.z > p.y) != (b.z > p.y) && p.x < (b.x - a.x) * (p.y - a.z) / (b.z - a.z) + a.x)
                    inside = !inside;
            }
            return inside;
        }

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(1f, 0.62f, 0.2f);
            foreach (Corridor corridor in corridors)
            {
                if (corridor.points == null) continue;
                for (int i = 0; i < corridor.points.Length; i++)
                {
                    Vector3 a = corridor.points[i] + Vector3.up * 0.15f;
                    if (i + 1 < corridor.points.Length) Gizmos.DrawLine(a, corridor.points[i + 1] + Vector3.up * 0.15f);
                    Vector3 forward = corridor.points[Mathf.Min(i + 1, corridor.points.Length - 1)]
                                      - corridor.points[Mathf.Max(i - 1, 0)];
                    Vector3 side = Vector3.Cross(Vector3.up, forward).normalized * corridor.halfWidth;
                    Gizmos.DrawLine(a - side, a + side);
                }
            }

            Gizmos.color = new Color(0.3f, 0.85f, 0.95f);
            foreach (Zone zone in zones)
            {
                if (zone.outline == null) continue;
                for (int i = 0; i < zone.outline.Length; i++)
                    Gizmos.DrawLine(zone.outline[i] + Vector3.up * 0.15f,
                        zone.outline[(i + 1) % zone.outline.Length] + Vector3.up * 0.15f);
            }
        }
    }
}
