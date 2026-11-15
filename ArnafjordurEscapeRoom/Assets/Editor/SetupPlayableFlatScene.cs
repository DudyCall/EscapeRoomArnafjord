using System.Collections.Generic;
using System.Linq;
using Arnafjordur.Characters;
using Arnafjordur.Core;
using Arnafjordur.UI;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;

namespace Arnafjordur.Editor
{
    /// <summary>
    /// Configures rigged character FBX importers, locomotion Animator assets, and the playable flat SampleScene.
    /// </summary>
    public static class SetupPlayableFlatScene
    {
        private static readonly string[] CharacterNames = { "Olavur", "Leivur", "Andrias" };

        private const string BaseControllerPath = "Assets/Characters/CharacterLocomotion.controller";
        private const string WetBasaltMatPath = "Assets/Materials/Mat_WetBasalt.mat";
        private const string ConcreteMatPath = "Assets/Materials/Mat_HarbourConcrete.mat";
        private const float ArenaFloorSize = 40f;
        private const float ArenaHalfExtent = 18.4f;

        /// <summary>
        /// Runs the complete end-to-end character rig import, Animator controller generation, and SampleScene wiring.
        /// </summary>
        [MenuItem("Arnafjordur/Setup Playable Flat Scene")]
        public static void Run()
        {
            const string sampleScenePath = "Assets/Scenes/SampleScene.unity";
            if (EditorSceneManager.GetActiveScene().path != sampleScenePath &&
                AssetDatabase.LoadAssetAtPath<SceneAsset>(sampleScenePath) != null)
            {
                EditorSceneManager.OpenScene(sampleScenePath, OpenSceneMode.Single);
            }

            ConfigureAllModelImporters();

            AnimatorController baseController = BuildOrUpdateBaseAnimatorController();
            RuntimeAnimatorController[] characterControllers = new RuntimeAnimatorController[CharacterNames.Length];
            for (int i = 0; i < CharacterNames.Length; i++)
            {
                characterControllers[i] = BuildOrUpdateOverrideController(CharacterNames[i], baseController);
            }

            SetupFlatArenaGeometry();
            CharacterMotor[] motors = SetupCharactersInScene(characterControllers);
            SetupPartyCameraAndHud(motors);

            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
            EditorSceneManager.SaveOpenScenes();
            AssetDatabase.SaveAssets();
            Debug.Log("[SetupPlayableFlatScene] Completed rigged FBX import, locomotion Animator setup, and playable flat scene.");
        }

        private static void ConfigureAllModelImporters()
        {
            foreach (string name in CharacterNames)
            {
                string fbxPath = $"Assets/Characters/{name}/{name}.fbx";
                ModelImporter importer = AssetImporter.GetAtPath(fbxPath) as ModelImporter;
                if (importer == null)
                {
                    Debug.LogError($"[SetupPlayableFlatScene] Missing FBX at {fbxPath}");
                    continue;
                }

                importer.animationType = ModelImporterAnimationType.Generic;
                importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
                importer.importAnimation = true;
                importer.optimizeGameObjects = false;

                ModelImporterClipAnimation[] clips = importer.defaultClipAnimations;
                for (int i = 0; i < clips.Length; i++)
                {
                    string shortName = StripRigPrefix(clips[i].takeName);
                    clips[i].name = shortName;
                    clips[i].loopTime = true;
                    clips[i].loopPose = true;
                    clips[i].keepOriginalOrientation = true;
                    clips[i].keepOriginalPositionY = true;
                    clips[i].keepOriginalPositionXZ = true;
                    clips[i].lockRootRotation = true;
                    clips[i].lockRootHeightY = true;
                    clips[i].lockRootPositionXZ = true;
                }

                importer.clipAnimations = clips;
                importer.SaveAndReimport();
            }
        }

        private static string StripRigPrefix(string takeName)
        {
            if (string.IsNullOrEmpty(takeName))
            {
                return takeName;
            }

            int pipeIndex = takeName.IndexOf('|');
            return pipeIndex >= 0 && pipeIndex + 1 < takeName.Length
                ? takeName.Substring(pipeIndex + 1)
                : takeName;
        }

