using System.Collections.Generic;
using System.IO;
using System.Linq;
using Arnafjordur.Core;
using Arnafjordur.Intro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace Arnafjordur.Editor
{
    /// <summary>
    /// Builds Assets/Scenes/Intro_TunnelExit.unity: the car leaves the tunnel, turns right and heads down
    /// towards Árnafjørður, filmed from a fixed roadside camera outside the portal.
    /// </summary>
    public static class IntroTunnelExitSceneSetup
    {
        private const string ScenePath = "Assets/Scenes/Intro_TunnelExit.unity";
        private const string TunnelScenePath = "Assets/Scenes/Intro_TunnelDrive.unity";
        private const string ChurchArrivalScenePath = "Assets/Scenes/Intro_ChurchArrival.unity";
        private const string Dir = "Assets/Environment/TunnelExit";
        private const string MatDir = Dir + "/Materials";
        private const string TunnelDir = "Assets/Environment/Tunnel";
        private const string CarPath = "Assets/Vehicles/Golf/VWGolf_WithPlayers.prefab";

        [System.Serializable]
        private class PathFile
        {
            public Vector3[] points;
        }

        [MenuItem("Tools/Intro/Open Tunnel Exit Scene")]
        public static void Open()
        {
            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        }

        [MenuItem("Tools/Intro/Rebuild Tunnel Exit Scene")]
        public static void Build()
        {
            IntroTunnelSceneSetup.Build();              // keeps the hand-over from the tunnel scene in sync
            ShaderUtil.allowAsyncCompilation = false;   // so preview frames never show placeholder shaders
            var lit = Shader.Find("Universal Render Pipeline/Lit");
            Directory.CreateDirectory(MatDir);
            AssetDatabase.Refresh();

            var detailImporter = AssetImporter.GetAtPath($"{Dir}/Textures/Exit_Terrain_Detail.png") as TextureImporter;
            if (detailImporter != null && detailImporter.sRGBTexture)
            {
                detailImporter.sRGBTexture = false;
                detailImporter.SaveAndReimport();
            }

            var houseDetailImporter = AssetImporter.GetAtPath($"{Dir}/Textures/Exit_House_Detail.png") as TextureImporter;
            if (houseDetailImporter != null && (houseDetailImporter.sRGBTexture || houseDetailImporter.wrapMode != TextureWrapMode.Repeat))
            {
                houseDetailImporter.sRGBTexture = false;
                houseDetailImporter.wrapMode = TextureWrapMode.Repeat;
                houseDetailImporter.SaveAndReimport();
            }

            var signsTex = AssetDatabase.LoadAssetAtPath<Texture2D>($"{Dir}/Textures/Exit_Signs_Albedo.png");
            if (signsTex != null)
            {
                var signImporter = AssetImporter.GetAtPath($"{Dir}/Textures/Exit_Signs_Albedo.png") as TextureImporter;
                if (signImporter != null && signImporter.wrapMode != TextureWrapMode.Clamp)
                {
                    signImporter.wrapMode = TextureWrapMode.Clamp;
                    signImporter.SaveAndReimport();
                }
            }

            // ---- materials
            var remap = new Dictionary<string, Material>();
            var terrainMat = Colour("Mat_Exit_Terrain", lit, Color.white, 0.08f);
            var albedo = AssetDatabase.LoadAssetAtPath<Texture2D>($"{Dir}/Textures/Exit_Terrain_Albedo.png");
            terrainMat.SetTexture("_BaseMap", albedo);
            terrainMat.SetTexture("_MainTex", albedo);
            terrainMat.SetTexture("_DetailAlbedoMap", AssetDatabase.LoadAssetAtPath<Texture2D>($"{Dir}/Textures/Exit_Terrain_Detail.png"));
            terrainMat.SetTextureScale("_DetailAlbedoMap", new Vector2(350f, 350f));
            terrainMat.SetFloat("_DetailAlbedoMapScale", 1f);
            terrainMat.EnableKeyword("_DETAIL_MULX2");
            remap["ExitTerrain"] = terrainMat;
            remap["TunnelRoad"] = AssetDatabase.LoadAssetAtPath<Material>($"{TunnelDir}/Materials/Mat_Tunnel_Road.mat");
            var portalMat = Colour("Mat_Exit_PortalConcrete", lit, Color.white, 0.12f);
            var portalTex = AssetDatabase.LoadAssetAtPath<Texture2D>($"{Dir}/Textures/Exit_Portal_Albedo.png");
            portalMat.SetTexture("_BaseMap", portalTex);
            portalMat.SetTexture("_MainTex", portalTex);
            remap["PortalConcrete"] = portalMat;
            remap["BasaltRock"] = Colour("Mat_Exit_BasaltRock", lit, Hex("#2A3036"), 0.10f);
            remap["TunnelDarkness"] = AssetDatabase.LoadAssetAtPath<Material>($"{TunnelDir}/Materials/Mat_Tunnel_Darkness.mat");
            remap["FjordWater"] = FjordWaterSetup.Apply(Colour("Mat_Exit_FjordWater", lit, Hex("#22343F"), 0.93f));
            remap["RiverWater"] = Colour("Mat_Exit_RiverWater", lit, Hex("#4A6E82"), 0.85f);
            var signMat = Colour("Mat_Exit_RoadSign", lit, Color.white, 0.35f);
            signMat.SetTexture("_BaseMap", signsTex);
            signMat.SetTexture("_MainTex", signsTex);
            remap["RoadSign"] = signMat;
            var guardMat = Colour("Mat_Exit_GuardrailMetal", lit, Hex("#8E9499"), 0.55f);
            guardMat.SetFloat("_Metallic", 0.72f);
            remap["GuardrailMetal"] = guardMat;
            remap["HouseBlack"] = Colour("Mat_Exit_HouseBlack", lit, Hex("#1B1D20"), 0.22f);
            remap["HouseWhite"] = Colour("Mat_Exit_HouseWhite", lit, Hex("#ECEFF2"), 0.24f);
            remap["HouseRed"] = Colour("Mat_Exit_HouseRed", lit, Hex("#6E221F"), 0.24f);
            remap["HouseGrey"] = Colour("Mat_Exit_HouseGrey", lit, Hex("#D6DADF"), 0.16f);
            remap["HouseGrey"].SetTexture("_DetailAlbedoMap", null);
            remap["HouseGrey"].DisableKeyword("_DETAIL_MULX2");
            EditorUtility.SetDirty(remap["HouseGrey"]);
            remap["HouseTeal"] = Colour("Mat_Exit_HouseTeal", lit, Hex("#5B8A82"), 0.25f);
            remap["HouseYellow"] = Colour("Mat_Exit_HouseYellow", lit, Hex("#CFA038"), 0.25f);
            remap["HouseDoor"] = Colour("Mat_Exit_HouseDoor", lit, Hex("#244632"), 0.35f);
            remap["HouseTimber"] = Colour("Mat_Exit_HouseTimber", lit, Hex("#5C4E43"), 0.18f);
            remap["HouseWindow"] = Colour("Mat_Exit_HouseWindow", lit, Hex("#1C2B36"), 0.85f);
            var litWinMat = Colour("Mat_Exit_HouseWindowLit", lit, Palette.LanternAmber, 0.80f);
            litWinMat.EnableKeyword("_EMISSION");
            litWinMat.SetColor("_EmissionColor", Palette.LanternAmber * 0.85f);
            EditorUtility.SetDirty(litWinMat);
            remap["HouseWindowLit"] = litWinMat;
            remap["RoofTurf"] = Colour("Mat_Exit_RoofTurf", lit, Hex("#345645"), 0.22f);
            remap["RoofRed"] = Colour("Mat_Exit_RoofRed", lit, Hex("#7D2E24"), 0.32f);
            remap["RoofGrey"] = Colour("Mat_Exit_RoofGrey", lit, Hex("#484D54"), 0.42f);
            remap["RoofBlack"] = Colour("Mat_Exit_RoofBlack", lit, Hex("#202428"), 0.34f);

            var houseDetailTex = AssetDatabase.LoadAssetAtPath<Texture2D>($"{Dir}/Textures/Exit_House_Detail.png");
            foreach (var key in new[] { "HouseBlack", "HouseWhite", "HouseRed", "HouseTeal", "HouseYellow", "HouseTimber", "RoofRed", "RoofGrey", "RoofBlack", "RoofTurf" })
            {
                if (houseDetailTex == null) break;
                var m = remap[key];
                m.SetTexture("_DetailAlbedoMap", houseDetailTex);
                m.SetTextureScale("_DetailAlbedoMap", Vector2.one);
                m.SetFloat("_DetailAlbedoMapScale", key.StartsWith("Roof") ? 0.50f : 0.70f);
                m.EnableKeyword("_DETAIL_MULX2");
                EditorUtility.SetDirty(m);
            }

            var importer = AssetImporter.GetAtPath($"{Dir}/TunnelExit.fbx") as ModelImporter;
            if (importer == null)
            {
                Debug.LogError("[IntroTunnelExitSceneSetup] TunnelExit.fbx missing");
                return;
            }
            foreach (var kv in remap)
                importer.AddRemap(new AssetImporter.SourceAssetIdentifier(typeof(Material), kv.Key), kv.Value);
            importer.importAnimation = false;
            importer.animationType = ModelImporterAnimationType.None;
            importer.importCameras = false;
            importer.importLights = false;
            importer.SaveAndReimport();

            // Shaded copies of the bore materials: the inside of the mountain should read as dark from outside.
            var rockShade = Shade("Mat_Tunnel_Rock", 0.16f);
            var roadShade = Shade("Mat_Tunnel_Road", 0.30f);
            AssetDatabase.SaveAssets();

            // ---- scene
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var sunGo = new GameObject("Sun");
            var sun = sunGo.AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.color = Hex("#FFE6C4");
            sun.intensity = 1.25f;
            sun.shadows = LightShadows.Soft;
            sunGo.transform.rotation = Quaternion.Euler(38f, 140f, 0f);

            var sky = AssetDatabase.LoadAssetAtPath<Material>($"{MatDir}/Mat_Exit_Sky.mat");
            var skyShader = Shader.Find("Skybox/Procedural");
            if (sky == null && skyShader != null)
            {
                sky = new Material(skyShader);
                AssetDatabase.CreateAsset(sky, $"{MatDir}/Mat_Exit_Sky.mat");
            }
            if (sky != null)
            {
                sky.SetColor("_SkyTint", Hex("#7B8E9C"));
                sky.SetColor("_GroundColor", Hex("#B4C2C9"));
                sky.SetFloat("_AtmosphereThickness", 1.25f);
                sky.SetFloat("_Exposure", 1.05f);
                sky.SetFloat("_SunSize", 0.03f);
                EditorUtility.SetDirty(sky);
                RenderSettings.skybox = sky;
            }
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

            var world = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>($"{Dir}/TunnelExit.fbx"));
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
            RiverFit.Apply(world, -89.2f, "TunnelExit"); // the Stórá as one unbroken stream

            // A stretch of bore behind the portal, closed off by darkness.
            var boreRoot = new GameObject("TunnelBore").transform;
            var straight = AssetDatabase.LoadAssetAtPath<GameObject>($"{TunnelDir}/TunnelStraight.fbx");
            for (int i = 1; i <= 2; i++)
            {
                var piece = (GameObject)PrefabUtility.InstantiatePrefab(straight, boreRoot);
                piece.name = $"Bore_{i}";
                piece.transform.position = new Vector3(0f, 0f, -24f * i);
                foreach (var r in piece.GetComponentsInChildren<MeshRenderer>())
                {
                    var mats = r.sharedMaterials;
                    for (int m = 0; m < mats.Length; m++)
                        mats[m] = mats[m] != null && mats[m].name.Contains("Road") ? roadShade : rockShade;
                    r.sharedMaterials = mats;
                    r.shadowCastingMode = ShadowCastingMode.TwoSided;
                }
            }
            var plug = GameObject.CreatePrimitive(PrimitiveType.Quad);
            Object.DestroyImmediate(plug.GetComponent<Collider>());
            plug.name = "Darkness";
            plug.transform.SetParent(boreRoot, false);
            plug.transform.position = new Vector3(0f, 3f, -47.5f);
            plug.transform.rotation = Quaternion.Euler(0f, 180f, 0f);
            plug.transform.localScale = new Vector3(16f, 10f, 1f);
            plug.GetComponent<MeshRenderer>().sharedMaterial = AssetDatabase.LoadAssetAtPath<Material>($"{TunnelDir}/Materials/Mat_Tunnel_Darkness.mat");
            plug.GetComponent<MeshRenderer>().shadowCastingMode = ShadowCastingMode.Off;

            // ---- car
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

            var pathAsset = File.ReadAllText($"{Dir}/TunnelExitPath.json");
            Vector3[] path = JsonUtility.FromJson<PathFile>(pathAsset).points;

            Physics.SyncTransforms();
            float GroundY(float gx, float gz, float fallback)
            {
                return Physics.Raycast(new Vector3(gx, 250f, gz), Vector3.down, out var hit, 600f)
                    ? hit.point.y
                    : fallback;
            }

            var shot1Pos = new Vector3(8.0f, Mathf.Max(1.55f, GroundY(8.0f, 38.0f, -1.0f) + 2.2f), 38.0f);
            var shot2Pos = new Vector3(-6.0f, Mathf.Max(19.5f, GroundY(-6.0f, -12.0f, 12.0f) + 7.5f), -12.0f);
            var shot3Pos = new Vector3(-12.0f, Mathf.Max(14.5f, GroundY(-12.0f, -5.0f, 8.0f) + 6.0f), -5.0f);
            var shot4Pos = new Vector3(38.0f, GroundY(38.0f, 14.0f, -1.0f) + 7.2f, 14.0f);

            var shots = new[]
            {
                new IntroPathDrive.Shot
                {
                    startTime = 0f,
                    position = shot1Pos,
                    lookOffset = new Vector3(0f, 1.0f, 0f),
                    lookLead = 1.5f,
                    framingHeight = 11.5f,
                    minFieldOfView = 24f,
                    maxFieldOfView = 42f,
                },
                new IntroPathDrive.Shot
                {
                    startTime = 2.6f,
                    position = shot2Pos,
                    lookOffset = new Vector3(-24f, 0.2f, 12f),
                    lookLead = 1.5f,
                    framingHeight = 46f,
                    minFieldOfView = 44f,
                    maxFieldOfView = 50f,
                },
                new IntroPathDrive.Shot
                {
                    startTime = 4.4f,
                    position = shot3Pos,
                    lookOffset = new Vector3(26f, -10f, 34f),
                    lookLead = 18f,
                    framingHeight = 28f,
                    minFieldOfView = 30f,
                    maxFieldOfView = 42f,
                },
                new IntroPathDrive.Shot
                {
                    startTime = 6.6f,
                    position = shot4Pos,
                    lookOffset = new Vector3(14f, -4.5f, 18f),
                    lookLead = 12f,
                    framingHeight = 16f,
                    minFieldOfView = 28f,
                    maxFieldOfView = 40f,
                },
            };

            var wheels = new[] { "Wheel_FL", "Wheel_FR", "Wheel_RL", "Wheel_RR" }.Select(n => golf.Find(n)).ToArray();
            var director = new GameObject("IntroPathDrive").AddComponent<IntroPathDrive>();
            director.ConfigureShots(car.transform, wheels, cam, path, 9.4f, 13.5f, shots);
            director.NextSceneName = "Intro_ChurchArrival";
            director.Evaluate(0f);
            EditorUtility.SetDirty(director);

            IntroMusicSetup.AddToOpenScene();
            EditorSceneManager.SaveScene(scene, ScenePath);

            // All intro scenes need to be in the build list for the hand-over between them to work.
            var buildScenes = EditorBuildSettings.scenes.ToList();
            foreach (var p in new[] { TunnelScenePath, ScenePath, ChurchArrivalScenePath })
                if (File.Exists(p) && buildScenes.All(s => s.path != p))
                    buildScenes.Add(new EditorBuildSettingsScene(p, true));
            EditorBuildSettings.scenes = buildScenes.ToArray();
            AssetDatabase.SaveAssets();
            Debug.Log($"[IntroTunnelExitSceneSetup] OK saved {ScenePath} (duration {director.Duration}s, camera {shot1Pos}, path points {path.Length})");

            try
            {
                string dir = Path.GetFullPath(Path.Combine(Application.dataPath, "../Docs/preview"));
                Directory.CreateDirectory(dir);
                foreach (float t in new[] { 0.8f, 2.2f, 3.4f, 4.8f, 6.2f, 7.6f, 9.0f })
                {
                    director.Evaluate(t);
                    Shot(cam, Path.Combine(dir, $"intro_exit_{Mathf.RoundToInt(t * 10f):000}.png"));
                }
                director.Evaluate(0f);
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("[IntroTunnelExitSceneSetup] preview frames failed: " + e.Message);
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

        private static Material Shade(string sourceName, float brightness)
        {
            var source = AssetDatabase.LoadAssetAtPath<Material>($"{TunnelDir}/Materials/{sourceName}.mat");
            string path = $"{MatDir}/{sourceName}_Shade.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                mat = new Material(source);
                AssetDatabase.CreateAsset(mat, path);
            }
            mat.CopyPropertiesFromMaterial(source);
            var c = new Color(brightness, brightness, brightness, 1f);
            mat.SetColor("_BaseColor", c);
            mat.SetColor("_Color", c);
            // No sky reflections or highlights inside the mountain.
            mat.SetFloat("_Smoothness", 0f);
            mat.SetFloat("_EnvironmentReflections", 0f);
            mat.EnableKeyword("_ENVIRONMENTREFLECTIONS_OFF");
            mat.SetFloat("_SpecularHighlights", 0f);
            mat.EnableKeyword("_SPECULARHIGHLIGHTS_OFF");
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
