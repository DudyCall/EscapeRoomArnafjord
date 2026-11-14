using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Arnafjordur.Editor
{
    /// <summary>
    /// The Stórá as one unbroken stream from the mountain to the fjord.
    /// The river in the FBX is a flat ribbon laid just over its centre line, and the terrain mesh is too
    /// coarse to hold a clean channel for it, so it keeps diving under the ground and coming back up.
    /// This rebuilds it in the scene: the water level at every step is taken from the highest ground
    /// in the channel there, and is never allowed to rise on the way down (it pools behind bumps instead
    /// of vanishing under them), and the stream stops where it meets the sea.
    /// </summary>
    public static class RiverFit
    {
        private const string CentreLinePath = "Assets/Environment/TunnelExit/StoraRiver.json";
        private const string RiverMaterialPath = "Assets/Environment/TunnelExit/Materials/Mat_Exit_RiverWater.mat";
        private const string RiverMaterialName = "Mat_Exit_RiverWater";
        private const string MeshDir = "Assets/Environment/TunnelExit";

        private const float Step = 1.5f;          // metres between cross-sections
        private const float HalfWidth = 2.1f;
        private const float ChannelProbe = 1.1f;  // the level is set by the ground within this of the centre line
        private const float Depth = 0.14f;        // water over that ground
        private const float Skirt = 1.6f;         // the edges drop this far, so no gap shows on sloping ground

        [System.Serializable]
        private class CentreLine
        {
            public Vector3[] points;
        }

        /// <param name="world">the instantiated TunnelExit.fbx root</param>
        /// <param name="assetTag">keeps each scene's generated meshes apart</param>
        public static void Apply(GameObject world, float seaLevel, string assetTag)
        {
            MeshFilter terrainFilter = world.GetComponentsInChildren<MeshFilter>().FirstOrDefault(mf => mf.name == "Exit_Terrain");
            MeshFilter waterFilter = world.GetComponentsInChildren<MeshFilter>().FirstOrDefault(mf => mf.name == "Exit_Water");
            Collider terrain = terrainFilter != null ? terrainFilter.GetComponent<Collider>() : null;
            if (terrain == null || waterFilter == null || !File.Exists(CentreLinePath))
            {
                Debug.LogWarning("[RiverFit] terrain collider, water or river centre line missing - river left as it was.");
                return;
            }

            Physics.SyncTransforms();

            // ---- centre line, evenly resampled and lightly smoothed
            Vector3[] raw = JsonUtility.FromJson<CentreLine>(File.ReadAllText(CentreLinePath)).points;
            var line = new List<Vector2>();
            float carry = 0f;
            for (int i = 0; i < raw.Length - 1; i++)
            {
                var a = new Vector2(raw[i].x, raw[i].z);
                var b = new Vector2(raw[i + 1].x, raw[i + 1].z);
                float len = Vector2.Distance(a, b);
                float d = carry;
                for (; d < len; d += Step)
                {
                    line.Add(Vector2.Lerp(a, b, d / len));
                }

                carry = d - len;
            }

            for (int pass = 0; pass < 3; pass++)
            {
                for (int i = 1; i < line.Count - 1; i++)
                {
                    line[i] = (line[i - 1] + line[i] * 2f + line[i + 1]) * 0.25f;
                }
            }

            float Ground(Vector2 p)
            {
                return terrain.Raycast(new Ray(new Vector3(p.x, 600f, p.y), Vector3.down), out RaycastHit hit, 1400f)
                    ? hit.point.y
                    : float.NegativeInfinity;
            }

            int n = line.Count;
            var side = new Vector2[n];
            var level = new float[n];
            var bed = new float[n];
            for (int i = 0; i < n; i++)
            {
                Vector2 dir = (line[Mathf.Min(i + 1, n - 1)] - line[Mathf.Max(i - 1, 0)]).normalized;
                side[i] = new Vector2(dir.y, -dir.x);
                float top = float.NegativeInfinity;
                for (int k = -2; k <= 2; k++)
                {
                    top = Mathf.Max(top, Ground(line[i] + side[i] * (k * 0.5f * ChannelProbe)));
                }

                bed[i] = top;
                level[i] = top + Depth;
            }

            // make sure the line runs downhill, mountain first
            if (bed[0] < bed[n - 1])
            {
                line.Reverse();
                System.Array.Reverse(side);
                System.Array.Reverse(level);
                System.Array.Reverse(bed);
                for (int i = 0; i < n; i++)
                {
                    side[i] = -side[i];
                }
            }

            // the stream ends where it reaches the fjord
            int last = n - 1;
            for (int i = 0; i < n; i++)
            {
                if (float.IsNegativeInfinity(bed[i]) || bed[i] < seaLevel - 0.05f)
                {
                    last = i;
                    break;
                }
            }

            int first = 0;
            while (first < last && float.IsNegativeInfinity(bed[first]))
            {
                first++;
            }

            level[last] = seaLevel - 0.03f; // tucked just under the sea so the two join without a step

            // water never runs uphill: walking up from the sea, each step is at least as high as the one below
            for (int i = last - 1; i >= first; i--)
            {
                level[i] = Mathf.Max(level[i], level[i + 1]);
            }

            // ---- ribbon with dropped edges
            int count = last - first + 1;
            if (count < 2)
            {
                Debug.LogWarning("[RiverFit] river centre line never reaches the terrain - river left as it was.");
                return;
            }

            var verts = new Vector3[count * 4];
            var uvs = new Vector2[count * 4];
            var tris = new List<int>();
            float run = 0f;
            for (int c = 0; c < count; c++)
            {
                int i = first + c;
                if (c > 0)
                {
                    run += Vector2.Distance(line[i], line[i - 1]);
                }

                // narrows to nothing over the last few metres at the source
                float w = HalfWidth * Mathf.Clamp01(0.25f + c * Step / 12f);
                Vector2 l = line[i] - side[i] * w, r = line[i] + side[i] * w;
                Vector2 lo = line[i] - side[i] * (w + 0.25f), ro = line[i] + side[i] * (w + 0.25f);
                verts[c * 4 + 0] = new Vector3(lo.x, level[i] - Skirt, lo.y);
                verts[c * 4 + 1] = new Vector3(l.x, level[i], l.y);
                verts[c * 4 + 2] = new Vector3(r.x, level[i], r.y);
                verts[c * 4 + 3] = new Vector3(ro.x, level[i] - Skirt, ro.y);
                float v = run / 4f;
                uvs[c * 4 + 0] = new Vector2(-0.2f, v);
                uvs[c * 4 + 1] = new Vector2(0f, v);
                uvs[c * 4 + 2] = new Vector2(1f, v);
                uvs[c * 4 + 3] = new Vector2(1.2f, v);
                if (c == count - 1)
                {
                    break;
                }

                for (int s = 0; s < 3; s++)
                {
                    int a = c * 4 + s, b = a + 4;
                    tris.AddRange(new[] { a, b, b + 1, a, b + 1, a + 1 });
                }
            }

            var mesh = new Mesh { name = "Exit_River" };
            mesh.vertices = verts;
            mesh.uv = uvs;
            mesh.triangles = tris.ToArray();
            mesh.RecalculateNormals();
            if (Vector3.Dot(mesh.normals[1], Vector3.up) < 0f)
            {
                tris.Reverse();
                mesh.triangles = tris.ToArray();
                mesh.RecalculateNormals();
            }

            mesh.RecalculateBounds();
            string riverPath = $"{MeshDir}/Exit_River_{assetTag}.asset";
            AssetDatabase.DeleteAsset(riverPath);
            AssetDatabase.CreateAsset(mesh, riverPath);

            Transform old = world.transform.Find("Exit_River");
            if (old != null)
            {
                Object.DestroyImmediate(old.gameObject);
            }

            var go = new GameObject("Exit_River") { isStatic = true };
            go.transform.SetParent(world.transform, true);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = AssetDatabase.LoadAssetAtPath<Material>(RiverMaterialPath);
            renderer.shadowCastingMode = ShadowCastingMode.Off;

            // ---- take the old broken ribbon out of the water mesh
            var waterRenderer = waterFilter.GetComponent<MeshRenderer>();
            Material[] mats = waterRenderer.sharedMaterials;
            int riverSub = System.Array.FindIndex(mats, m => m != null && m.name == RiverMaterialName);
            if (riverSub >= 0)
            {
                Mesh seaOnly = Object.Instantiate(waterFilter.sharedMesh);
                seaOnly.name = "Exit_Water_SeaOnly";
                seaOnly.SetTriangles(new int[0], riverSub);
                string seaPath = $"{MeshDir}/Exit_Water_SeaOnly_{assetTag}.asset";
                AssetDatabase.DeleteAsset(seaPath);
                AssetDatabase.CreateAsset(seaOnly, seaPath);
                waterFilter.sharedMesh = seaOnly;
            }

            Debug.Log($"[RiverFit] river rebuilt: {count} sections, {run:F0} m, from {level[first]:F1} down to the sea at {seaLevel:F1}");
        }
    }
}