        private static AnimatorController BuildOrUpdateBaseAnimatorController()
        {
            AnimatorController controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(BaseControllerPath);
            if (controller != null)
            {
                AssetDatabase.DeleteAsset(BaseControllerPath);
            }

            controller = AnimatorController.CreateAnimatorControllerAtPath(BaseControllerPath);
            controller.AddParameter("Speed", AnimatorControllerParameterType.Float);

            Dictionary<string, AnimationClip> olavurClips = LoadCharacterClips("Olavur");
            AnimationClip idle = olavurClips.GetValueOrDefault("Idle");
            AnimationClip walk = olavurClips.GetValueOrDefault("Walk");
            AnimationClip run = olavurClips.GetValueOrDefault("Run");

            AnimatorStateMachine rootStateMachine = controller.layers[0].stateMachine;
            BlendTree locomotionTree;
            AnimatorState locomotionState = controller.CreateBlendTreeInController("Locomotion", out locomotionTree, 0);
            locomotionState.writeDefaultValues = true;
            rootStateMachine.defaultState = locomotionState;

            for (int p = controller.parameters.Length - 1; p >= 0; p--)
            {
                if (controller.parameters[p].name == "Blend")
                {
                    controller.RemoveParameter(p);
                }
            }

            locomotionTree.name = "LocomotionBlendTree";
            locomotionTree.blendType = BlendTreeType.Simple1D;
            locomotionTree.blendParameter = "Speed";
            locomotionTree.useAutomaticThresholds = false;

            if (idle != null) locomotionTree.AddChild(idle, 0.0f);
            if (walk != null) locomotionTree.AddChild(walk, 1.75f);
            if (run != null) locomotionTree.AddChild(run, 4.2f);

            EditorUtility.SetDirty(locomotionTree);
            EditorUtility.SetDirty(controller);
            return controller;
        }

        private static RuntimeAnimatorController BuildOrUpdateOverrideController(string characterName, AnimatorController baseController)
        {
            string overridePath = $"Assets/Characters/{characterName}/{characterName}_Locomotion.overrideController";
            AnimatorOverrideController existing = AssetDatabase.LoadAssetAtPath<AnimatorOverrideController>(overridePath);
            if (existing != null)
            {
                AssetDatabase.DeleteAsset(overridePath);
            }

            AnimatorOverrideController overrideController = new AnimatorOverrideController(baseController);
            Dictionary<string, AnimationClip> clips = LoadCharacterClips(characterName);

            var overrides = new List<KeyValuePair<AnimationClip, AnimationClip>>(overrideController.overridesCount);
            overrideController.GetOverrides(overrides);
            for (int i = 0; i < overrides.Count; i++)
            {
                AnimationClip keyClip = overrides[i].Key;
                if (keyClip == null)
                {
                    continue;
                }

                string clipKeyName = StripRigPrefix(keyClip.name);
                if (clips.TryGetValue(clipKeyName, out AnimationClip replacement) && replacement != null)
                {
                    overrides[i] = new KeyValuePair<AnimationClip, AnimationClip>(keyClip, replacement);
                }
            }

            overrideController.ApplyOverrides(overrides);
            AssetDatabase.CreateAsset(overrideController, overridePath);
            EditorUtility.SetDirty(overrideController);
            return overrideController;
        }

        private static Dictionary<string, AnimationClip> LoadCharacterClips(string characterName)
        {
            string fbxPath = $"Assets/Characters/{characterName}/{characterName}.fbx";
            return AssetDatabase.LoadAllAssetsAtPath(fbxPath)
                .OfType<AnimationClip>()
                .Where(c => !c.name.StartsWith("__preview__"))
                .GroupBy(c => StripRigPrefix(c.name))
                .ToDictionary(g => g.Key, g => g.First());
        }

