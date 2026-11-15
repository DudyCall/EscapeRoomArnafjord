using System.Collections.Generic;
using System.IO;
using System.Linq;
using Arnafjordur.Intro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace Arnafjordur.Editor
{
    /// <summary>
    /// Builds Assets/Scenes/Intro_ChurchArrival.unity: the final intro driving scene where the blue VW Golf
    /// winds down the Árnafjørður switchbacks and smoothly pulls into the churchyard forecourt to park outside Árnafjarðar kirkja.
    /// </summary>
    public static class IntroChurchArrivalSceneSetup
    {
        private const string ScenePath = "Assets/Scenes/Intro_ChurchArrival.unity";
        private const string TunnelScenePath = "Assets/Scenes/Intro_TunnelDrive.unity";
        private const string TunnelExitScenePath = "Assets/Scenes/Intro_TunnelExit.unity";
        private const string ExitDir = "Assets/Environment/TunnelExit";
        private const string ExitMatDir = ExitDir + "/Materials";
        private const string Dir = "Assets/Environment/ChurchArrival";
        private const string MatDir = Dir + "/Materials";
        private const string TunnelDir = "Assets/Environment/Tunnel";
        private const string CarPath = "Assets/Vehicles/Golf/VWGolf_WithPlayers.prefab";

        [System.Serializable]
        private class PathFile
        {
            public Vector3[] points;
            public float stopDistance;
        }

        [MenuItem("Tools/Intro/Open Church Arrival Scene")]
        public static void Open()
        {
            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        }

        [MenuItem("Tools/Intro/Rebuild Church Arrival Scene")]
        public static void Build()
        {
            ShaderUtil.allowAsyncCompilation = false;   // so preview frames never show placeholder shaders
            var lit = Shader.Find("Universal Render Pipeline/Lit");
            Directory.CreateDirectory(MatDir);
            AssetDatabase.Refresh();

            // ---- remap materials on ChurchArea.fbx reusing TunnelExit palette + churchyard materials
            var churchRemap = new Dictionary<string, Material>
            {
                ["TunnelRoad"] = AssetDatabase.LoadAssetAtPath<Material>($"{TunnelDir}/Materials/Mat_Tunnel_Road.mat"),
                ["PortalConcrete"] = AssetDatabase.LoadAssetAtPath<Material>($"{ExitMatDir}/Mat_Exit_PortalConcrete.mat"),
                ["GuardrailMetal"] = AssetDatabase.LoadAssetAtPath<Material>($"{ExitMatDir}/Mat_Exit_GuardrailMetal.mat"),
                ["HouseWhite"] = AssetDatabase.LoadAssetAtPath<Material>($"{ExitMatDir}/Mat_Exit_HouseWhite.mat"),
                ["HouseBlack"] = AssetDatabase.LoadAssetAtPath<Material>($"{ExitMatDir}/Mat_Exit_HouseBlack.mat"),
                ["RoofRed"] = AssetDatabase.LoadAssetAtPath<Material>($"{ExitMatDir}/Mat_Exit_RoofRed.mat"),
                ["HouseWindow"] = AssetDatabase.LoadAssetAtPath<Material>($"{ExitMatDir}/Mat_Exit_HouseWindow.mat"),
                ["ChurchGravel"] = Colour("Mat_Church_Gravel", lit, Hex("#4B4944"), 0.08f),
                ["DryStone"] = Colour("Mat_Church_DryStone", lit, Hex("#3C3E40"), 0.08f),
                ["ChurchDoor"] = Colour("Mat_Church_Door", lit, Hex("#244632"), 0.35f),
                ["ChurchLawn"] = Colour("Mat_Church_Lawn", lit, Hex("#3D5123"), 0.06f),
            };
            var detailTex = AssetDatabase.LoadAssetAtPath<Texture2D>($"{ExitDir}/Textures/Exit_Terrain_Detail.png");
            if (detailTex != null)
            {
                foreach (var k in new[] { "ChurchGravel", "DryStone" })
                {
                    var m = churchRemap[k];
                    m.SetTexture("_DetailAlbedoMap", detailTex);
                    m.SetTextureScale("_DetailAlbedoMap", new Vector2(12f, 12f));
                    m.SetFloat("_DetailAlbedoMapScale", 0.9f);
                    m.EnableKeyword("_DETAIL_MULX2");
                }
            }

            var churchImporter = AssetImporter.GetAtPath($"{Dir}/ChurchArea.fbx") as ModelImporter;
            if (churchImporter == null)
            {
                Debug.LogError("[IntroChurchArrivalSceneSetup] ChurchArea.fbx missing");
                return;
            }
            foreach (var kv in churchRemap)
            {
                if (kv.Value != null)
                    churchImporter.AddRemap(new AssetImporter.SourceAssetIdentifier(typeof(Material), kv.Key), kv.Value);
            }
            churchImporter.importAnimation = false;
            churchImporter.animationType = ModelImporterAnimationType.None;
            churchImporter.importCameras = false;
            churchImporter.importLights = false;
            churchImporter.SaveAndReimport();
            AssetDatabase.SaveAssets();

            // ---- scene + atmosphere matching Intro_TunnelExit seamlessly
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var sunGo = new GameObject("Sun");
            var sun = sunGo.AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.color = Hex("#FFE6C4");
            sun.intensity = 1.25f;
            sun.shadows = LightShadows.Soft;
            sunGo.transform.rotation = Quaternion.Euler(38f, 140f, 0f);

            var sky = AssetDatabase.LoadAssetAtPath<Material>($"{ExitMatDir}/Mat_Exit_Sky.mat");
            if (sky != null) RenderSettings.skybox = sky;
            RenderSettings.sun = sun;
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = Hex("#7B8E9C");
            RenderSettings.ambientEquatorColor = Hex("#8B979D");
            RenderSettings.ambientGroundColor = Hex("#3B4038");
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.Exponential;
            RenderSettings.fogColor = Hex("#B4C2C9");
            RenderSettings.fogDensity = 0.00058f;
            EveningLook.Apply(sun); // the story is set in the evening: overrides the daylight values above

            // ---- reuse full Árnafjørður valley & village backdrop from TunnelExit.fbx
            var worldPrefab = AssetDatabase.LoadAssetAtPath<GameObject>($"{ExitDir}/TunnelExit.fbx");
            if (worldPrefab == null)
            {
                Debug.LogError("[IntroChurchArrivalSceneSetup] TunnelExit.fbx missing");
                return;
            }
            var world = (GameObject)PrefabUtility.InstantiatePrefab(worldPrefab);
            world.name = "TunnelExit";
            foreach (var mf in world.GetComponentsInChildren<MeshFilter>())
            {
                mf.gameObject.isStatic = true;
                var r = mf.GetComponent<MeshRenderer>();
                if (mf.name == "Exit_Terrain" || mf.name == "Exit_Road")
                    mf.gameObject.AddComponent<MeshCollider>().sharedMesh = mf.sharedMesh;
                if (mf.name == "Exit_Terrain") r.shadowCastingMode = ShadowCastingMode.TwoSided;
                if (mf.name == "Exit_Water") r.shadowCastingMode = ShadowCastingMode.Off;
            }

            // ---- instantiate close-up churchyard forecourt, church porch/doors/mullions, stone wall & Stórá parapets
            var churchArea = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>($"{Dir}/ChurchArea.fbx"));
            churchArea.name = "ChurchArea";
            foreach (var mf in churchArea.GetComponentsInChildren<MeshFilter>())
            {
                mf.gameObject.isStatic = true;
                if (mf.name == "ChurchForecourt")
                    mf.gameObject.AddComponent<MeshCollider>().sharedMesh = mf.sharedMesh;
            }

            // ---- car + headlights
            var car = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(CarPath));
            var golf = car.transform.Find("VWGolf");
            var lights = new GameObject("Lights").transform;
            lights.SetParent(car.transform, false);
            foreach (float x in new[] { -0.58f, 0.58f })
            {
                var go = new GameObject(x < 0 ? "Headlight_L" : "Headlight_R");
                go.transform.SetParent(lights, false);
                go.transform.localPosition = new Vector3(x, 0.66f, 2.18f);
                go.transform.localRotation = Quaternion.Euler(7f, 0f, 0f);
                var l = go.AddComponent<Light>();
                l.type = LightType.Spot;
                l.color = new Color(1f, 0.92f, 0.78f);
                l.intensity = 1.05f;
                l.range = 32f;
                l.spotAngle = 70f;
                l.innerSpotAngle = 32f;
            }
            foreach (var part in new[] { "Body", "Tailgate" })   // the tailgate carries the inner tail lamps
            {
                var partTf = golf.Find(part);
                if (partTf == null) continue;
                var body = partTf.GetComponent<MeshRenderer>();
                var bodyMats = body.sharedMaterials;
                for (int i = 0; i < bodyMats.Length; i++)
                {
                    if (bodyMats[i] == null) continue;
                    var on = AssetDatabase.LoadAssetAtPath<Material>($"Assets/Vehicles/Golf/Materials/{bodyMats[i].name}_On.mat");
                    if (on != null) bodyMats[i] = on;
                }
                body.sharedMaterials = bodyMats;
            }

            // ---- camera + director
            var camGo = new GameObject("IntroCamera");
            var cam = camGo.AddComponent<Camera>();
            camGo.AddComponent<AudioListener>();
            camGo.tag = "MainCamera";
            cam.nearClipPlane = 0.1f;
            cam.farClipPlane = 4500f;

            var pathFile = JsonUtility.FromJson<PathFile>(File.ReadAllText($"{Dir}/ChurchArrivalPath.json"));
            Vector3[] path = pathFile.points;

            Physics.SyncTransforms();

            // Keep the hillside from poking up through the road and the gravel along the drive.
            var terrainFilter = world.GetComponentsInChildren<MeshFilter>().FirstOrDefault(mf => mf.name == "Exit_Terrain");
            if (terrainFilter != null)
            {
                var surfaces = world.GetComponentsInChildren<MeshCollider>()
                    .Concat(churchArea.GetComponentsInChildren<MeshCollider>())
                    .Where(c => c.name == "Exit_Road" || c.name == "ChurchForecourt")
                    .Cast<Collider>().ToArray();
                int fitted = RoadTerrainFit.Apply(terrainFilter, surfaces, path, 45f, $"{Dir}/Exit_Terrain_ChurchRoadFit.asset");
                Debug.Log($"[IntroChurchArrivalSceneSetup] lowered {fitted} terrain vertices under the road");
                Physics.SyncTransforms();
            }

            RiverFit.Apply(world, -89.2f, "ChurchArrival"); // the Stórá as one unbroken stream

            // Snap waypoints gently onto the actual Exit_Road / ChurchForecourt surface where present so wheels stay flush.
            for (int i = 0; i < path.Length; i++)
            {
                Vector3 wp = path[i];
                var hits = Physics.RaycastAll(new Vector3(wp.x, wp.y + 8f, wp.z), Vector3.down, 18f);
                float bestY = float.NegativeInfinity;
                bool foundSurface = false;
                foreach (var hit in hits)
                {
                    string hName = hit.collider.gameObject.name;
                    if (hName == "ChurchForecourt" || hName == "Exit_Road")
                    {
                        if (hit.point.y > bestY)
                        {
                            bestY = hit.point.y;
                            foundSurface = true;
                        }
                    }
                }
                if (foundSurface) path[i].y = bestY;
            }

            float GroundY(float gx, float gz, float fallback)
            {
                return Physics.Raycast(new Vector3(gx, 120f, gz), Vector3.down, out var hit, 400f)
                    ? hit.point.y
                    : fallback;
            }

            // Shot 1 (0.0 - 4.4s): elevated roadside viewpoint above the switchback straight overlooking village & fjord
            var shot1Pos = new Vector3(524.0f, GroundY(524.0f, 164.0f, -54.0f) + 4.8f, 164.0f);
            // Shot 2 (4.4 - 8.6s): unobstructed slope terrace overlooking lower bend & church as Golf winds into the village
            var shot2Pos = new Vector3(436.0f, GroundY(436.0f, 256.0f, -77.0f) + 5.2f, 256.0f);
            // Shot 3 (8.6 - 15.2s): churchyard viewpoint framing full Golf, gravel bay, stone wall, church facade & spire cross
            var shot3Pos = new Vector3(411.5f, GroundY(411.5f, 292.5f, -82.0f) + 3.8f, 292.5f);

            var shots = new[]
            {
                new IntroPathDrive.Shot
                {
                    startTime = 0f,
                    position = shot1Pos,
                    lookOffset = new Vector3(-6f, -1.5f, 6f),
                    lookLead = 10f,
                    framingHeight = 16f,
                    minFieldOfView = 26f,
                    maxFieldOfView = 38f,
                },
                new IntroPathDrive.Shot
                {
                    startTime = 4.4f,
                    position = shot2Pos,
                    lookOffset = new Vector3(2f, 0.8f, 2f),
                    lookLead = 4f,
                    framingHeight = 14f,
                    minFieldOfView = 26f,
                    maxFieldOfView = 40f,
                },
                new IntroPathDrive.Shot
                {
                    startTime = 8.6f,
                    position = shot3Pos,
                    lookOffset = new Vector3(-4.0f, 4.4f, -3.5f),
                    lookLead = 1.4f,
                    framingHeight = 23f,
                    minFieldOfView = 34f,
                    maxFieldOfView = 50f,
                },
            };

            // Total ~15.2s: startDist=46m -> stopDistance=pathFile.stopDistance, speed=16.5m/s, brake=4.0s, hold=2.4s
            var wheels = new[] { "Wheel_FL", "Wheel_FR", "Wheel_RL", "Wheel_RR" }.Select(n => golf.Find(n)).ToArray();
            var director = new GameObject("IntroPathDrive").AddComponent<IntroPathDrive>();
            float cruiseSecs = Mathf.Max(0.5f, (pathFile.stopDistance - 46f - 0.5f * 16.5f * 4.0f) / 16.5f);
            float totalSecs = cruiseSecs + 4.0f + 2.4f;
            director.ConfigureShots(car.transform, wheels, cam, path, totalSecs, 16.5f, shots,
                startDist: 46f, brakeSecs: 4.0f, holdSecs: 2.4f);
            director.NextSceneName = "Intro_CarExit";
            director.Evaluate(0f);
            EditorUtility.SetDirty(director);

            IntroMusicSetup.AddToOpenScene();
            EditorSceneManager.SaveScene(scene, ScenePath);

            // Ensure all three intro scenes are present in build order.
            var buildScenes = EditorBuildSettings.scenes.ToList();
            foreach (var p in new[] { TunnelScenePath, TunnelExitScenePath, ScenePath })
            {
                if (File.Exists(p) && buildScenes.All(s => s.path != p))
                    buildScenes.Add(new EditorBuildSettingsScene(p, true));
            }
            EditorBuildSettings.scenes = buildScenes.ToArray();
            AssetDatabase.SaveAssets();
            Debug.Log($"[IntroChurchArrivalSceneSetup] OK saved {ScenePath} (duration {director.Duration:F2}s, path {path.Length} pts, stopDistance {pathFile.stopDistance:F1}m)");

            try
            {
                string previewDir = Path.GetFullPath(Path.Combine(Application.dataPath, "../Docs/preview"));
                Directory.CreateDirectory(previewDir);
                foreach (float t in new[] { 1.0f, 3.2f, 5.4f, 7.6f, 9.6f, 11.4f, 13.5f, 15.0f })
                {
                    director.Evaluate(t);
                    Shot(cam, Path.Combine(previewDir, $"intro_church_{Mathf.RoundToInt(t * 10f):000}.png"));
                }
                director.Evaluate(0f);
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("[IntroChurchArrivalSceneSetup] preview frames failed: " + e.Message);
            }
        }

        private static Color Hex(string hex)
        {
            ColorUtility.TryParseHtmlString(hex, out var c);
            return c;
        }

        private static Material Colour(string name, Shader shader, Color color, float smoothness)
        {
            string path = $"{MatDir}/{name}.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                mat = new Material(shader);
                AssetDatabase.CreateAsset(mat, path);
            }
            mat.SetColor("_BaseColor", color);
            mat.SetColor("_Color", color);
            mat.SetFloat("_Metallic", 0f);
            mat.SetFloat("_Smoothness", smoothness);
            EditorUtility.SetDirty(mat);
            return mat;
        }

        private static void Shot(Camera cam, string file)
        {
            var rt = new RenderTexture(1100, 620, 24, RenderTextureFormat.ARGB32);
            cam.targetTexture = rt;
            cam.Render();
            RenderTexture.active = rt;
            var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            tex.Apply();
            File.WriteAllBytes(file, tex.EncodeToPNG());
            RenderTexture.active = null;
            cam.targetTexture = null;
            Object.DestroyImmediate(rt);
            Object.DestroyImmediate(tex);
        }
    }
}
