using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Arnafjordur.Editor
{
    /// <summary>
    /// Seats the three players in the VW Golf: Leivur drives, Andrias rides in the front passenger seat and
    /// Ólavur sits in the back. Produces a seated pose clip + controller per character and a combined prefab.
    /// </summary>
    public static class CarOccupantsSetup
    {
        private const string CarPrefabPath = "Assets/Vehicles/Golf/VWGolf.prefab";
        private const string OutPrefabPath = "Assets/Vehicles/Golf/VWGolf_WithPlayers.prefab";

        private struct Occupant
        {
            public string Name, Seat;
            public bool Driver, Rear;
        }

        private static readonly Occupant[] Occupants =
        {
            new Occupant { Name = "Leivur", Seat = "Seat_Driver", Driver = true },
            new Occupant { Name = "Andrias", Seat = "Seat_FrontPassenger" },
            new Occupant { Name = "Olavur", Seat = "Seat_RearMiddle", Rear = true },
        };

        [MenuItem("Tools/Vehicles/Rebuild VW Golf With Players")]
        public static void Build()
        {
            var carPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(CarPrefabPath);
            if (carPrefab == null)
            {
                Debug.LogError("[CarOccupantsSetup] Car prefab missing - run Tools/Vehicles/Rebuild VW Golf Prefab first");
                return;
            }

            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var root = new GameObject("VWGolf_WithPlayers");
            var car = (GameObject)PrefabUtility.InstantiatePrefab(carPrefab, root.transform);
            var occRoot = new GameObject("Occupants");
            occRoot.transform.SetParent(root.transform, false);
            var report = "[CarOccupantsSetup] OK";

            foreach (var occ in Occupants)
            {
                string fbxPath = $"Assets/Characters/{occ.Name}/{occ.Name}.fbx";
                var fbx = AssetDatabase.LoadAssetAtPath<GameObject>(fbxPath);
                var seat = car.transform.Find(occ.Seat);
                if (fbx == null || seat == null)
                {
                    Debug.LogError($"[CarOccupantsSetup] Missing {fbxPath} or seat {occ.Seat}");
                    return;
                }

                var inst = (GameObject)PrefabUtility.InstantiatePrefab(fbx, occRoot.transform);
                inst.name = occ.Name + "_Seated";
                var mat = AssetDatabase.LoadAssetAtPath<Material>($"Assets/Characters/{occ.Name}/Mat_{occ.Name}.mat");
                if (mat != null)
                    foreach (var smr in inst.GetComponentsInChildren<SkinnedMeshRenderer>())
                        smr.sharedMaterial = mat;

                var b = new Bones(inst.transform);
                if (b.Hips == null)
                {
                    Debug.LogError($"[CarOccupantsSetup] {occ.Name}: Hips bone not found");
                    return;
                }

                PoseBody(b, occ);
                inst.transform.position += seat.position + new Vector3(0f, 0.07f, occ.Rear ? -0.02f : -0.08f) - b.Hips.position;
                PoseArms(b, occ, car.transform);

                var clip = BuildClip(inst.transform, occ);
                var animator = inst.GetComponent<Animator>();
                if (animator == null) animator = inst.AddComponent<Animator>();
                if (animator.avatar == null)
                    animator.avatar = AssetDatabase.LoadAllAssetsAtPath(fbxPath).OfType<Avatar>().FirstOrDefault();
                animator.applyRootMotion = false;
                animator.runtimeAnimatorController = BuildController(occ, clip);

                var head = b.Find("Head");
                report += $"\n  {occ.Name}: root={inst.transform.position} hips={b.Hips.position} head={(head ? head.position : Vector3.zero)}";
            }

            PrefabUtility.SaveAsPrefabAsset(root, OutPrefabPath);
            AssetDatabase.SaveAssets();
            Debug.Log(report);

            try
            {
                Screenshots(car);
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("[CarOccupantsSetup] screenshots failed: " + e.Message);
            }

            CarBeerSetup.Build();       // the rebuilt prefab starts without beers; put them back
        }

        // ------------------------------------------------------------------ posing
        private sealed class Bones
        {
            private readonly Dictionary<string, Transform> _map = new Dictionary<string, Transform>();
            public readonly Transform Hips;

            public Bones(Transform root)
            {
                foreach (var t in root.GetComponentsInChildren<Transform>(true))
                    if (!_map.ContainsKey(t.name)) _map[t.name] = t;
                Hips = Find("Hips");
            }

            public Transform Find(string name) => _map.TryGetValue(name, out var t) ? t : null;
        }

        private static void RotW(Transform t, Vector3 axis, float deg)
        {
            if (t != null) t.rotation = Quaternion.AngleAxis(deg, axis) * t.rotation;
        }

        /// <summary>Angles are about the character's right axis: negative tips "up" backwards / swings a hanging limb forwards.</summary>
        private static void PoseBody(Bones b, Occupant occ)
        {
            Vector3 right = Vector3.right, up = Vector3.up;
            const float lean = 18f;
            float thigh = occ.Rear ? -108f : -105f;      // from hanging straight down
            float shin = occ.Driver ? -56f : occ.Rear ? -48f : -50f;

            RotW(b.Hips, right, -lean);
            RotW(b.Find("Spine"), right, 4f);
            RotW(b.Find("Chest"), right, 4f);
            RotW(b.Find("Neck"), right, 6f);
            RotW(b.Find("Head"), right, 4f);

            foreach (var side in new[] { "Left", "Right" })
            {
                var upper = b.Find(side + "UpperLeg");
                var lower = b.Find(side + "LowerLeg");
                var foot = b.Find(side + "Foot");
                if (upper == null || lower == null) continue;
                float sx = Mathf.Sign(upper.position.x - b.Hips.position.x);
                RotW(upper, right, thigh + lean);
                RotW(upper, up, sx * (occ.Rear ? 5f : 9f));          // knees slightly apart
                RotW(lower, right, shin - thigh);
                RotW(foot, right, -shin * 0.75f);
            }
        }

        private static void PoseArms(Bones b, Occupant occ, Transform car)
        {
            var wheel = car.Find("SteeringWheel");
            foreach (var side in new[] { "Left", "Right" })
            {
                var upper = b.Find(side + "UpperArm");
                var lower = b.Find(side + "LowerArm");
                var hand = b.Find(side + "Hand");
                var thigh = b.Find(side + "UpperLeg");
                var knee = b.Find(side + "LowerLeg");
                if (upper == null || lower == null || hand == null) continue;
                float sx = Mathf.Sign(upper.position.x - b.Hips.position.x);

                Vector3 target;
                if (occ.Driver && wheel != null)
                    target = wheel.position + new Vector3(sx * 0.185f, -0.015f, -0.075f);     // 9 and 3 o'clock on the rim
                else
                    target = Vector3.Lerp(thigh.position, knee.position, 0.55f) + new Vector3(-sx * 0.02f, 0.10f, 0f);

                TwoBoneIK(upper, lower, hand, target, Vector3.down + Vector3.right * sx * 0.45f);
                if (occ.Driver) RotW(hand, Vector3.right, -35f);
            }
        }

        private static void TwoBoneIK(Transform upper, Transform lower, Transform hand, Vector3 target, Vector3 pole)
        {
            Vector3 s = upper.position;
            float a = (lower.position - s).magnitude;
            float c = (hand.position - lower.position).magnitude;
            Vector3 toTarget = target - s;
            float d = Mathf.Clamp(toTarget.magnitude, Mathf.Abs(a - c) + 0.01f, a + c - 0.005f);
            Vector3 dir = toTarget.normalized;
            float x = (a * a - c * c + d * d) / (2f * d);
            float h = Mathf.Sqrt(Mathf.Max(0f, a * a - x * x));
            Vector3 elbow = s + dir * x + Vector3.ProjectOnPlane(pole, dir).normalized * h;
            upper.rotation = Quaternion.FromToRotation(lower.position - s, elbow - s) * upper.rotation;
            Vector3 wrist = s + dir * d;
            lower.rotation = Quaternion.FromToRotation(hand.position - lower.position, wrist - lower.position) * lower.rotation;
        }

        // --------------------------------------------------------------- animation
        private static AnimationClip BuildClip(Transform root, Occupant occ)
        {
            var clip = new AnimationClip { name = $"{occ.Name}_SitCar", frameRate = 30f };
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                if (t == root || t.GetComponent<Renderer>() != null) continue;
                string path = AnimationUtility.CalculateTransformPath(t, root);
                Quaternion q = t.localRotation;
                Vector3 p = t.localPosition;
                Set(clip, path, "localRotation.x", q.x);
                Set(clip, path, "localRotation.y", q.y);
                Set(clip, path, "localRotation.z", q.z);
                Set(clip, path, "localRotation.w", q.w);
                Set(clip, path, "localPosition.x", p.x);
                Set(clip, path, "localPosition.y", p.y);
                Set(clip, path, "localPosition.z", p.z);
            }
            var settings = AnimationUtility.GetAnimationClipSettings(clip);
            settings.loopTime = true;
            AnimationUtility.SetAnimationClipSettings(clip, settings);

            string path2 = $"Assets/Characters/{occ.Name}/{occ.Name}_SitCar.anim";
            AssetDatabase.DeleteAsset(path2);
            AssetDatabase.CreateAsset(clip, path2);
            return clip;
        }

        private static void Set(AnimationClip clip, string path, string prop, float value)
        {
            clip.SetCurve(path, typeof(Transform), prop, AnimationCurve.Constant(0f, 1f, value));
        }

        private static RuntimeAnimatorController BuildController(Occupant occ, AnimationClip clip)
        {
            string path = $"Assets/Characters/{occ.Name}/{occ.Name}_SitCar.controller";
            AssetDatabase.DeleteAsset(path);
            var controller = AnimatorController.CreateAnimatorControllerAtPath(path);
            var state = controller.layers[0].stateMachine.AddState("SitCar");
            state.motion = clip;
            controller.layers[0].stateMachine.defaultState = state;
            EditorUtility.SetDirty(controller);
            return controller;
        }

        // -------------------------------------------------------------- screenshots
        private static void Screenshots(GameObject car)
        {
            string dir = Path.GetFullPath(Path.Combine(Application.dataPath, "../../Vehicles/Blender/preview"));
            Directory.CreateDirectory(dir);

            var lightGo = new GameObject("ShotLight");
            var light = lightGo.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.3f;
            lightGo.transform.rotation = Quaternion.Euler(48f, 150f, 0f);
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.55f, 0.57f, 0.6f);
            DynamicGI.UpdateEnvironment();
            var fillGo = new GameObject("ShotFill");
            var fill = fillGo.AddComponent<Light>();
            fill.type = LightType.Directional;
            fill.intensity = 0.9f;
            fillGo.transform.rotation = Quaternion.Euler(25f, -20f, 0f);

            var glass = car.transform.Find("Glass");
            var body = car.transform.Find("Body");

            Shot(Path.Combine(dir, "unity_players_front.png"), new Vector3(-0.9f, 1.35f, 4.6f), new Vector3(0f, 0.95f, 0f), 32f);
            Shot(Path.Combine(dir, "unity_players_side.png"), new Vector3(-4.2f, 1.3f, 0.8f), new Vector3(0f, 0.85f, -0.1f), 32f);
            if (glass) glass.gameObject.SetActive(false);
            if (body) body.gameObject.SetActive(false);
            Shot(Path.Combine(dir, "unity_players_cutaway_left.png"), new Vector3(-3.4f, 1.5f, 1.4f), new Vector3(0f, 0.75f, -0.4f), 30f);
            Shot(Path.Combine(dir, "unity_players_cutaway_right.png"), new Vector3(3.2f, 1.7f, 1.8f), new Vector3(0f, 0.75f, -0.4f), 30f);
            Shot(Path.Combine(dir, "unity_players_cutaway_top.png"), new Vector3(0f, 4.6f, -0.2f), new Vector3(0f, 0.6f, -0.1f), 32f);
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