        private static void SetupFlatArenaGeometry()
        {
            GameObject baseline = FindOrCreate("_LightingBaseline");
            Transform floorTransform = baseline.transform.Find("QuayFloorReference");
            GameObject floor = floorTransform != null ? floorTransform.gameObject : GameObject.CreatePrimitive(PrimitiveType.Cube);
            floor.name = "QuayFloorReference";
            floor.transform.SetParent(baseline.transform, false);
            floor.transform.position = new Vector3(0f, -0.15f, 0f);
            floor.transform.rotation = Quaternion.identity;
            floor.transform.localScale = new Vector3(ArenaFloorSize, 0.3f, ArenaFloorSize);

            Material concreteMat = AssetDatabase.LoadAssetAtPath<Material>(ConcreteMatPath);
            if (concreteMat != null && floor.TryGetComponent(out Renderer floorRenderer))
            {
                floorRenderer.sharedMaterial = concreteMat;
            }

            // Rebuild subtle flush surface markers and low perimeter coping so movement speed reads clearly on the flat ground.
            Transform oldArena = baseline.transform.Find("_FlatArenaMarkings");
            if (oldArena != null)
            {
                Object.DestroyImmediate(oldArena.gameObject);
            }

            GameObject arenaRoot = new GameObject("_FlatArenaMarkings");
            arenaRoot.transform.SetParent(baseline.transform, false);

            Material basaltMat = EnsureWetBasaltMaterial();

            // Flush decorative joint strips (no colliders so feet glide cleanly across y = 0; +5mm prevents distant z-fighting).
            const float gridExtent = 38f;
            for (int coord = -15; coord <= 15; coord += 5)
            {
                CreateArenaStrip($"GridX_{coord}", arenaRoot.transform, new Vector3(0f, -0.005f, coord), new Vector3(gridExtent, 0.02f, 0.055f), basaltMat, withCollider: false);
                CreateArenaStrip($"GridZ_{coord}", arenaRoot.transform, new Vector3(coord, -0.005f, 0f), new Vector3(0.055f, 0.02f, gridExtent), basaltMat, withCollider: false);
            }

            // Low quay coping curb along all four outer edges (with colliders at +-19.5m).
            const float curbOffset = 19.6f;
            const float curbSpan = 40f;
            CreateArenaStrip("CurbNorth", arenaRoot.transform, new Vector3(0f, 0.08f, curbOffset), new Vector3(curbSpan, 0.18f, 0.8f), basaltMat, withCollider: true);
            CreateArenaStrip("CurbSouth", arenaRoot.transform, new Vector3(0f, 0.08f, -curbOffset), new Vector3(curbSpan, 0.18f, 0.8f), basaltMat, withCollider: true);
            CreateArenaStrip("CurbEast", arenaRoot.transform, new Vector3(curbOffset, 0.08f, 0f), new Vector3(0.8f, 0.18f, curbSpan), basaltMat, withCollider: true);
            CreateArenaStrip("CurbWest", arenaRoot.transform, new Vector3(-curbOffset, 0.08f, 0f), new Vector3(0.8f, 0.18f, curbSpan), basaltMat, withCollider: true);
        }

        private static void CreateArenaStrip(string name, Transform parent, Vector3 position, Vector3 scale, Material material, bool withCollider)
        {
            GameObject strip = GameObject.CreatePrimitive(PrimitiveType.Cube);
            strip.name = name;
            strip.transform.SetParent(parent, false);
            strip.transform.position = position;
            strip.transform.localScale = scale;
            strip.isStatic = true;

            if (!withCollider && strip.TryGetComponent(out Collider col))
            {
                Object.DestroyImmediate(col);
            }

            if (material != null && strip.TryGetComponent(out Renderer renderer))
            {
                renderer.sharedMaterial = material;
            }
        }

        private static Material EnsureWetBasaltMaterial()
        {
            Material mat = AssetDatabase.LoadAssetAtPath<Material>(WetBasaltMatPath);
            if (mat != null)
            {
                return mat;
            }

            Shader urpLit = Shader.Find("Universal Render Pipeline/Lit");
            if (urpLit == null) urpLit = Shader.Find("Standard");
            mat = new Material(urpLit) { name = "Mat_WetBasalt" };
            mat.SetColor("_BaseColor", Palette.WetBasalt);
            mat.SetFloat("_Smoothness", 0.22f);
            mat.SetFloat("_Metallic", 0.0f);
            AssetDatabase.CreateAsset(mat, WetBasaltMatPath);
            return mat;
        }

