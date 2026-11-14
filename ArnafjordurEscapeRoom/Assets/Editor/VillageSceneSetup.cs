using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Arnafjordur.Characters;
using Arnafjordur.Intro;
using Arnafjordur.UI;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;

namespace Arnafjordur.Editor
{
    /// <summary>
    /// Builds Assets/Scenes/Arnafjordur_Village.unity: the playable Árnafjørður village scene starting at the
    /// church parking bay right after Intro_CarExit, with gravel footpaths and a <see cref="WalkableArea"/>
    /// restricting movement to the forecourt, village road, footpaths, and beach.
    /// </summary>
    public static class VillageSceneSetup
    {
        public const string ScenePath = "Assets/Scenes/Arnafjordur_Village.unity";
        public const string SceneName = "Arnafjordur_Village";

        private const string CarExitScenePath = "Assets/Scenes/Intro_CarExit.unity";
        private const string WalkableJsonPath = "Assets/Environment/Village/VillageWalkable.json";
        private const string VillageAssetDir = "Assets/Environment/Village";
        private const string GravelMatPath = "Assets/Environment/ChurchArrival/Materials/Mat_Church_Gravel.mat";
        private static readonly string[] PartyOrder = { "Olavur", "Leivur", "Andrias" };

        private static readonly HashSet<string> GroundObjectNames = new HashSet<string>
        {
            "Exit_Terrain",
            "Exit_Road",
            "ChurchForecourt",
        };

        [Serializable]
        private class WalkableFile
        {
            public float seaLevel;
            public CorridorData[] corridors;
            public FootpathData[] footpaths;
            public ZoneData[] zones;
        }

        [Serializable]
        private class CorridorData
        {
            public string name;
            public float halfWidth;
            public Vector3[] points;
        }

        [Serializable]
        private class FootpathData
        {
            public string name;
            public float halfWidth;
            public float ribbonHalfWidth;
            public Vector3[] points;
        }

        [Serializable]
        private class ZoneData
        {
            public string name;
            public Vector3[] outline;
        }

        [MenuItem("Tools/Village/Open Village Scene")]
        public static void Open()
        {
            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        }

        [MenuItem("Tools/Village/Rebuild Village Scene")]
        public static void Build()
        {
            if (!File.Exists(CarExitScenePath))
            {
                IntroCarExitSceneSetup.Build();
                if (!File.Exists(CarExitScenePath))
                {
                    Debug.LogError("[VillageSceneSetup] Intro_CarExit.unity missing after build.");
                    return;
                }
            }

            if (!File.Exists(WalkableJsonPath))
            {
                Debug.LogError($"[VillageSceneSetup] Missing {WalkableJsonPath}.");
                return;
            }

            var locomotionControllers = new Dictionary<string, RuntimeAnimatorController>();
            var idleClips = new Dictionary<string, AnimationClip>();
            foreach (string name in PartyOrder)
            {
                string ctrlPath = $"Assets/Characters/{name}/{name}_Locomotion.overrideController";
                var ctrl = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(ctrlPath);
                if (ctrl == null)
                {
                    Debug.LogError($"[VillageSceneSetup] Missing {ctrlPath} - run Arnafjordur/Setup Playable Flat Scene first.");
                    return;
                }

                locomotionControllers[name] = ctrl;
                idleClips[name] = LoadIdleClip(name);
            }

            AssetDatabase.DeleteAsset(ScenePath);
            if (!AssetDatabase.CopyAsset(CarExitScenePath, ScenePath))
            {
                Debug.LogError($"[VillageSceneSetup] Could not copy {CarExitScenePath} to {ScenePath}.");
                return;
            }

            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

            foreach (var music in UnityEngine.Object.FindObjectsByType<IntroMusic>(FindObjectsInactive.Include))
            {
                UnityEngine.Object.DestroyImmediate(music.gameObject);
            }

            var exit = UnityEngine.Object.FindAnyObjectByType<IntroCarExit>();
            var carGo = GameObject.Find("VWGolf_WithPlayers");
            var cam = Camera.main != null ? Camera.main : UnityEngine.Object.FindAnyObjectByType<Camera>();
            if (exit == null || carGo == null || cam == null)
            {
                Debug.LogError("[VillageSceneSetup] Intro_CarExit scene is missing IntroCarExit, VWGolf_WithPlayers, or Camera.");
                return;
            }

            ConfigureParkedCar(carGo);
            EnsureBuildingColliders();
            Physics.SyncTransforms();

            WalkableFile walkableData = JsonUtility.FromJson<WalkableFile>(File.ReadAllText(WalkableJsonPath));
            var walkableArea = BuildWalkableAndFootpaths(walkableData);
            BuildBeach(walkableData);
            FjordWaterSetup.ApplyToExisting();

            CharacterMotor[] motors = ExtractAndConfigurePlayers(exit, carGo, walkableArea, locomotionControllers, idleClips);
            if (motors == null)
            {
                return;
            }

            SetupCameraPartyAndHud(cam, motors, walkableArea.gameObject);

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, ScenePath);

            VerifyWalkableConstraints(walkableArea, walkableData, motors);
            RenderPreviewFrames(cam);

            var buildScenes = EditorBuildSettings.scenes.ToList();
            if (buildScenes.All(s => s.path != ScenePath))
            {
                buildScenes.Add(new EditorBuildSettingsScene(ScenePath, true));
                EditorBuildSettings.scenes = buildScenes.ToArray();
            }

            ChainCarExitToVillage();

            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            AssetDatabase.SaveAssets();
            Debug.Log($"[VillageSceneSetup] OK saved {ScenePath} ({walkableArea.Corridors.Length} corridors, {walkableArea.Zones.Length} zones, {motors.Length} players)");
        }

        private static void ConfigureParkedCar(GameObject carGo)
        {
            Transform lights = carGo.transform.Find("Lights");
            if (lights != null)
            {
                lights.gameObject.SetActive(false);
            }

            Transform bodyTransform = carGo.transform.Find("VWGolf/Body");
            if (bodyTransform != null && bodyTransform.TryGetComponent(out MeshRenderer body))
            {
                Material[] mats = body.sharedMaterials;
                for (int i = 0; i < mats.Length; i++)
                {
                    if (mats[i] == null || !mats[i].name.EndsWith("_On"))
                    {
                        continue;
                    }

                    string baseName = mats[i].name.Substring(0, mats[i].name.Length - 3);
                    var off = AssetDatabase.LoadAssetAtPath<Material>($"Assets/Vehicles/Golf/Materials/{baseName}.mat");
                    if (off != null)
                    {
                        mats[i] = off;
                    }
                }

                body.sharedMaterials = mats;
                EditorUtility.SetDirty(body);
            }

            Transform golf = carGo.transform.Find("VWGolf");
            if (golf != null)
            {
                Transform existingObs = golf.Find("CarObstacle");
                if (existingObs != null)
                {
                    UnityEngine.Object.DestroyImmediate(existingObs.gameObject);
                }

                var obstacle = new GameObject("CarObstacle");
                obstacle.layer = 2; // Ignore Raycast: blocks CharacterController, invisible to camera/click raycasts
                obstacle.transform.SetParent(golf, false);
                var capsule = obstacle.AddComponent<CapsuleCollider>();
                capsule.center = new Vector3(0f, 0.7f, 0f);
                capsule.radius = 0.85f;
                capsule.height = 3.8f;
                capsule.direction = 2;
            }
        }

