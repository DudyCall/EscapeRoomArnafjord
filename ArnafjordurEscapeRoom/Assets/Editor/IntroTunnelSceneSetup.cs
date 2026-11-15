using System.IO;
using Arnafjordur.Intro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace Arnafjordur.Editor
{
    /// <summary>
    /// Builds Assets/Scenes/Intro_TunnelDrive.unity: the Golf with the three players driving through the
    /// unlit Árnafjørður tunnel for the first 20 seconds of the intro.
    /// </summary>
    public static class IntroTunnelSceneSetup
    {
        private const string ScenePath = "Assets/Scenes/Intro_TunnelDrive.unity";
        private const string CarPath = "Assets/Vehicles/Golf/VWGolf_WithPlayers.prefab";
        private const string TunnelPath = "Assets/Environment/Tunnel/ArnafjordurTunnel.prefab";
        private const string GolfMatDir = "Assets/Vehicles/Golf/Materials";

        [MenuItem("Tools/Intro/Open Tunnel Drive Scene")]
        public static void Open()
        {
            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        }

        [MenuItem("Tools/Intro/Rebuild Tunnel Drive Scene")]
        public static void Build()
        {
            ShaderUtil.allowAsyncCompilation = false;   // so preview frames never show placeholder shaders
            TunnelSetup.Build();    // make sure the tunnel prefab matches the current layout length

            var carPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(CarPath);
            var tunnelPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(TunnelPath);
            if (carPrefab == null || tunnelPrefab == null)
            {
                Debug.LogError("[IntroTunnelSceneSetup] Car or tunnel prefab missing");
                return;
            }

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            // Darkness: no sun, no sky, almost no ambient, black fog swallowing the distance.
            RenderSettings.skybox = null;
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.03f, 0.032f, 0.038f);
            RenderSettings.reflectionIntensity = 0f;
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.Exponential;
            RenderSettings.fogColor = Color.black;
            RenderSettings.fogDensity = 0.022f;

            var tunnel = (GameObject)PrefabUtility.InstantiatePrefab(tunnelPrefab);
            var car = (GameObject)PrefabUtility.InstantiatePrefab(carPrefab);
            car.transform.position = new Vector3(0f, 0f, 10f);

            var golf = car.transform.Find("VWGolf");
            var lights = new GameObject("Lights").transform;
            lights.SetParent(car.transform, false);
            foreach (float x in new[] { -0.58f, 0.58f })
            {
                var l = NewLight(lights, x < 0 ? "Headlight_L" : "Headlight_R", LightType.Spot,
                    new Vector3(x, 0.66f, 2.18f), new Color(1f, 0.90f, 0.74f), 45f, 75f);
                l.transform.localRotation = Quaternion.Euler(5f, x < 0 ? -2f : 2f, 0f);
                l.spotAngle = 78f;
                l.innerSpotAngle = 38f;
                l.shadows = LightShadows.Soft;
            }
            var tail = NewLight(lights, "Taillights", LightType.Spot, new Vector3(0f, 0.9f, -2.2f), new Color(1f, 0.08f, 0.04f), 2.2f, 7f);
            tail.transform.localRotation = Quaternion.Euler(12f, 180f, 0f);
            tail.spotAngle = 150f;
            tail.innerSpotAngle = 90f;
            // Fake bounce of the headlights off the rock, so the car and walls beside it are not pitch black.
            NewLight(lights, "HeadlightBounce", LightType.Point, new Vector3(0f, 2.1f, 6.8f), new Color(1f, 0.88f, 0.72f), 1.5f, 12f);
            NewLight(lights, "DashGlow", LightType.Point, new Vector3(0f, 1.0f, 0.42f), new Color(0.62f, 0.74f, 1f), 0.55f, 1.9f);

            // Lit lamp lenses on this instance only.
            foreach (var part in new[] { "Body", "Tailgate" })   // the tailgate carries the inner tail lamps
            {
                var body = golf != null ? golf.Find(part) : null;
                if (body == null) continue;
                var r = body.GetComponent<MeshRenderer>();
                var mats = r.sharedMaterials;
                for (int i = 0; i < mats.Length; i++)
                {
                    if (mats[i] == null) continue;
                    if (mats[i].name == "Mat_Golf_HeadlightLens") mats[i] = LitVariant(mats[i], new Color(1f, 0.95f, 0.84f));
                    else if (mats[i].name == "Mat_Golf_TaillightLens") mats[i] = LitVariant(mats[i], new Color(0.95f, 0.05f, 0.03f));
                }
                r.sharedMaterials = mats;
                Debug.Log("[IntroTunnelSceneSetup] body materials: " + string.Join(", ", System.Array.ConvertAll(mats, m => m ? m.name + (m.IsKeywordEnabled("_EMISSION") ? "(E)" : "") : "null")));
            }

            var camGo = new GameObject("IntroCamera");
            var cam = camGo.AddComponent<Camera>();
            camGo.AddComponent<AudioListener>();
            camGo.tag = "MainCamera";
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Color.black;
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = 400f;

            var wheels = new Transform[4];
            string[] wheelNames = { "Wheel_FL", "Wheel_FR", "Wheel_RL", "Wheel_RR" };
            for (int i = 0; i < 4; i++) wheels[i] = golf.Find(wheelNames[i]);

            var shots = new[]
            {
                // headlights coming out of the dark
                new IntroTunnelDrive.Shot { startTime = 0f, position = new Vector3(-1.25f, 0.7f, 9.5f), lookAt = new Vector3(0f, 0.8f, 0f), drift = new Vector3(0f, 0.02f, -0.45f), fieldOfView = 38f },
                // alongside, squeezed between car and rock
                new IntroTunnelDrive.Shot { startTime = 5f, position = new Vector3(-1.72f, 1.05f, 3.4f), lookAt = new Vector3(0.1f, 0.95f, -0.3f), drift = new Vector3(0f, 0f, -0.3f), fieldOfView = 46f },
                // from the back seat, looking down the tunnel
                new IntroTunnelDrive.Shot { startTime = 10f, position = new Vector3(0.0f, 1.17f, -0.62f), lookAt = new Vector3(0f, 0.95f, 12f), drift = Vector3.zero, fieldOfView = 62f },
                // through the windscreen at the three of them (dialogue goes here later)
                new IntroTunnelDrive.Shot { startTime = 15f, position = new Vector3(0.0f, 1.24f, 2.35f), lookAt = new Vector3(0f, 1.08f, -0.4f), drift = new Vector3(0f, 0f, -0.04f), fieldOfView = 40f },
            };

            var director = new GameObject("IntroTunnelDrive").AddComponent<IntroTunnelDrive>();
            director.Configure(car.transform, wheels, cam, shots);
            director.NextSceneName = "Intro_TunnelExit";
            director.Evaluate(0f);
            EditorUtility.SetDirty(director);

            Directory.CreateDirectory(Path.GetDirectoryName(ScenePath));
            IntroMusicSetup.AddToOpenScene();
            EditorSceneManager.SaveScene(scene, ScenePath);
            AssetDatabase.SaveAssets();
            Debug.Log($"[IntroTunnelSceneSetup] OK saved {ScenePath} (duration {director.Duration}s)");

            try
            {
                string dir = Path.GetFullPath(Path.Combine(Application.dataPath, "../Docs/preview"));
                Directory.CreateDirectory(dir);
                foreach (float t in new[] { 2.5f, 7.5f, 12.5f, 17.5f })
                {
                    director.Evaluate(t);
                    Shot(cam, Path.Combine(dir, $"intro_tunnel_{t:00.0}s.png".Replace(',', '.')));
                }
                director.Evaluate(0f);
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("[IntroTunnelSceneSetup] preview frames failed: " + e.Message);
            }
        }

        private static Light NewLight(Transform parent, string name, LightType type, Vector3 pos, Color color, float intensity, float range)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            var l = go.AddComponent<Light>();
            l.type = type;
            l.color = color;
            l.intensity = intensity;
            l.range = range;
            return l;
        }

        /// <summary>Self-lit (unlit) copy of a lamp lens material, so the lamps glow in the dark without relying on bloom.</summary>
        private static Material LitVariant(Material source, Color glow)
        {
            string path = $"{GolfMatDir}/{source.name}_On.mat";
            var unlit = Shader.Find("Universal Render Pipeline/Unlit");
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                mat = new Material(unlit);
                AssetDatabase.CreateAsset(mat, path);
            }
            mat.shader = unlit;
            mat.SetColor("_BaseColor", glow);
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