        private static CharacterMotor[] SetupCharactersInScene(RuntimeAnimatorController[] controllers)
        {
            GameObject showcaseRoot = FindOrCreate("_CharactersShowcase");

            // Rebuild character instances cleanly from reimported FBX prefabs to guarantee fresh rigs and Avatars.
            for (int i = showcaseRoot.transform.childCount - 1; i >= 0; i--)
            {
                Object.DestroyImmediate(showcaseRoot.transform.GetChild(i).gameObject);
            }

            Vector3[] spawnPositions =
            {
                new Vector3(-1.6f, 0f, 0f),
                new Vector3(0.0f, 0f, 0f),
                new Vector3(1.6f, 0f, 0f),
            };

            CharacterMotor[] motors = new CharacterMotor[CharacterNames.Length];
            for (int i = 0; i < CharacterNames.Length; i++)
            {
                string name = CharacterNames[i];
                string fbxPath = $"Assets/Characters/{name}/{name}.fbx";
                GameObject fbxPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(fbxPath);
                GameObject instance = (GameObject)PrefabUtility.InstantiatePrefab(fbxPrefab, showcaseRoot.transform);
                instance.name = name;
                instance.transform.position = spawnPositions[i];
                instance.transform.rotation = Quaternion.Euler(0f, 180f, 0f);
                instance.transform.localScale = Vector3.one;

                Animator animator = GetOrAdd<Animator>(instance);
                Avatar avatar = AssetDatabase.LoadAllAssetsAtPath(fbxPath).OfType<Avatar>().FirstOrDefault();
                if (avatar != null)
                {
                    animator.avatar = avatar;
                }

                animator.runtimeAnimatorController = controllers[i];
                animator.applyRootMotion = false;
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;

                Material charMat = AssetDatabase.LoadAssetAtPath<Material>($"Assets/Characters/{name}/Mat_{name}.mat");
                foreach (SkinnedMeshRenderer smr in instance.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    if (charMat != null)
                    {
                        smr.sharedMaterial = charMat;
                    }

                    smr.updateWhenOffscreen = true;
                }

                CharacterController controller = GetOrAdd<CharacterController>(instance);
                controller.height = 1.74f;
                controller.radius = 0.28f;
                controller.skinWidth = 0.03f;
                controller.center = new Vector3(0f, 0.90f, 0f);
                controller.stepOffset = 0.25f;
                controller.slopeLimit = 45f;
                controller.minMoveDistance = 0.0001f;

                CharacterMotor motor = GetOrAdd<CharacterMotor>(instance);
                motor.Configure(name, animator, ArenaHalfExtent);
                Transform rightHand = instance.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == "RightHand");
                Transform rightLowerArm = instance.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == "RightLowerArm");
                Vector3 fistLocal = CharacterItemHolder.ComputeFistCentre(instance.transform, rightHand, rightLowerArm);
                CharacterItemHolder itemHolder = GetOrAdd<CharacterItemHolder>(instance);
                itemHolder.Configure(rightHand, fistLocal);
                CharacterDrunk drunk = GetOrAdd<CharacterDrunk>(instance);
                EditorUtility.SetDirty(animator);
                EditorUtility.SetDirty(controller);
                EditorUtility.SetDirty(motor);
                EditorUtility.SetDirty(itemHolder);
                EditorUtility.SetDirty(drunk);
                motors[i] = motor;
            }

            return motors;
        }

        private static void SetupPartyCameraAndHud(CharacterMotor[] motors)
        {
            Camera mainCam = Camera.main;
            if (mainCam == null)
            {
                mainCam = Object.FindAnyObjectByType<Camera>();
            }

            ThirdPersonCameraRig cameraRig = null;
            if (mainCam != null)
            {
                mainCam.fieldOfView = 45f;
                cameraRig = GetOrAdd<ThirdPersonCameraRig>(mainCam.gameObject);
                if (motors.Length > 0 && motors[0] != null)
                {
                    cameraRig.SetTarget(motors[0].transform, snap: false);
                    cameraRig.SetOrbit(18f, 16f, 5.6f, snap: true);
                }

                EditorUtility.SetDirty(cameraRig);
            }

            GameObject partyGo = FindOrCreate("_PartySystem");
            PartyController partyController = GetOrAdd<PartyController>(partyGo);
            partyController.Configure(motors, cameraRig, mainCam);
            EditorUtility.SetDirty(partyController);

            CharacterTestHud hud = GetOrAdd<CharacterTestHud>(partyGo);
            hud.Configure(partyController);
            EditorUtility.SetDirty(hud);

            EventSystem existingEventSystem = Object.FindAnyObjectByType<EventSystem>();
            if (existingEventSystem == null)
            {
                new GameObject("EventSystem", typeof(EventSystem), typeof(InputSystemUIInputModule));
            }
            else if (existingEventSystem.GetComponent<InputSystemUIInputModule>() == null)
            {
                existingEventSystem.gameObject.AddComponent<InputSystemUIInputModule>();
            }
        }

        private static GameObject FindOrCreate(string objectName)
        {
            GameObject existing = GameObject.Find(objectName);
            return existing != null ? existing : new GameObject(objectName);
        }

        private static T GetOrAdd<T>(GameObject target) where T : Component
        {
            return target.TryGetComponent(out T component) ? component : target.AddComponent<T>();
        }
    }
}