        private static void EnsureBuildingColliders()
        {
            string[] staticObstacles = { "Exit_Village", "ChurchDetail", "ChurchyardWallAndProps" };
            foreach (string objectName in staticObstacles)
            {
                GameObject go = GameObject.Find(objectName);
                if (go == null)
                {
                    continue;
                }

                if (go.TryGetComponent(out MeshFilter mf) && mf.sharedMesh != null && go.GetComponent<Collider>() == null)
                {
                    go.AddComponent<MeshCollider>().sharedMesh = mf.sharedMesh;
                }
            }
        }

        private static WalkableArea BuildWalkableAndFootpaths(WalkableFile data)
        {
            var corridors = new List<WalkableArea.Corridor>();
            if (data.corridors != null)
            {
                foreach (CorridorData c in data.corridors)
                {
                    corridors.Add(new WalkableArea.Corridor
                    {
                        name = c.name,
                        halfWidth = c.halfWidth,
                        points = c.points ?? Array.Empty<Vector3>(),
                    });
                }
            }

            GameObject existingFootpaths = GameObject.Find("VillageFootpaths");
            if (existingFootpaths != null)
            {
                UnityEngine.Object.DestroyImmediate(existingFootpaths);
            }

            var footpathsRoot = new GameObject("VillageFootpaths") { isStatic = true };
            Material gravelMat = AssetDatabase.LoadAssetAtPath<Material>(GravelMatPath);

            if (data.footpaths != null)
            {
                foreach (FootpathData fp in data.footpaths)
                {
                    if (fp.points == null || fp.points.Length < 2)
                    {
                        continue;
                    }

                    Vector3[] groundedPoints = BuildFootpathMeshAndGetPoints(fp, footpathsRoot.transform, gravelMat);
                    corridors.Add(new WalkableArea.Corridor
                    {
                        name = fp.name,
                        halfWidth = fp.halfWidth,
                        points = groundedPoints,
                    });
                }
            }

            Physics.SyncTransforms();

            var zones = new List<WalkableArea.Zone>();
            if (data.zones != null)
            {
                foreach (ZoneData z in data.zones)
                {
                    zones.Add(new WalkableArea.Zone
                    {
                        name = z.name,
                        outline = z.outline ?? Array.Empty<Vector3>(),
                    });
                }
            }

            GameObject partyGo = GameObject.Find("_PartySystem");
            if (partyGo == null)
            {
                partyGo = new GameObject("_PartySystem");
            }

            var area = GetOrAdd<WalkableArea>(partyGo);
            area.Configure(corridors.ToArray(), zones.ToArray());
            EditorUtility.SetDirty(area);
            return area;
        }

        private static Vector3[] BuildFootpathMeshAndGetPoints(FootpathData fp, Transform parent, Material gravelMat)
        {
            Vector3[] src = fp.points;
            int count = src.Length;
            var distances = new float[count];
            for (int i = 1; i < count; i++)
            {
                float dx = src[i].x - src[i - 1].x;
                float dz = src[i].z - src[i - 1].z;
                distances[i] = distances[i - 1] + Mathf.Sqrt(dx * dx + dz * dz);
            }

            const float MaxRampGrade = 0.55f;
            float totalLen = Mathf.Max(0.001f, distances[count - 1]);
            float yStart = SurfaceY(src[0], includeFootpaths: false);
            float yEnd = SurfaceY(src[count - 1], includeFootpaths: false);
            float startDelta = yStart - src[0].y;
            float endDelta = yEnd - src[count - 1].y;

            var centerHeights = new float[count];
            for (int i = 0; i < count; i++)
            {
                float d = distances[i];
                float blendedY;
                if (totalLen < 15f)
                {
                    blendedY = Mathf.Max(src[i].y, Mathf.Lerp(yStart, yEnd, d / totalLen)) + 0.03f;
                }
                else
                {
                    float startBlend = 1f - Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(d / 7.5f));
                    float endBlend = 1f - Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((totalLen - d) / 7.5f));
                    blendedY = src[i].y + startDelta * startBlend + endDelta * endBlend + 0.03f;
                }

