using System.Collections.Generic;
using System.IO;
using System.Linq;
using Arnafjordur.Intro;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Arnafjordur.Editor
{
    /// <summary>
    /// Builds Assets/Scenes/Intro_CarExit.unity: a copy of the church arrival scene with the Golf already parked,
    /// where Leivur, Andrias and Ólavur get out of the car outside Árnafjarðar kirkja and stand facing the camera.
    /// Also chains Intro_ChurchArrival into it.
    /// </summary>
    public static class IntroCarExitSceneSetup
    {
        private const string ScenePath = "Assets/Scenes/Intro_CarExit.unity";
        private const string ChurchScenePath = "Assets/Scenes/Intro_ChurchArrival.unity";
        private const string SceneName = "Intro_CarExit";

        // Car-local layout (metres): x is across the car, z is along it.
        private const float DoorOutX = 1.4f;        // how far out from the centre line a player stands after getting out
        private const float RearClearZ = -2.9f;     // behind the rear bumper, on the road the car just drove in on
        private const float DoorOpenDegrees = 62f;
        private static readonly string[] SeatOrder = { "Leivur", "Andrias", "Olavur" };
        private static readonly float[] StandTimes = { 1.0f, 1.5f, 2.1f };

        [MenuItem("Tools/Intro/Open Car Exit Scene")]
        public static void Open()
        {
            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        }

        [MenuItem("Tools/Intro/Rebuild Car Exit Scene")]
        public static void Build()
        {
            if (!File.Exists(ChurchScenePath))
            {
                Debug.LogError("[IntroCarExitSceneSetup] Intro_ChurchArrival.unity missing - run Tools/Intro/Rebuild Church Arrival Scene first");
                return;
            }

            var controllers = new Dictionary<string, RuntimeAnimatorController>();
            foreach (string name in SeatOrder)
            {
                var c = BuildController(name);
                if (c == null) return;
                controllers[name] = c;
            }
            AssetDatabase.SaveAssets();

            // ---- start from the arrival scene so the village, lighting and parked position match exactly
            AssetDatabase.DeleteAsset(ScenePath);
            if (!AssetDatabase.CopyAsset(ChurchScenePath, ScenePath))
            {
                Debug.LogError("[IntroCarExitSceneSetup] could not copy " + ChurchScenePath);
                return;
            }
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

            var arrival = Object.FindAnyObjectByType<IntroPathDrive>();
            var carGo = GameObject.Find("VWGolf_WithPlayers");
            var cam = Camera.main != null ? Camera.main : Object.FindAnyObjectByType<Camera>();
            if (arrival == null || carGo == null || cam == null)
            {
                Debug.LogError("[IntroCarExitSceneSetup] arrival scene is missing its director, car or camera");
                return;
            }
            arrival.Evaluate(arrival.Duration);          // car parked, camera on the last frame of the arrival shot
            Object.DestroyImmediate(arrival.gameObject);

            Transform car = carGo.transform;
            Physics.SyncTransforms();

            Vector3 Ground(Vector3 p)
            {
                float best = float.NegativeInfinity;
                foreach (var hit in Physics.RaycastAll(new Vector3(p.x, p.y + 6f, p.z), Vector3.down, 16f))
                    if (hit.point.y > best) best = hit.point.y;
                if (!float.IsNegativeInfinity(best)) p.y = best;
                return p;
            }
            Vector3 Local(float x, float z) => Ground(car.TransformPoint(new Vector3(x, 0f, z)));

            // The players gather on the side of the car the camera can see.
            float camSide = Mathf.Sign(car.InverseTransformPoint(cam.transform.position).x);
            if (camSide == 0f) camSide = 1f;

            var seats = new List<(string name, Transform root, Animator animator, float side, float z)>();
            foreach (string name in SeatOrder)
            {
                Transform root = car.Find($"Occupants/{name}_Seated");
                if (root == null)
                {
                    Debug.LogError($"[IntroCarExitSceneSetup] {name}_Seated not found under the car");
                    return;
                }
                var animator = root.GetComponent<Animator>();
                animator.runtimeAnimatorController = controllers[name];
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                foreach (var smr in root.GetComponentsInChildren<SkinnedMeshRenderer>(true)) smr.updateWhenOffscreen = true;

                Transform hips = root.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == "Hips");
                Vector3 seat = car.InverseTransformPoint(hips != null ? hips.position : root.position);
                float side = Mathf.Abs(seat.x) < 0.12f ? camSide : Mathf.Sign(seat.x);   // middle seat gets out on the camera side
                seats.Add((name, root, animator, side, seat.z));
            }

            // Standing spots in a loose row beside the car; whoever has to walk round the back takes the rearmost ones.
            var spots = new List<Vector3>
            {
                Local(camSide * 2.2f, 0.7f),
                Local(camSide * 2.6f, -0.5f),
                Local(camSide * 2.2f, -1.7f),
            };
            var order = seats.OrderBy(s => s.side == camSide ? 0 : 1).ThenByDescending(s => s.z).ToList();

            var doors = car.GetComponentsInChildren<Transform>(true).Where(t => t.name.StartsWith("Door_")).ToList();
            float rearHingeZ = doors.Where(d => d.name.StartsWith("Door_R"))
                .Select(d => car.InverseTransformPoint(d.position).z).DefaultIfEmpty(-0.42f).First();

            var occupants = new IntroCarExit.Occupant[seats.Count];
            var report = $"[IntroCarExitSceneSetup] OK saved {ScenePath} (camera side {(camSide > 0 ? "right" : "left")})";
            for (int i = 0; i < seats.Count; i++)
            {
                var s = seats[i];
                var route = new List<Vector3> { Local(s.side * DoorOutX, s.z + 0.1f) };
                if (s.side != camSide)
                {
                    route.Add(Local(s.side * DoorOutX, RearClearZ));
                    route.Add(Local(camSide * DoorOutX, RearClearZ));
                }
                route.Add(spots[order.FindIndex(o => o.name == s.name)]);
                // Front seats use the front door on their side; the back seat uses the rear door.
                Transform door = doors
                    .Where(d => Mathf.Sign(car.InverseTransformPoint(d.position).x) == s.side)
                    .OrderBy(d => d.name.StartsWith("Door_F") == (s.z > rearHingeZ - 0.2f) ? 0 : 1)
                    .FirstOrDefault();
                if (door == null) Debug.LogWarning($"[IntroCarExitSceneSetup] no door found for {s.name} - re-export VWGolf.fbx from build_golf.py");
                occupants[i] = new IntroCarExit.Occupant
                {
                    root = s.root,
                    animator = s.animator,
                    door = door,
                    doorOpenAngle = -s.side * DoorOpenDegrees,
                    standTime = StandTimes[i],
                    route = route.ToArray(),
                };
                report += $"\n  {s.name}: {(door != null ? door.name : "no door")} ({(s.side > 0 ? "right" : "left")}), {route.Count} route points, stands at {route[route.Count - 1]}";
            }

            // ---- camera eases in from the arrival's last frame towards the car and the group
            Vector3 group = spots.Aggregate(Vector3.zero, (a, b) => a + b) / spots.Count + Vector3.up * 1.0f;
            Vector3 lookAt = Vector3.Lerp(car.position + Vector3.up * 0.9f, group, 0.5f);
            Vector3 camEnd = Vector3.Lerp(cam.transform.position, lookAt, 0.4f);
            camEnd.y = Mathf.Max(camEnd.y, Ground(camEnd).y + 1.7f);

            // ---- everyone ends up facing the camera (where it settles), so the shot shows their faces
            Vector3 faceTarget = camEnd;

            // ---- lamps off once parked
            Transform lights = car.Find("Lights");
            var body = car.Find("VWGolf/Body").GetComponent<MeshRenderer>();
            var offMats = body.sharedMaterials;
            for (int i = 0; i < offMats.Length; i++)
            {
                if (offMats[i] == null || !offMats[i].name.EndsWith("_On")) continue;
                string baseName = offMats[i].name.Substring(0, offMats[i].name.Length - 3);
                var off = AssetDatabase.LoadAssetAtPath<Material>($"Assets/Vehicles/Golf/Materials/{baseName}.mat");
                if (off != null) offMats[i] = off;
            }

            var director = new GameObject("IntroCarExit").AddComponent<IntroCarExit>();
            director.Configure(occupants, faceTarget, cam, camEnd, lookAt, cam.fieldOfView,
                lights != null ? lights.gameObject : null, body, offMats);
            var tailTf = car.Find("VWGolf/Tailgate");
            if (tailTf != null && tailTf.TryGetComponent(out MeshRenderer tail))
            {
                var tailOff = tail.sharedMaterials;
                for (int i = 0; i < tailOff.Length; i++)
                {
                    if (tailOff[i] == null || !tailOff[i].name.EndsWith("_On")) continue;
                    var off = AssetDatabase.LoadAssetAtPath<Material>($"Assets/Vehicles/Golf/Materials/{tailOff[i].name.Substring(0, tailOff[i].name.Length - 3)}.mat");
                    if (off != null) tailOff[i] = off;
                }
                director.ConfigureTailgateLights(tail, tailOff);
            }
            if (File.Exists(VillageSceneSetup.ScenePath)) director.NextSceneName = VillageSceneSetup.SceneName;   // on into the playable village
            EditorUtility.SetDirty(director);
            EditorSceneManager.MarkSceneDirty(scene);
            IntroMusicSetup.AddToOpenScene();
            EditorSceneManager.SaveScene(scene, ScenePath);

            // Preview frames with the doors swung open (not saved: the scene is reopened below).
            try
            {
                ShaderUtil.allowAsyncCompilation = false;
                foreach (var occ in occupants)
                    if (occ.door != null)
                        occ.door.rotation = Quaternion.AngleAxis(occ.doorOpenAngle, car.up) * occ.door.rotation;
                string previewDir = Path.GetFullPath(Path.Combine(Application.dataPath, "../Docs/preview"));
                Directory.CreateDirectory(previewDir);
                Vector3 target = car.TransformPoint(new Vector3(0f, 0.8f, -0.2f));
                var views = new (string name, Vector3 local)[]
                {
                    ("right", new Vector3(4.6f, 1.7f, -2.6f)),
                    ("left", new Vector3(-4.6f, 1.7f, -2.6f)),
                };
                Vector3 keepPos = cam.transform.position;
                Quaternion keepRot = cam.transform.rotation;
                float keepFov = cam.fieldOfView;
                foreach (var view in views)
                {
                    cam.transform.position = car.TransformPoint(view.local);
                    cam.transform.LookAt(target);
                    cam.fieldOfView = 45f;
                    Shot(cam, Path.Combine(previewDir, $"intro_carexit_doors_{view.name}.png"));
                }
                cam.transform.SetPositionAndRotation(keepPos, keepRot);
                cam.fieldOfView = keepFov;
                Shot(cam, Path.Combine(previewDir, "intro_carexit_start.png"));
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("[IntroCarExitSceneSetup] preview frames failed: " + e.Message);
            }

            var buildScenes = EditorBuildSettings.scenes.ToList();
            if (buildScenes.All(s => s.path != ScenePath))
                buildScenes.Add(new EditorBuildSettingsScene(ScenePath, true));
            EditorBuildSettings.scenes = buildScenes.ToArray();

            // ---- the arrival now hands over to this scene
            var churchScene = EditorSceneManager.OpenScene(ChurchScenePath, OpenSceneMode.Single);
            var churchDirector = Object.FindAnyObjectByType<IntroPathDrive>();
            if (churchDirector != null && churchDirector.NextSceneName != SceneName)
            {
                churchDirector.NextSceneName = SceneName;
                EditorUtility.SetDirty(churchDirector);
                EditorSceneManager.MarkSceneDirty(churchScene);
                EditorSceneManager.SaveScene(churchScene);
            }

            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            AssetDatabase.SaveAssets();
            Debug.Log(report);
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

        /// <summary>Seated pose that blends into the character's own idle/walk when the Stand trigger fires.</summary>
        private static RuntimeAnimatorController BuildController(string name)
        {
            var sit = AssetDatabase.LoadAssetAtPath<AnimationClip>($"Assets/Characters/{name}/{name}_SitCar.anim");
            var clips = AssetDatabase.LoadAllAssetsAtPath($"Assets/Characters/{name}/{name}.fbx")
                .OfType<AnimationClip>()
                .Where(c => !c.name.StartsWith("__preview__"))
                .GroupBy(c => c.name.Substring(c.name.IndexOf('|') + 1))
                .ToDictionary(g => g.Key, g => g.First());
            clips.TryGetValue("Idle", out var idle);
            clips.TryGetValue("Walk", out var walk);
            if (sit == null || idle == null || walk == null)
            {
                Debug.LogError($"[IntroCarExitSceneSetup] {name}: need {name}_SitCar.anim plus Idle and Walk clips " +
                               "(run Tools/Vehicles/Rebuild VW Golf With Players and Arnafjordur/Setup Playable Flat Scene)");
                return null;
            }

            string path = $"Assets/Characters/{name}/{name}_CarExit.controller";
            AssetDatabase.DeleteAsset(path);
            var controller = AnimatorController.CreateAnimatorControllerAtPath(path);
            controller.AddParameter("Speed", AnimatorControllerParameterType.Float);
            controller.AddParameter("Stand", AnimatorControllerParameterType.Trigger);

            var machine = controller.layers[0].stateMachine;
            var sitState = machine.AddState("SitCar");
            sitState.motion = sit;

            var walkState = controller.CreateBlendTreeInController("Locomotion", out BlendTree tree, 0);
            for (int p = controller.parameters.Length - 1; p >= 0; p--)
                if (controller.parameters[p].name == "Blend") controller.RemoveParameter(p);
            tree.name = "LocomotionBlendTree";
            tree.blendType = BlendTreeType.Simple1D;
            tree.blendParameter = "Speed";
            tree.useAutomaticThresholds = false;
            tree.AddChild(idle, 0f);
            tree.AddChild(walk, 1.75f);

            machine.defaultState = sitState;
            var stand = sitState.AddTransition(walkState);
            stand.hasExitTime = false;
            stand.hasFixedDuration = true;
            stand.duration = 0.8f;
            stand.AddCondition(AnimatorConditionMode.If, 0f, "Stand");

            EditorUtility.SetDirty(tree);
            EditorUtility.SetDirty(controller);
            return controller;
        }
    }
}
