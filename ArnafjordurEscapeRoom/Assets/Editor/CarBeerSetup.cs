using System.IO;
using System.Linq;
using Arnafjordur.Characters;
using Arnafjordur.Intro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Arnafjordur.Editor
{
    /// <summary>
    /// Puts a beer bottle in the right hand of each player seated in VWGolf_WithPlayers.prefab and adds the
    /// <see cref="BeerDrinker"/> that makes them sip it. Edits the prefab in place, so every scene using it follows.
    /// </summary>
    public static class CarBeerSetup
    {
        private const string PrefabPath = "Assets/Vehicles/Golf/VWGolf_WithPlayers.prefab";
        private const string Dir = "Assets/Props/Beer";

        // name, seconds between sips, sip offset, hold the beer away from the wheel between sips
        private static readonly (string name, float period, float offset, bool driver)[] Players =
        {
            ("Leivur", 9f, 6.5f, true),
            ("Andrias", 7.5f, 4.5f, false),
            ("Olavur", 8.5f, 2f, false),
        };

        [MenuItem("Tools/Vehicles/Add Beers To Players")]
        public static void Build()
        {
            if (AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath) == null)
            {
                Debug.LogError("[CarBeerSetup] " + PrefabPath + " missing - run Tools/Vehicles/Rebuild VW Golf With Players first");
                return;
            }
            Directory.CreateDirectory(Dir);
            AssetDatabase.Refresh();
            var lit = Shader.Find("Universal Render Pipeline/Lit");
            var glass = Colour("Mat_Beer_Glass", lit, "#4A2A0C", 0.88f);
            var label = Colour("Mat_Beer_Label", lit, "#E9DDB0", 0.3f);
            var cap = Colour("Mat_Beer_Cap", lit, "#C9A227", 0.7f, 1f);
            AssetDatabase.SaveAssets();

            var root = PrefabUtility.LoadPrefabContents(PrefabPath);
            var report = "[CarBeerSetup] OK";
            try
            {
                foreach (var p in Players)
                {
                    Transform occ = root.transform.Find($"Occupants/{p.name}_Seated");
                    if (occ == null)
                    {
                        Debug.LogError($"[CarBeerSetup] {p.name}_Seated not found in the prefab");
                        return;
                    }
                    Transform Bone(string n) => occ.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == n);
                    Transform upper = Bone("RightUpperArm"), lower = Bone("RightLowerArm"), hand = Bone("RightHand"), head = Bone("Head");
                    Transform thigh = Bone("RightUpperLeg"), knee = Bone("RightLowerLeg");
                    if (upper == null || lower == null || hand == null || head == null)
                    {
                        Debug.LogError($"[CarBeerSetup] {p.name}: arm or head bones not found");
                        return;
                    }

                    foreach (var old in occ.GetComponentsInChildren<Transform>(true).Where(t => t.name == "Beer").ToArray())
                        Object.DestroyImmediate(old.gameObject);
                    foreach (var old in occ.GetComponents<BeerDrinker>())
                        Object.DestroyImmediate(old);

                    Vector3 fist = CharacterItemHolder.ComputeFistCentre(occ, hand, lower);
                    Transform beer = MakeBottle(glass, label, cap);
                    beer.SetPositionAndRotation(hand.TransformPoint(fist), occ.rotation);
                    beer.SetParent(hand, true);
                    var pickup = beer.gameObject.AddComponent<PickupItem>();
                    pickup.Configure("Beer", 0.02f, 0.075f, true, "Drink", true);
                    pickup.SetCollidersEnabled(false);

                    // Between sips the bottle stands on the thigh, close to the body, with the elbow bent.
                    bool hasRest = thigh != null && knee != null;
                    Vector3 rest = Vector3.zero;
                    if (hasRest)
                        rest = occ.InverseTransformPoint(Vector3.Lerp(thigh.position, knee.position, p.driver ? 0.42f : 0.3f) + occ.up * 0.14f);

                    var drinker = occ.gameObject.AddComponent<BeerDrinker>();
                    drinker.Configure(upper, lower, hand, head, beer, p.period, p.offset, hasRest, rest, fist);
                    report += $"\n  {p.name}: beer in {hand.name}, fist at {fist.ToString("F3")} from the wrist, sip every {p.period}s";
                }
                PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
            AssetDatabase.SaveAssets();
            Debug.Log(report);

            try
            {
                Screenshots();
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("[CarBeerSetup] screenshots failed: " + e.Message);
            }
        }

        /// <summary>A 33 cl brown bottle, origin at the middle of the body, mouth along +Y.</summary>
        private static Transform MakeBottle(Material glass, Material label, Material cap)
        {
            var root = new GameObject("Beer").transform;
            Part("Bottle", root, new Vector3(0f, 0f, 0f), new Vector3(0.062f, 0.075f, 0.062f), glass);
            Part("Shoulder", root, new Vector3(0f, 0.088f, 0f), new Vector3(0.044f, 0.014f, 0.044f), glass);
            Part("Neck", root, new Vector3(0f, 0.125f, 0f), new Vector3(0.028f, 0.035f, 0.028f), glass);
            Part("Label", root, new Vector3(0f, 0.03f, 0f), new Vector3(0.0635f, 0.02f, 0.0635f), label);
            Part("Lip", root, new Vector3(0f, 0.157f, 0f), new Vector3(0.032f, 0.004f, 0.032f), cap);   // foil collar; the cap is off
            return root;
        }

        private static void Part(string name, Transform parent, Vector3 pos, Vector3 scale, Material mat)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            go.name = name;
            Object.DestroyImmediate(go.GetComponent<Collider>());
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            go.transform.localScale = scale;
            go.GetComponent<MeshRenderer>().sharedMaterial = mat;
        }

        private static Material Colour(string name, Shader shader, string hex, float smoothness, float metallic = 0f)
        {
            string path = $"{Dir}/{name}.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                mat = new Material(shader);
                AssetDatabase.CreateAsset(mat, path);
            }
            ColorUtility.TryParseHtmlString(hex, out var c);
            mat.SetColor("_BaseColor", c);
            mat.SetColor("_Color", c);
            mat.SetFloat("_Metallic", metallic);
            mat.SetFloat("_Smoothness", smoothness);
            EditorUtility.SetDirty(mat);
            return mat;
        }

        // -------------------------------------------------------------- screenshots
        private static void Screenshots()
        {
            ShaderUtil.allowAsyncCompilation = false;
            string dir = Path.GetFullPath(Path.Combine(Application.dataPath, "../Docs/preview"));
            Directory.CreateDirectory(dir);
            // Skinned meshes only re-skin once per editor frame, so each pose gets its own fresh scene and car.
            Stage(0f);          // first render of a session can come out untextured; this one is a warm-up
            Shot(Path.Combine(dir, "beer_rest_right.png"), new Vector3(3.2f, 1.7f, 1.8f), new Vector3(0f, 0.8f, -0.4f), 30f);
            foreach (float sip in new[] { 0f, 1f })
            {
                string tag = sip > 0f ? "sip" : "rest";
                var car = Stage(sip);
                Shot(Path.Combine(dir, $"beer_{tag}_right.png"), new Vector3(3.2f, 1.7f, 1.8f), new Vector3(0f, 0.8f, -0.4f), 30f);
                Shot(Path.Combine(dir, $"beer_{tag}_left.png"), new Vector3(-3.4f, 1.5f, 1.4f), new Vector3(0f, 0.8f, -0.4f), 30f);
                foreach (var d in car.GetComponentsInChildren<BeerDrinker>())
                {
                    Transform beer = d.GetComponentsInChildren<Transform>().First(t => t.name == "Beer");
                    float side = d.transform.position.x < -0.1f ? -1f : 1f;      // look in from the nearer side of the car
                    Vector3 from = beer.position + new Vector3(side * 0.75f, 0.3f, 0.55f);
                    Shot(Path.Combine(dir, $"beer_{tag}_close_{d.name.Replace("_Seated", "")}.png"), from, beer.position, 40f);
                }
            }
        }

        private static GameObject Stage(float sip)
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var car = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath));
            foreach (var t in car.GetComponentsInChildren<Transform>(true))
                if (t.name == "Body" || t.name == "Glass" || t.name == "Tailgate" || t.name.StartsWith("Door_")) t.gameObject.SetActive(false);
            foreach (var d in car.GetComponentsInChildren<BeerDrinker>()) d.Pose(sip);

            var lightGo = new GameObject("ShotLight");
            var light = lightGo.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.3f;
            lightGo.transform.rotation = Quaternion.Euler(48f, 150f, 0f);
            var fill = new GameObject("ShotFill").AddComponent<Light>();
            fill.type = LightType.Directional;
            fill.intensity = 0.9f;
            fill.transform.rotation = Quaternion.Euler(25f, -20f, 0f);
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.55f, 0.57f, 0.6f);
            DynamicGI.UpdateEnvironment();
            return car;
        }

        private static void Shot(string file, Vector3 pos, Vector3 target, float fov)
        {
            var go = new GameObject("ShotCam");
            var cam = go.AddComponent<Camera>();
            cam.fieldOfView = fov;
            cam.nearClipPlane = 0.05f;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.62f, 0.68f, 0.74f);
            go.transform.position = pos;
            go.transform.LookAt(target);
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
            Object.DestroyImmediate(go);
            Object.DestroyImmediate(rt);
            Object.DestroyImmediate(tex);
        }
    }
}