                float groundY = SurfaceY(src[i], includeFootpaths: false);
                centerHeights[i] = Mathf.Max(blendedY, groundY + 0.03f);
            }

            SmoothMaxGradeEnvelope(centerHeights, distances, MaxRampGrade);

            var centerLine = new Vector3[count];
            for (int i = 0; i < count; i++)
            {
                centerLine[i] = new Vector3(src[i].x, centerHeights[i], src[i].z);
            }

            float halfRibbon = fp.ribbonHalfWidth > 0f ? fp.ribbonHalfWidth : fp.halfWidth + 0.35f;
            float outerOffset = halfRibbon + 0.45f;

            var leftEdgePoints = new Vector3[count];
            var rightEdgePoints = new Vector3[count];
            var leftOuterPoints = new Vector3[count];
            var rightOuterPoints = new Vector3[count];
            var leftTopHeights = new float[count];
            var rightTopHeights = new float[count];
            var leftOuterHeights = new float[count];
            var rightOuterHeights = new float[count];

            for (int i = 0; i < count; i++)
            {
                Vector3 prev = centerLine[Mathf.Max(i - 1, 0)];
                Vector3 next = centerLine[Mathf.Min(i + 1, count - 1)];
                Vector3 forward = new Vector3(next.x - prev.x, 0f, next.z - prev.z);
                if (forward.sqrMagnitude < 1e-6f)
                {
                    forward = Vector3.forward;
                }

                Vector3 side = Vector3.Cross(Vector3.up, forward.normalized).normalized;
                Vector3 c = centerLine[i];

                leftEdgePoints[i] = c - side * halfRibbon;
                rightEdgePoints[i] = c + side * halfRibbon;
                float leftGround = SurfaceY(leftEdgePoints[i], includeFootpaths: false);
                float rightGround = SurfaceY(rightEdgePoints[i], includeFootpaths: false);

                leftTopHeights[i] = Mathf.Max(c.y, leftGround + 0.03f);
                rightTopHeights[i] = Mathf.Max(c.y, rightGround + 0.03f);

                leftOuterPoints[i] = c - side * outerOffset;
                rightOuterPoints[i] = c + side * outerOffset;
                leftOuterHeights[i] = SurfaceY(leftOuterPoints[i], includeFootpaths: false) - 0.35f;
                rightOuterHeights[i] = SurfaceY(rightOuterPoints[i], includeFootpaths: false) - 0.35f;
            }

            SmoothMaxGradeEnvelope(leftTopHeights, distances, MaxRampGrade);
            SmoothMaxGradeEnvelope(rightTopHeights, distances, MaxRampGrade);

            var verts = new Vector3[count * 4];
            var uvs = new Vector2[count * 4];
            var tris = new List<int>((count - 1) * 18);

            for (int i = 0; i < count; i++)
            {
                int row = i * 4;
                verts[row + 0] = new Vector3(leftOuterPoints[i].x, leftOuterHeights[i], leftOuterPoints[i].z);
                verts[row + 1] = new Vector3(leftEdgePoints[i].x, leftTopHeights[i], leftEdgePoints[i].z);
                verts[row + 2] = new Vector3(rightEdgePoints[i].x, rightTopHeights[i], rightEdgePoints[i].z);
                verts[row + 3] = new Vector3(rightOuterPoints[i].x, rightOuterHeights[i], rightOuterPoints[i].z);

                float v = distances[i] / 4f;
                uvs[row + 0] = new Vector2(-0.35f, v);
                uvs[row + 1] = new Vector2(0f, v);
                uvs[row + 2] = new Vector2(1f, v);
                uvs[row + 3] = new Vector2(1.35f, v);
            }

            for (int i = 0; i < count - 1; i++)
            {
                int r0 = i * 4;
                int r1 = (i + 1) * 4;
                for (int col = 0; col < 3; col++)
                {
                    int a = r0 + col;
                    int b = r1 + col;
                    int c = r1 + col + 1;
                    int d = r0 + col + 1;
                    tris.Add(a);
                    tris.Add(b);
                    tris.Add(c);
                    tris.Add(a);
                    tris.Add(c);
                    tris.Add(d);
                }
            }

            var mesh = new Mesh { name = $"Footpath_{fp.name}" };
            mesh.vertices = verts;
            mesh.uv = uvs;
            mesh.triangles = tris.ToArray();
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();

            string meshPath = $"{VillageAssetDir}/Footpath_{fp.name}.asset";
            AssetDatabase.DeleteAsset(meshPath);
            AssetDatabase.CreateAsset(mesh, meshPath);

            var go = new GameObject($"Footpath_{fp.name}") { isStatic = true };
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = go.AddComponent<MeshRenderer>();
            if (gravelMat != null)
            {
                renderer.sharedMaterial = gravelMat;
            }

            go.AddComponent<MeshCollider>().sharedMesh = mesh;
            return centerLine;
        }

        // ------------------------------------------------------------------ beach sand
        // The terrain albedo is ~1.3 m per pixel, far too coarse for a beach, so the sand is its own layer:
        // a mesh draped just above the terrain around the "Beach" zone, with a painted sand texture
        // (wet sand at the waterline, tide lines of seaweed, pebbles, a ragged edge where the turf starts).
        private const float BeachGridStep = 1f;
        private const float BeachPixelsPerMetre = 12f;
        private const float BeachLift = 0.05f;
        private const string BeachAlbedoPath = VillageAssetDir + "/Beach_Sand_Albedo.png";
        private const string BeachGlossPath = VillageAssetDir + "/Beach_Sand_Gloss.png";
        private const string BeachMatPath = VillageAssetDir + "/Mat_Beach_Sand.mat";
        private const string BeachMeshPath = VillageAssetDir + "/Beach_Sand.asset";
        private const string TerrainDetailPath = "Assets/Environment/TunnelExit/Textures/Exit_Terrain_Detail.png";

        private static void BuildBeach(WalkableFile data)
        {
            ZoneData zone = data.zones?.FirstOrDefault(z => z.name == "Beach");
            if (zone == null || zone.outline == null || zone.outline.Length < 3)
            {
                Debug.LogWarning("[VillageSceneSetup] No Beach zone - skipping the sand.");
                return;
            }

            GameObject existing = GameObject.Find("VillageBeach");
            if (existing != null)
            {
                UnityEngine.Object.DestroyImmediate(existing);
            }

            float sea = data.seaLevel;
            const float fade = 8f;
            float x0 = zone.outline.Min(p => p.x) - 10f;
            float x1 = zone.outline.Max(p => p.x) + 30f;
            float z0 = zone.outline.Min(p => p.z) - 12f;
            float z1 = zone.outline.Max(p => p.z) + 12f;

            // ---- terrain heights on a 1 m grid
            int nx = Mathf.CeilToInt((x1 - x0) / BeachGridStep) + 1;
            int nz = Mathf.CeilToInt((z1 - z0) / BeachGridStep) + 1;
            x1 = x0 + (nx - 1) * BeachGridStep;
            z1 = z0 + (nz - 1) * BeachGridStep;

            GameObject terrainGo = GameObject.Find("Exit_Terrain");
            Collider terrain = terrainGo != null ? terrainGo.GetComponent<Collider>() : null;
            // sampled twice as fine as the mesh, so the sharp edge where the land drops into the fjord is found
            int fx = nx * 2 - 1, fz = nz * 2 - 1;
            var fine = new float[fx * fz];
            for (int j = 0; j < fz; j++)
            {
                for (int i = 0; i < fx; i++)
                {
                    float x = x0 + i * BeachGridStep * 0.5f;
                    float z = z0 + j * BeachGridStep * 0.5f;
                    if (terrain != null && terrain.Raycast(new Ray(new Vector3(x, 40f, z), Vector3.down), out RaycastHit hit, 220f))
                    {
                        fine[j * fx + i] = hit.point.y;
                    }
                    else
                    {
                        fine[j * fx + i] = SurfaceY(new Vector3(x, 0f, z), includeFootpaths: false);
                    }
                }
            }

            var heights = new float[nx * nz];
            for (int j = 0; j < nz; j++)
            {
                for (int i = 0; i < nx; i++)
                {
                    float lo = float.PositiveInfinity, hi = float.NegativeInfinity;
                    for (int dj = -2; dj <= 2; dj++)
                    {
                        for (int di = -2; di <= 2; di++)
                        {
                            int si = Mathf.Clamp(i * 2 + di, 0, fx - 1);
                            int sj = Mathf.Clamp(j * 2 + dj, 0, fz - 1);
                            float s = fine[sj * fx + si];
                            lo = Mathf.Min(lo, s);
                            hi = Mathf.Max(hi, s);
                        }
                    }

                    // where the ground is steep or creased (the bank down to the water), sit on the highest
                    // ground within a metre so no edge pokes through between the grid points; on the
                    // gentle rest of the beach just follow the terrain
                    float centre = fine[j * 2 * fx + i * 2];
                    heights[j * nx + i] = Mathf.Lerp(centre, hi, Smooth(0.3f, 0.7f, hi - lo));
                }
            }

            // ---- at the water's edge the terrain drops straight to the fjord floor; carry the sand on from
            // the last bit of land in a gentle slope instead, so it runs into the water like a beach and
            // the shallows and the foam have some width
            var shoreDist = new float[nx * nz];
            var shoreHeight = new float[nx * nz]; // height of the nearest land
            for (int k = 0; k < shoreDist.Length; k++)
            {
                // land never sits lower than 0.25 m above the sea; anything below that is the drop-off face
                shoreDist[k] = heights[k] >= sea + 0.2f ? 0f : 1e6f;
                shoreHeight[k] = heights[k];
            }

            for (int pass = 0; pass < 2; pass++)
            {
                int dir = pass == 0 ? 1 : -1;
                for (int jj = 0; jj < nz; jj++)
                {
                    int j = pass == 0 ? jj : nz - 1 - jj;
                    for (int ii = 0; ii < nx; ii++)
                    {
                        int i = pass == 0 ? ii : nx - 1 - ii;
                        int self = j * nx + i;
                        int pj = j - dir;

                        void Relax(int from, float step)
                        {
                            if (shoreDist[from] + step < shoreDist[self])
                            {
                                shoreDist[self] = shoreDist[from] + step;
                                shoreHeight[self] = shoreHeight[from];
                            }
                        }

                        if (i - dir >= 0 && i - dir < nx)
                        {
                            Relax(self - dir, BeachGridStep);
                        }

                        if (pj >= 0 && pj < nz)
                        {
                            Relax(pj * nx + i, BeachGridStep);
                            for (int di = -1; di <= 1; di += 2)
                            {
                                if (i + di >= 0 && i + di < nx)
                                {
                                    Relax(pj * nx + i + di, BeachGridStep * 1.4142f);
                                }
                            }
                        }
                    }
                }
            }

            for (int k = 0; k < heights.Length; k++)
            {
                if (shoreDist[k] > 0f)
                {
                    float run = Mathf.Max(shoreDist[k] - 1f, 0f);
                    heights[k] = Mathf.Max(shoreHeight[k] + 0.08f - 0.16f * run, sea - 8f); // a touch proud of the terrain's edge
                }
            }

            // ---- paint
            int w = Mathf.Min(2048, Mathf.RoundToInt((x1 - x0) * BeachPixelsPerMetre));
            int h = Mathf.Min(2048, Mathf.RoundToInt((z1 - z0) * BeachPixelsPerMetre));
            var albedo = new Color32[w * h];
            var gloss = new Color32[w * h];

            var dryA = new Color(0.60f, 0.54f, 0.44f);
            var dryB = new Color(0.74f, 0.68f, 0.56f);
            var wetCol = new Color(0.44f, 0.36f, 0.25f);
            var wrackCol = new Color(0.15f, 0.13f, 0.09f);
            var earthCol = new Color(0.31f, 0.29f, 0.19f);
            var turfCol = new Color(0.23f, 0.28f, 0.13f);

            for (int py = 0; py < h; py++)
            {
                float z = Mathf.Lerp(z0, z1, (py + 0.5f) / h);
                for (int px = 0; px < w; px++)
                {
                    float x = Mathf.Lerp(x0, x1, (px + 0.5f) / w);
                    float above = SampleGrid(heights, nx, nz, (x - x0) / BeachGridStep, (z - z0) / BeachGridStep) - sea;

                    float nBig = Fbm(x * 0.06f + 11.3f, z * 0.06f + 4.1f);
                    float nMid = Fbm(x * 0.7f + 53.7f, z * 0.7f + 17.9f);
                    float nFine = Mathf.PerlinNoise(x * 5.5f + 3.3f, z * 5.5f + 91.2f);

                    // how much this spot is beach: low ground, fading out towards the ends of the patch
                    float top = 3.9f + (nBig - 0.5f) * 1.6f;
                    float edge = Smooth(0f, fade, x - x0) * Smooth(0f, fade, z - z0) * Smooth(0f, fade, z1 - z);
                    float sand = (1f - Smooth(top - 0.9f, top + 0.3f, above)) * edge;
                    float alpha = Mathf.Clamp01(sand + (nFine - 0.5f) * 0.45f + (nMid - 0.5f) * 0.3f);
                    if (InsideFootpath(data.footpaths, x, z))
                    {
                        alpha = 0f; // the gravel ribbon lies here
                    }

                    float wet = 1f - Smooth(0.1f, 1.3f, above + (nMid - 0.5f) * 0.9f + (nBig - 0.5f) * 0.6f);
                    Color col = Color.Lerp(dryA, dryB, nBig) * (0.92f + 0.16f * nFine);
                    col = Color.Lerp(col, wetCol * (0.9f + 0.2f * nMid), wet);

                    // faint ripple lines following the slope on the lower beach
                    float ripple = 0.5f + 0.5f * Mathf.Sin(above * 22f + nBig * 9f + nMid * 2f);
                    col *= 1f - 0.08f * ripple * (1f - Smooth(1.2f, 2.2f, above));

                    // tide lines: a broken band of dark seaweed at high water, a fainter old one above it
                    float patchy = Smooth(0.38f, 0.62f, Mathf.PerlinNoise(x * 1.7f + 31f, z * 1.7f + 7f));
                    float wrack = Bump(above, 1.95f + (nBig - 0.5f) * 0.5f, 0.13f) * patchy;
                    float oldWrack = Bump(above, 2.9f + (nBig - 0.5f) * 0.7f, 0.10f) * (1f - patchy) * 0.45f;
                    col = Color.Lerp(col, wrackCol, 0.85f * Mathf.Max(wrack, oldWrack));

                    // where the sand runs out: darker earth and tufts of turf
                    float thin = 1f - sand;
                    col = Color.Lerp(col, earthCol, 0.7f * Smooth(0.15f, 0.8f, thin));
                    float tuft = Smooth(0.58f, 0.68f, Mathf.PerlinNoise(x * 3.1f + 7.7f, z * 3.1f + 60.4f));
                    col = Color.Lerp(col, turfCol * (0.85f + 0.3f * nFine), tuft * Smooth(0.1f, 0.5f, thin));

                    // wet sand shines, but only above the waterline: under it the water does the reflecting
                    float smooth = Mathf.Lerp(0.10f, 0.2f, wet * Smooth(-0.15f, 0.05f, above));

                    // basalt pebbles: thick along the tide line and at the water's edge, sparse elsewhere
                    float density = 0.05f + 0.5f * wrack + 0.4f * (1f - Smooth(0.1f, 0.6f, above)) * Smooth(-1.2f, -0.2f, above) + 0.2f * thin;
                    if (Pebble(x, z, density, out float shade, out float lit))
                    {
                        col = new Color(0.20f, 0.21f, 0.22f) * (0.7f + 1.1f * shade) * (0.75f + 0.5f * lit);
                        smooth = Mathf.Max(smooth, 0.25f);
                    }

                    if (above < 0f)
                    {
                        col *= 0.8f;
                    }

                    albedo[py * w + px] = new Color(col.r, col.g, col.b, alpha);
                    gloss[py * w + px] = new Color(0f, 0f, 0f, smooth);
                }
            }

            Texture2D albedoTex = SaveBeachTexture(BeachAlbedoPath, albedo, w, h, srgb: true);
            Texture2D glossTex = SaveBeachTexture(BeachGlossPath, gloss, w, h, srgb: false);

            // ---- material
            var mat = AssetDatabase.LoadAssetAtPath<Material>(BeachMatPath);
            if (mat == null)
            {
                mat = new Material(Shader.Find("Universal Render Pipeline/Lit")) { name = "Mat_Beach_Sand" };
                AssetDatabase.CreateAsset(mat, BeachMatPath);
            }

            mat.SetColor("_BaseColor", Color.white);
            mat.SetTexture("_BaseMap", albedoTex);
            mat.SetTexture("_MainTex", albedoTex);
            mat.SetTexture("_MetallicGlossMap", glossTex);
            mat.EnableKeyword("_METALLICSPECGLOSSMAP");
            mat.SetFloat("_Metallic", 0f);
            mat.SetFloat("_Smoothness", 1f);
            mat.SetFloat("_SmoothnessTextureChannel", 0f);
            mat.SetFloat("_AlphaClip", 1f);
            mat.SetFloat("_Cutoff", 0.5f);
            mat.EnableKeyword("_ALPHATEST_ON");
            mat.SetOverrideTag("RenderType", "TransparentCutout");
            mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.AlphaTest;
            var detail = AssetDatabase.LoadAssetAtPath<Texture2D>(TerrainDetailPath);
            if (detail != null)
            {
                mat.SetTexture("_DetailAlbedoMap", detail);
                mat.SetTextureScale("_DetailAlbedoMap", new Vector2((x1 - x0) / 2.5f, (z1 - z0) / 2.5f));
                mat.SetFloat("_DetailAlbedoMapScale", 0.6f);
                mat.EnableKeyword("_DETAIL_MULX2");
            }

            EditorUtility.SetDirty(mat);

            // ---- mesh: only the grid cells that can hold sand
            var verts = new Vector3[nx * nz];
            var uvs = new Vector2[nx * nz];
            var maybeSand = new bool[nx * nz];
            for (int j = 0; j < nz; j++)
            {
                for (int i = 0; i < nx; i++)
                {
                    int k = j * nx + i;
                    verts[k] = new Vector3(x0 + i * BeachGridStep, heights[k] + BeachLift, z0 + j * BeachGridStep);
                    uvs[k] = new Vector2(i / (float)(nx - 1), j / (float)(nz - 1));
                    maybeSand[k] = heights[k] - sea < 5.6f;
                }
            }

            var tris = new List<int>();
            for (int j = 0; j < nz - 1; j++)
            {
                for (int i = 0; i < nx - 1; i++)
                {
                    int a = j * nx + i;
                    int b = a + nx;
                    if (!(maybeSand[a] || maybeSand[a + 1] || maybeSand[b] || maybeSand[b + 1]))
                    {
                        continue;
                    }

                    tris.Add(a);
                    tris.Add(b);
                    tris.Add(b + 1);
                    tris.Add(a);
                    tris.Add(b + 1);
                    tris.Add(a + 1);
                }
            }

            var mesh = new Mesh { name = "Beach_Sand" };
            mesh.vertices = verts;
            mesh.uv = uvs;
            mesh.triangles = tris.ToArray();
            mesh.RecalculateNormals();
            mesh.RecalculateTangents();
            mesh.RecalculateBounds();
            AssetDatabase.DeleteAsset(BeachMeshPath);
            AssetDatabase.CreateAsset(mesh, BeachMeshPath);

            var go = new GameObject("VillageBeach") { isStatic = true };
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = mat;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        }

        private static Texture2D SaveBeachTexture(string path, Color32[] pixels, int w, int h, bool srgb)
        {
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            tex.SetPixels32(pixels);
            File.WriteAllBytes(path, tex.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(tex);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);

            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            importer.sRGBTexture = srgb;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.alphaIsTransparency = false;
            importer.maxTextureSize = 2048;
            importer.anisoLevel = 8;
            importer.mipmapEnabled = true;
            if (srgb)
            {
                importer.mipMapsPreserveCoverage = true;
                importer.alphaTestReferenceValue = 0.5f;
            }

            importer.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        private static bool InsideFootpath(FootpathData[] footpaths, float x, float z)
        {
            if (footpaths == null)
            {
                return false;
            }

            foreach (FootpathData fp in footpaths)
            {
                if (fp.points == null)
                {
                    continue;
                }

                float half = (fp.ribbonHalfWidth > 0f ? fp.ribbonHalfWidth : fp.halfWidth + 0.35f) - 0.15f;
                for (int i = 0; i < fp.points.Length - 1; i++)
                {
                    float ax = fp.points[i].x, az = fp.points[i].z;
                    float dx = fp.points[i + 1].x - ax, dz = fp.points[i + 1].z - az;
                    float len2 = dx * dx + dz * dz;
                    if (len2 < 1e-6f)
                    {
                        continue;
                    }

                    float t = ((x - ax) * dx + (z - az) * dz) / len2;
                    if ((i == 0 && t < 0f) || (i == fp.points.Length - 2 && t > 1f))
                    {
                        continue; // the ribbon is cut square at its ends
                    }

                    t = Mathf.Clamp01(t);
                    float ex = x - (ax + dx * t), ez = z - (az + dz * t);
                    if (ex * ex + ez * ez < half * half)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool Pebble(float x, float z, float density, out float shade, out float lit)
        {
            const float cell = 0.3f;
            int cx = Mathf.FloorToInt(x / cell);
            int cz = Mathf.FloorToInt(z / cell);
            for (int dz = -1; dz <= 1; dz++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    int ix = cx + dx, iz = cz + dz;
                    if (Hash(ix, iz, 1) > density)
                    {
                        continue;
                    }

                    float ox = x - (ix + Hash(ix, iz, 2)) * cell;
                    float oz = z - (iz + Hash(ix, iz, 3)) * cell;
                    float r = 0.05f + 0.09f * Hash(ix, iz, 4);
                    if (ox * ox + oz * oz < r * r)
                    {
                        shade = Hash(ix, iz, 5);
                        lit = Mathf.Clamp01(0.5f + (ox - oz) / (2.8f * r)); // lit from one side so they read as round
                        return true;
                    }
                }
            }

            shade = 0f;
            lit = 0f;
            return false;
        }

        private static float Hash(int x, int y, int seed)
        {
            unchecked
            {
                uint n = (uint)(x * 374761393 + y * 668265263 + seed * 1442695041);
                n = (n ^ (n >> 13)) * 1274126177u;
                n ^= n >> 16;
                return (n & 0xFFFFFF) / 16777216f;
            }
        }

        private static float Fbm(float x, float z)
        {
            return Mathf.PerlinNoise(x, z) * 0.55f
                + Mathf.PerlinNoise(x * 2.1f + 13.1f, z * 2.1f + 5.7f) * 0.3f
                + Mathf.PerlinNoise(x * 4.3f + 71.9f, z * 4.3f + 29.3f) * 0.15f;
        }

        private static float Smooth(float a, float b, float v)
        {
            float t = Mathf.Clamp01((v - a) / (b - a));
            return t * t * (3f - 2f * t);
        }

        private static float Bump(float v, float centre, float width)
        {
            float d = (v - centre) / width;
            return Mathf.Exp(-d * d);
        }

        private static float SampleGrid(float[] grid, int nx, int nz, float fx, float fz)
        {
            fx = Mathf.Clamp(fx, 0f, nx - 1.001f);
            fz = Mathf.Clamp(fz, 0f, nz - 1.001f);
            int i = (int)fx, j = (int)fz;
            float tx = fx - i, tz = fz - j;
            int k = j * nx + i;
            return Mathf.Lerp(Mathf.Lerp(grid[k], grid[k + 1], tx), Mathf.Lerp(grid[k + nx], grid[k + nx + 1], tx), tz);
        }

        private static void SmoothMaxGradeEnvelope(float[] heights, float[] distances, float maxGrade)
        {
            for (int i = 1; i < heights.Length; i++)
            {
                float ds = Mathf.Max(0f, distances[i] - distances[i - 1]);
                heights[i] = Mathf.Max(heights[i], heights[i - 1] - maxGrade * ds);
            }

            for (int i = heights.Length - 2; i >= 0; i--)
            {
                float ds = Mathf.Max(0f, distances[i + 1] - distances[i]);
                heights[i] = Mathf.Max(heights[i], heights[i + 1] - maxGrade * ds);
            }
        }

        private static CharacterMotor[] ExtractAndConfigurePlayers(
            IntroCarExit exit,
            GameObject carGo,
            WalkableArea walkableArea,
            Dictionary<string, RuntimeAnimatorController> controllers,
            Dictionary<string, AnimationClip> idleClips)
        {
            if (PrefabUtility.IsPartOfPrefabInstance(carGo))
            {
                PrefabUtility.UnpackPrefabInstance(carGo, PrefabUnpackMode.OutermostRoot, InteractionMode.AutomatedAction);
            }

            var playersRoot = new GameObject("Players");
            var motors = new CharacterMotor[PartyOrder.Length];
            var occByName = new Dictionary<string, IntroCarExit.Occupant>(StringComparer.OrdinalIgnoreCase);

            if (exit.Occupants != null)
            {
                foreach (IntroCarExit.Occupant occ in exit.Occupants)
                {
                    Transform occRoot = occ.root != null ? occ.root : (occ.animator != null ? occ.animator.transform : null);
                    if (occRoot == null)
                    {
                        continue;
                    }

                    foreach (string name in PartyOrder)
                    {
                        if (occRoot.name.StartsWith(name, StringComparison.OrdinalIgnoreCase))
                        {
                            IntroCarExit.Occupant resolved = occ;
                            resolved.root = occRoot;
                            occByName[name] = resolved;
                        }
                    }
                }
            }

            var lookTarget = new Vector3(401f, 0f, 290f);

            for (int i = 0; i < PartyOrder.Length; i++)
            {
                string name = PartyOrder[i];
                Transform charTransform = null;
                Vector3[] route = null;
                Animator animator = null;

                if (occByName.TryGetValue(name, out IntroCarExit.Occupant occ))
                {
                    charTransform = occ.root;
                    route = occ.route;
                    animator = occ.animator;
                }

                if (charTransform == null)
                {
                    charTransform = carGo.transform.Find($"Occupants/{name}_Seated");
                }

                if (charTransform == null)
                {
                    Debug.LogError($"[VillageSceneSetup] Could not find occupant transform for {name}.");
                    return null;
                }

                charTransform.SetParent(playersRoot.transform, true);
                charTransform.name = name;
                charTransform.localScale = Vector3.one;

                Vector3 pos = route != null && route.Length > 0 ? route[route.Length - 1] : charTransform.position;
                pos = walkableArea.Clamp(pos);
                pos.y = SurfaceY(pos, includeFootpaths: true);
                charTransform.position = pos;

                Vector3 faceDir = new Vector3(lookTarget.x - pos.x, 0f, lookTarget.z - pos.z);
                if (faceDir.sqrMagnitude > 1e-4f)
                {
                    charTransform.rotation = Quaternion.LookRotation(faceDir.normalized, Vector3.up);
                }
                else
                {
                    charTransform.rotation = Quaternion.identity;
                }

                if (animator == null)
                {
                    animator = GetOrAdd<Animator>(charTransform.gameObject);
                }

                animator.runtimeAnimatorController = controllers[name];
                animator.applyRootMotion = false;
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;

                Material charMat = AssetDatabase.LoadAssetAtPath<Material>($"Assets/Characters/{name}/Mat_{name}.mat");
                foreach (SkinnedMeshRenderer smr in charTransform.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    smr.updateWhenOffscreen = true;
                    if (charMat != null && smr.sharedMaterials.Length <= 1)
                    {
                        smr.sharedMaterial = charMat;
                    }
                }

                if (idleClips.TryGetValue(name, out AnimationClip idle) && idle != null)
                {
                    idle.SampleAnimation(charTransform.gameObject, 0f);
                }

                if (charTransform.TryGetComponent(out BeerDrinker drinker))
                {
                    drinker.StartLowered = true;
                    drinker.Pose(0f, 0f);
                    EditorUtility.SetDirty(drinker);
                }

                var controller = GetOrAdd<CharacterController>(charTransform.gameObject);
                controller.height = 1.74f;
                controller.radius = 0.28f;
                controller.skinWidth = 0.03f;
                controller.center = new Vector3(0f, 0.90f, 0f);
                controller.stepOffset = 0.25f;
                controller.slopeLimit = 45f;
                controller.minMoveDistance = 0.0001f;

                var motor = GetOrAdd<CharacterMotor>(charTransform.gameObject);
                motor.Configure(name, animator);
                motor.Area = walkableArea;

                EditorUtility.SetDirty(animator);
                EditorUtility.SetDirty(controller);
                EditorUtility.SetDirty(motor);
                motors[i] = motor;
            }

            Transform occupantsContainer = carGo.transform.Find("Occupants");
            if (occupantsContainer != null)
            {
                UnityEngine.Object.DestroyImmediate(occupantsContainer.gameObject);
            }

            UnityEngine.Object.DestroyImmediate(exit.gameObject);
            return motors;
        }

        private static void SetupCameraPartyAndHud(Camera cam, CharacterMotor[] motors, GameObject partyGo)
        {
            cam.gameObject.name = "MainCamera";
            cam.fieldOfView = 45f;
            var cameraRig = GetOrAdd<ThirdPersonCameraRig>(cam.gameObject);
            if (motors.Length > 0 && motors[0] != null)
            {
                cameraRig.SetTarget(motors[0].transform, snap: false);
                cameraRig.SetOrbit(16f, 18f, 5.6f, snap: true);
            }

            EditorUtility.SetDirty(cameraRig);

            var partyController = GetOrAdd<PartyController>(partyGo);
            partyController.Configure(motors, cameraRig, cam);
            partyController.SetCompanionMode(PartyController.CompanionMode.Follow);
            EditorUtility.SetDirty(partyController);

            var hud = GetOrAdd<CharacterTestHud>(partyGo);
            hud.Configure(partyController);
            EditorUtility.SetDirty(hud);

            EventSystem existingEventSystem = UnityEngine.Object.FindAnyObjectByType<EventSystem>();
            if (existingEventSystem == null)
            {
                new GameObject("EventSystem", typeof(EventSystem), typeof(InputSystemUIInputModule));
            }
            else if (existingEventSystem.GetComponent<InputSystemUIInputModule>() == null)
            {
                existingEventSystem.gameObject.AddComponent<InputSystemUIInputModule>();
            }
        }

        private static void VerifyWalkableConstraints(WalkableArea area, WalkableFile data, CharacterMotor[] motors)
        {
            var log = "[VillageSceneSetup] Walkable verification:";
            foreach (CharacterMotor motor in motors)
            {
                Vector3 p = motor.transform.position;
                bool inside = area.Contains(p);
                bool hasGround = TrySurfaceY(p, includeFootpaths: true, out float groundY);
                float groundDelta = hasGround ? Mathf.Abs(p.y - groundY) : float.PositiveInfinity;
                log += $"\n  Start {motor.DisplayName}: {p} inside={inside} groundY={groundY:F2} delta={groundDelta:F3} areaBound={motor.Area == area}";
                if (!inside || !hasGround || groundDelta > 0.05f || motor.Area != area)
                {
                    Debug.LogError($"[VillageSceneSetup] Player {motor.DisplayName} invalid start state at {p} (inside={inside}, hasGround={hasGround}, groundDelta={groundDelta:F3}, areaBound={motor.Area == area})!");
                }
            }

            Vector3 uphillProbe = new Vector3(440f, -80f, 270f);
            Vector3 uphillClamped = area.Clamp(uphillProbe);
            bool uphillBlocked = !area.Contains(uphillProbe) && uphillClamped.x <= 427.5f;
            log += $"\n  Uphill mountain road {uphillProbe} -> inside={area.Contains(uphillProbe)}, clamped={uphillClamped}, blocked={uphillBlocked}";
            if (!uphillBlocked)
            {
                Debug.LogError("[VillageSceneSetup] Uphill mountain road probe was not clamped as expected!");
            }

            var offLimitsProbes = new (string label, Vector3 point)[]
            {
                ("MountainAboveChurch", new Vector3(420f, -78f, 245f)),
                ("WestOffRoadHouse", new Vector3(372f, -83f, 340f)),
                ("DeepSeaEast", new Vector3(468f, -89f, 345f)),
            };
            foreach (var probe in offLimitsProbes)
            {
                bool inside = area.Contains(probe.point);
                Vector3 clamped = area.Clamp(probe.point);
                log += $"\n  OffLimits {probe.label} {probe.point} -> inside={inside}, clamped={clamped}";
                if (inside)
                {
                    Debug.LogError($"[VillageSceneSetup] Expected off-limits point {probe.label} was inside WalkableArea!");
                }
            }

            if (data.corridors != null)
            {
                foreach (CorridorData corridor in data.corridors)
                {
                    log += "\n" + VerifyPolylineContinuity($"Corridor.{corridor.name}", corridor.points, area);
                }
            }

            if (data.footpaths != null)
            {
                foreach (FootpathData footpath in data.footpaths)
                {
                    log += "\n" + VerifyPolylineContinuity($"Footpath.{footpath.name}", footpath.points, area);
                }
            }

            var pathSamples = new (string label, Vector3 point)[]
            {
                ("ChurchPath_Mid", new Vector3(400.6f, -82f, 286.6f)),
                ("Bridge", new Vector3(397.54f, -83f, 306.69f)),
                ("VillageRoad_Mid", new Vector3(388.367f, -82.5f, 348.1f)),
                ("BeachPathSouth_Mid", new Vector3(416.5f, -85.7f, 314.6f)),
                ("BeachPathNorth_Mid", new Vector3(413.0f, -84.2f, 398.6f)),
                ("Beach_Center", new Vector3(430f, -87f, 345f)),
            };

            foreach (var sample in pathSamples)
            {
                bool inside = area.Contains(sample.point);
                bool hasGround = TrySurfaceY(sample.point, includeFootpaths: true, out float groundY);
                log += $"\n  {sample.label} ({sample.point.x:F1}, {sample.point.z:F1}): inside={inside}, hasGround={hasGround}, groundY={groundY:F2}";
                if (!inside || !hasGround)
                {
                    Debug.LogError($"[VillageSceneSetup] Expected walkable point {sample.label} invalid (inside={inside}, hasGround={hasGround})!");
                }
            }

            Debug.Log(log);
        }

        private static string VerifyPolylineContinuity(string label, Vector3[] points, WalkableArea area)
        {
            if (points == null || points.Length < 2)
            {
                return $"  {label}: skipped (points={(points == null ? 0 : points.Length)})";
            }

            const float StepSpacing = 0.25f;
            int totalSamples = 0;
            int outsideCount = 0;
            int missingGroundCount = 0;
            float maxStepDy = 0f;
            float maxSlopeDeg = 0f;
            bool hasPrev = false;
            Vector3 prevPos = Vector3.zero;
            float prevY = 0f;

            for (int seg = 0; seg < points.Length - 1; seg++)
            {
                Vector3 a = points[seg];
                Vector3 b = points[seg + 1];
                float segLenXZ = Vector2.Distance(new Vector2(a.x, a.z), new Vector2(b.x, b.z));
                int steps = Mathf.Max(1, Mathf.CeilToInt(segLenXZ / StepSpacing));
                int startK = seg == 0 ? 0 : 1;
                for (int k = startK; k <= steps; k++)
                {
                    float t = (float)k / steps;
                    Vector3 p = Vector3.Lerp(a, b, t);
                    totalSamples++;

                    if (!area.Contains(p))
                    {
                        outsideCount++;
                    }

                    if (!TrySurfaceY(p, includeFootpaths: true, out float y))
                    {
                        missingGroundCount++;
                        hasPrev = false;
                        continue;
                    }

                    if (hasPrev)
                    {
                        float dxz = Vector2.Distance(new Vector2(prevPos.x, prevPos.z), new Vector2(p.x, p.z));
                        float dy = Mathf.Abs(y - prevY);
                        if (dy > maxStepDy)
                        {
                            maxStepDy = dy;
                        }

                        if (dxz > 1e-4f)
                        {
                            float slopeDeg = Mathf.Atan2(dy, dxz) * Mathf.Rad2Deg;
                            if (slopeDeg > maxSlopeDeg)
                            {
                                maxSlopeDeg = slopeDeg;
                            }
                        }
                    }

                    prevPos = p;
                    prevY = y;
                    hasPrev = true;
                }
            }

            if (outsideCount > 0 || missingGroundCount > 0 || maxStepDy > 0.28f)
            {
                Debug.LogError($"[VillageSceneSetup] Continuity failure on {label}: samples={totalSamples}, outside={outsideCount}, missingGround={missingGroundCount}, maxStepDy={maxStepDy:F3}m, maxSlope={maxSlopeDeg:F1}deg");
            }

            return $"  Continuity {label}: samples={totalSamples}, outside={outsideCount}, missingGround={missingGroundCount}, maxStepDy={maxStepDy:F3}m, maxSlope={maxSlopeDeg:F1}deg";
        }

        private static void RenderPreviewFrames(Camera cam)
        {
            try
            {
                ShaderUtil.allowAsyncCompilation = false;
                DynamicGI.UpdateEnvironment();
                string previewDir = Path.GetFullPath(Path.Combine(Application.dataPath, "../Docs/preview"));
                Directory.CreateDirectory(previewDir);

                Vector3 keepPos = cam.transform.position;
                Quaternion keepRot = cam.transform.rotation;
                float keepFov = cam.fieldOfView;

                // First batch render in a fresh Unity session can come out untextured; warm up before saving screenshots.
                Shot(cam, null);
                Shot(cam, Path.Combine(previewDir, "village_start.png"));

                void LookShot(string fileName, Vector3 pos, Vector3 target, float fov = 46f)
                {
                    cam.transform.position = pos;
                    cam.transform.LookAt(target);
                    cam.fieldOfView = fov;
                    Shot(cam, Path.Combine(previewDir, fileName));
                }

                LookShot("village_road.png",
                    new Vector3(392f, SurfaceY(new Vector3(392f, 0f, 298f), true) + 4.5f, 298f),
                    new Vector3(402f, SurfaceY(new Vector3(402f, 0f, 330f), true) + 1.0f, 330f));

                LookShot("village_beach.png",
                    new Vector3(415f, SurfaceY(new Vector3(415f, 0f, 312f), true) + 4.5f, 312f),
                    new Vector3(434f, -87.5f, 335f));

                LookShot("village_shore.png",
                    new Vector3(427f, -83f, 338f),
                    new Vector3(441f, -89.2f, 350f), 50f);

                LookShot("village_overview.png",
                    new Vector3(355f, -55f, 255f),
                    new Vector3(412f, -84f, 345f), 42f);

                cam.transform.SetPositionAndRotation(keepPos, keepRot);
                cam.fieldOfView = keepFov;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[VillageSceneSetup] preview frames failed: " + e.Message);
            }
        }

        private static void ChainCarExitToVillage()
        {
            var carExitScene = EditorSceneManager.OpenScene(CarExitScenePath, OpenSceneMode.Single);
            var exitDirector = UnityEngine.Object.FindAnyObjectByType<IntroCarExit>();
            if (exitDirector != null && exitDirector.NextSceneName != SceneName)
            {
                exitDirector.NextSceneName = SceneName;
                EditorUtility.SetDirty(exitDirector);
                EditorSceneManager.MarkSceneDirty(carExitScene);
                EditorSceneManager.SaveScene(carExitScene);
            }
        }

        private static float SurfaceY(Vector3 p, bool includeFootpaths)
        {
            TrySurfaceY(p, includeFootpaths, out float y);
            return y;
        }

        private static bool TrySurfaceY(Vector3 p, bool includeFootpaths, out float surfaceY)
        {
            RaycastHit[] hits = Physics.RaycastAll(new Vector3(p.x, 40f, p.z), Vector3.down, 220f,
                Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);

            float bestGround = float.NegativeInfinity;
            float bestAny = float.NegativeInfinity;
            foreach (RaycastHit hit in hits)
            {
                if (hit.collider == null)
                {
                    continue;
                }

                string n = hit.collider.gameObject.name;
                bool isGround = GroundObjectNames.Contains(n) || (includeFootpaths && n.StartsWith("Footpath_"));
                if (isGround && hit.point.y > bestGround)
                {
                    bestGround = hit.point.y;
                }

                if (hit.point.y > bestAny)
                {
                    bestAny = hit.point.y;
                }
            }

            if (!float.IsNegativeInfinity(bestGround))
            {
                surfaceY = bestGround;
                return true;
            }

            if (!float.IsNegativeInfinity(bestAny))
            {
                surfaceY = bestAny;
                return true;
            }

            surfaceY = p.y;
            return false;
        }

        private static AnimationClip LoadIdleClip(string characterName)
        {
            string fbxPath = $"Assets/Characters/{characterName}/{characterName}.fbx";
            foreach (AnimationClip clip in AssetDatabase.LoadAllAssetsAtPath(fbxPath).OfType<AnimationClip>())
            {
                if (clip.name.StartsWith("__preview__"))
                {
                    continue;
                }

                int pipe = clip.name.IndexOf('|');
                string shortName = pipe >= 0 && pipe + 1 < clip.name.Length ? clip.name.Substring(pipe + 1) : clip.name;
                if (shortName == "Idle")
                {
                    return clip;
                }
            }

            return null;
        }

        private static T GetOrAdd<T>(GameObject target) where T : Component
        {
            return target.TryGetComponent(out T component) ? component : target.AddComponent<T>();
        }

        private static void Shot(Camera cam, string file)
        {
            var rt = new RenderTexture(1100, 620, 24, RenderTextureFormat.ARGB32);
            cam.targetTexture = rt;
            cam.Render();
            if (!string.IsNullOrEmpty(file))
            {
                RenderTexture.active = rt;
                var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
                tex.Apply();
                File.WriteAllBytes(file, tex.EncodeToPNG());
                RenderTexture.active = null;
                UnityEngine.Object.DestroyImmediate(tex);
            }

            cam.targetTexture = null;
            UnityEngine.Object.DestroyImmediate(rt);
        }
    }
}
