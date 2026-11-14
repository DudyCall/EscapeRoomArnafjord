using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Arnafjordur.Editor
{
    /// <summary>
    /// The terrain mesh is coarser than the road, so here and there the hillside pokes up through the
    /// asphalt and shows as patches of turf and dirt lying on the road. This presses the terrain down to
    /// just under the driving surfaces along a route and saves the result as its own mesh asset
    /// (the FBX itself is left alone).
    /// </summary>
    public static class RoadTerrainFit
    {
        private const float Clearance = 0.06f;    // how far under the road the terrain must stay
        private const float MaxPoke = 1.0f;       // more than this above the road is a real hillside (tunnel, cutting), not a poke
        private const float SampleStep = 0.4f;

        /// <returns>how many terrain vertices were lowered</returns>
        public static int Apply(MeshFilter terrain, Collider[] surfaces, Vector3[] route, float routeRadius, string meshAssetPath)
        {
            Mesh source = terrain.sharedMesh;
            Transform tf = terrain.transform;
            Vector3[] local = source.vertices;
            int[] tris = source.triangles;

            var world = new Vector3[local.Length];
            var near = new bool[local.Length];
            float r2 = routeRadius * routeRadius;
            for (int i = 0; i < local.Length; i++)
            {
                world[i] = tf.TransformPoint(local[i]);
                foreach (Vector3 p in route)
                {
                    float dx = world[i].x - p.x, dz = world[i].z - p.z;
                    if (dx * dx + dz * dz < r2)
                    {
                        near[i] = true;
                        break;
                    }
                }
            }

            var candidates = new List<int>();
            for (int t = 0; t < tris.Length; t += 3)
            {
                if (near[tris[t]] || near[tris[t + 1]] || near[tris[t + 2]])
                {
                    candidates.Add(t);
                }
            }

            var lowered = new float[local.Length];
            var delta = new float[local.Length];
            for (int pass = 0; pass < 6; pass++)
            {
                System.Array.Clear(delta, 0, delta.Length);
                bool any = false;
                foreach (int t in candidates)
                {
                    int ia = tris[t], ib = tris[t + 1], ic = tris[t + 2];
                    Vector3 a = world[ia], b = world[ib], c = world[ic];
                    float longest = Mathf.Max((b - a).magnitude, (c - a).magnitude, (c - b).magnitude);
                    int n = Mathf.Clamp(Mathf.CeilToInt(longest / SampleStep), 1, 24);
                    float excess = 0f;
                    for (int i = 0; i <= n; i++)
                    {
                        for (int j = 0; j <= n - i; j++)
                        {
                            float u = i / (float)n, v = j / (float)n;
                            Vector3 p = a + (b - a) * u + (c - a) * v;
                            var ray = new Ray(new Vector3(p.x, p.y + Clearance, p.z), Vector3.down);
                            foreach (Collider surface in surfaces)
                            {
                                if (surface != null && surface.Raycast(ray, out RaycastHit hit, MaxPoke + Clearance))
                                {
                                    excess = Mathf.Max(excess, hit.distance);
                                }
                            }
                        }
                    }

                    if (excess > 0.002f)
                    {
                        any = true;
                        delta[ia] = Mathf.Max(delta[ia], excess);
                        delta[ib] = Mathf.Max(delta[ib], excess);
                        delta[ic] = Mathf.Max(delta[ic], excess);
                    }
                }

                if (!any)
                {
                    break;
                }

                for (int i = 0; i < world.Length; i++)
                {
                    world[i].y -= delta[i];
                    lowered[i] += delta[i];
                }
            }

            int count = 0;
            for (int i = 0; i < local.Length; i++)
            {
                if (lowered[i] > 0f)
                {
                    local[i] = tf.InverseTransformPoint(world[i]);
                    count++;
                }
            }

            Mesh fitted = Object.Instantiate(source);
            fitted.name = source.name + "_RoadFit";
            fitted.vertices = local;
            fitted.RecalculateBounds();
            AssetDatabase.DeleteAsset(meshAssetPath);
            AssetDatabase.CreateAsset(fitted, meshAssetPath);

            terrain.sharedMesh = fitted;
            if (terrain.TryGetComponent(out MeshCollider collider))
            {
                collider.sharedMesh = fitted;
            }

            return count;
        }
    }
}
