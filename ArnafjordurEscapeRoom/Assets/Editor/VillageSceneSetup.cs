using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Arnafjordur.Characters;
using Arnafjordur.Core;
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
            "ChurchParkingApron",
            "VillageBeach",
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

            WalkableFile walkableData = JsonUtility.FromJson<WalkableFile>(File.ReadAllText(WalkableJsonPath));
            ConfigureParkedCar(carGo, walkableData.seaLevel);
            EnsureBuildingColliders();
            Physics.SyncTransforms();

            BuildBeach(walkableData);
            FitTerrainUnderVillageRoads(walkableData);
            BuildShoreRocks(walkableData.seaLevel);
            SeaMineBomb seaMine = BuildSeaMineBomb(walkableData.seaLevel);
            Physics.SyncTransforms();
            BuildBeachPebbles(walkableData.seaLevel, seaMine != null ? seaMine.transform.position : Vector3.positiveInfinity);
            Physics.SyncTransforms();
            BuildBeachWood(walkableData.seaLevel, seaMine != null ? seaMine.transform.position : Vector3.positiveInfinity);
            var walkableArea = BuildWalkableAndFootpaths(walkableData);
            FjordWaterSetup.ApplyToExisting();

            CharacterMotor[] motors = ExtractAndConfigurePlayers(exit, carGo, walkableArea, locomotionControllers, idleClips);
            if (motors == null)
            {
                return;
            }

            SetupCameraPartyAndHud(cam, motors, walkableArea.gameObject, seaMine);

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, ScenePath);

            VerifyWalkableConstraints(walkableArea, walkableData, motors);
            VerifyDrunkProgression(motors, cam);
            VerifyFishingRod(carGo, motors, cam);
            VerifySeaMineBomb(seaMine, motors, cam);
            RenderPreviewFrames(cam, motors);

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

        private static void ConfigureParkedCar(GameObject carGo, float seaLevel)
        {
            Transform lights = carGo.transform.Find("Lights");
            if (lights != null)
            {
                lights.gameObject.SetActive(false);
            }

            foreach (string part in new[] { "VWGolf/Body", "VWGolf/Tailgate" })
            {
                Transform bodyTransform = carGo.transform.Find(part);
                if (bodyTransform == null || !bodyTransform.TryGetComponent(out MeshRenderer body))
                {
                    continue;
                }

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
            Transform tailgate = golf != null ? golf.Find("Tailgate") : null;
            if (tailgate != null && tailgate.TryGetComponent(out MeshFilter tailMesh))
            {
                // Click target for opening the trunk; a trigger, so it never blocks walking or the camera.
                var click = GetOrAdd<BoxCollider>(tailgate.gameObject);
                click.isTrigger = true;
                click.center = tailMesh.sharedMesh.bounds.center;
                click.size = tailMesh.sharedMesh.bounds.size;
                var trunk = GetOrAdd<CarTrunk>(tailgate.gameObject);
                trunk.Configure(new[] { BuildBeerBox(golf), BuildRadio(golf), BuildFishingRod(golf, seaLevel) });
                EditorUtility.SetDirty(trunk);
            }
            else
            {
                Debug.LogWarning("[VillageSceneSetup] VWGolf has no Tailgate - re-export VWGolf.fbx from build_golf.py, then rerun the Golf setup steps.");
            }

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

        /// <summary>
        /// A cardboard crate of twelve brown bottles on the Golf's boot floor (build_golf.py: top at y=0.55,
        /// centred 1.66 m behind the car origin). Carried by its top handle.
        /// </summary>
        private static PickupItem BuildBeerBox(Transform golf)
        {
            Transform old = golf.Find("BeerBox");
            if (old != null)
            {
                UnityEngine.Object.DestroyImmediate(old.gameObject);
            }

            const string dir = "Assets/Props/Beer";
            Material card = BoxMaterial($"{dir}/Mat_BeerBox_Cardboard.mat", "#9C7246", 0.15f);
            Material band = BoxMaterial($"{dir}/Mat_BeerBox_Label.mat", "#1F4E8C", 0.3f);
            Material glass = AssetDatabase.LoadAssetAtPath<Material>($"{dir}/Mat_Beer_Glass.mat") ?? BoxMaterial($"{dir}/Mat_Beer_Glass.mat", "#4A2A0C", 0.88f);
            Material cap = AssetDatabase.LoadAssetAtPath<Material>($"{dir}/Mat_Beer_Cap.mat") ?? BoxMaterial($"{dir}/Mat_Beer_Cap.mat", "#C9A227", 0.7f);

            const float w = 0.40f, d = 0.27f, h = 0.24f;
            var root = new GameObject("BeerBox").transform;
            root.SetParent(golf, false);
            root.localPosition = new Vector3(0f, 0.55f + h * 0.5f, -1.66f);
            root.localRotation = Quaternion.identity;

            BoxPart(PrimitiveType.Cube, "Crate", root, Vector3.zero, new Vector3(w, h, d), card);
            BoxPart(PrimitiveType.Cube, "LabelL", root, new Vector3(-w * 0.5f - 0.001f, 0f, 0f), new Vector3(0.002f, h * 0.35f, d * 0.8f), band);
            BoxPart(PrimitiveType.Cube, "LabelR", root, new Vector3(w * 0.5f + 0.001f, 0f, 0f), new Vector3(0.002f, h * 0.35f, d * 0.8f), band);
            BoxPart(PrimitiveType.Cube, "LabelB", root, new Vector3(0f, 0f, -d * 0.5f - 0.001f), new Vector3(w * 0.8f, h * 0.35f, 0.002f), band);
            BoxPart(PrimitiveType.Cube, "LabelF", root, new Vector3(0f, 0f, d * 0.5f + 0.001f), new Vector3(w * 0.8f, h * 0.35f, 0.002f), band);
            BoxPart(PrimitiveType.Cube, "Handle", root, new Vector3(0f, h * 0.5f + 0.05f, 0f), new Vector3(0.03f, 0.10f, d * 0.95f), card);
            var tops = new List<GameObject>();
            for (int ix = 0; ix < 4; ix++)
            {
                for (int iz = 0; iz < 3; iz++)
                {
                    float x = (ix - 1.5f) * (w / 4f), z = (iz - 1f) * (d / 3f);
                    var top = new GameObject($"Bottle_{tops.Count}").transform;
                    top.SetParent(root, false);
                    top.localPosition = new Vector3(x, h * 0.5f, z);
                    BoxPart(PrimitiveType.Cylinder, "Neck", top, new Vector3(0f, 0.03f, 0f), new Vector3(0.028f, 0.03f, 0.028f), glass);
                    BoxPart(PrimitiveType.Cylinder, "Cap", top, new Vector3(0f, 0.063f, 0f), new Vector3(0.03f, 0.004f, 0.03f), cap);
                    tops.Add(top.gameObject);
                }
            }

            // Every beer taken from the box is a copy of the players' own bottle, kept hidden in the box.
            Transform carBeer = golf.root.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == "Beer" && t.GetComponent<PickupItem>() != null);
            PickupItem template = null;
            if (carBeer != null)
            {
                var copy = UnityEngine.Object.Instantiate(carBeer.gameObject, root, false);
                copy.name = "BeerTemplate";
                copy.transform.localPosition = Vector3.zero;
                copy.transform.localRotation = Quaternion.identity;
                copy.transform.localScale = carBeer.lossyScale;
                foreach (var extra in copy.GetComponentsInChildren<Component>(true).Where(c => !(c is Transform || c is MeshFilter || c is MeshRenderer || c is Collider || c is PickupItem)).ToArray())
                {
                    UnityEngine.Object.DestroyImmediate(extra);
                }
                template = copy.GetComponent<PickupItem>();
                copy.SetActive(false);
            }
            else
            {
                Debug.LogWarning("[VillageSceneSetup] no player beer found to copy - the beer box will not hand out beers.");
            }

            var trigger = root.gameObject.AddComponent<BoxCollider>();
            trigger.isTrigger = true;
            trigger.center = new Vector3(0f, 0.04f, 0f);
            trigger.size = new Vector3(w, h + 0.1f, d);

            var item = root.gameObject.AddComponent<PickupItem>();
            item.Configure("Beer box", -(h * 0.5f + 0.08f), h * 0.5f, true, "Use", false);   // fist in the top handle
            EditorUtility.SetDirty(item);
            var crate = root.gameObject.AddComponent<BeerCrate>();
            crate.Configure(template, tops.ToArray());
            EditorUtility.SetDirty(crate);
            return item;
        }

        /// <summary>
        /// A portable boombox beside the beer box in the trunk. Its playlist is every AudioClip in
        /// Assets/Audio/Radio/ (empty for now), so new songs only need dropping there and a village rebuild.
        /// </summary>
        private static PickupItem BuildRadio(Transform golf)
        {
            Transform old = golf.Find("Radio");
            if (old != null)
            {
                UnityEngine.Object.DestroyImmediate(old.gameObject);
            }

            const string dir = "Assets/Props/Radio";
            const string songDir = "Assets/Audio/Radio";
            Directory.CreateDirectory(dir);
            Directory.CreateDirectory(songDir);
            AssetDatabase.Refresh();
            Material shell = BoxMaterial($"{dir}/Mat_Radio_Shell.mat", "#2B2E33", 0.35f);
            Material grille = BoxMaterial($"{dir}/Mat_Radio_Grille.mat", "#111214", 0.1f);
            Material trim = BoxMaterial($"{dir}/Mat_Radio_Trim.mat", "#B8BCC2", 0.75f);
            Material dial = BoxMaterial($"{dir}/Mat_Radio_Dial.mat", "#E0A43A", 0.5f);

            const float w = 0.30f, h = 0.17f, d = 0.10f;
            var root = new GameObject("Radio").transform;
            root.SetParent(golf, false);
            root.localPosition = new Vector3(-0.36f, 0.55f + h * 0.5f, -1.66f);
            root.localRotation = Quaternion.identity;   // speakers face out of the trunk (-Z)

            BoxPart(PrimitiveType.Cube, "Body", root, Vector3.zero, new Vector3(w, h, d), shell);
            foreach (float sx in new[] { -0.085f, 0.085f })
            {
                var speaker = new GameObject("Speaker").transform;
                speaker.SetParent(root, false);
                speaker.localPosition = new Vector3(sx, -0.01f, -d * 0.5f - 0.003f);
                speaker.localRotation = Quaternion.Euler(90f, 0f, 0f);
                BoxPart(PrimitiveType.Cylinder, "Ring", speaker, Vector3.zero, new Vector3(0.105f, 0.004f, 0.105f), trim);
                BoxPart(PrimitiveType.Cylinder, "Cone", speaker, new Vector3(0f, -0.002f, 0f), new Vector3(0.09f, 0.004f, 0.09f), grille);
            }
            BoxPart(PrimitiveType.Cube, "Display", root, new Vector3(0f, 0.05f, -d * 0.5f - 0.002f), new Vector3(0.07f, 0.03f, 0.004f), dial);
            BoxPart(PrimitiveType.Cube, "Handle", root, new Vector3(0f, h * 0.5f + 0.035f, 0f), new Vector3(w * 0.8f, 0.012f, 0.02f), trim);
            foreach (float sx in new[] { -w * 0.38f, w * 0.38f })
            {
                BoxPart(PrimitiveType.Cube, "HandlePost", root, new Vector3(sx, h * 0.5f + 0.017f, 0f), new Vector3(0.012f, 0.035f, 0.02f), trim);
            }
            BoxPart(PrimitiveType.Cube, "Antenna", root, new Vector3(w * 0.42f, h * 0.5f + 0.09f, 0.02f), new Vector3(0.006f, 0.18f, 0.006f), trim);

            var trigger = root.gameObject.AddComponent<BoxCollider>();
            trigger.isTrigger = true;
            trigger.center = new Vector3(0f, 0.03f, 0f);
            trigger.size = new Vector3(w, h + 0.07f, d + 0.04f);

            root.gameObject.AddComponent<AudioSource>();
            var radio = root.gameObject.AddComponent<RadioPlayer>();
            AudioClip[] songs = AssetDatabase.FindAssets("t:AudioClip", new[] { songDir })
                .Select(g => AssetDatabase.LoadAssetAtPath<AudioClip>(AssetDatabase.GUIDToAssetPath(g)))
                .Where(c => c != null).OrderBy(c => c.name).ToArray();
            radio.Configure(songs);
            EditorUtility.SetDirty(radio);

            var item = root.gameObject.AddComponent<PickupItem>();
            item.Configure("Radio", -(h * 0.5f + 0.035f), h * 0.5f, true, "Play", false);   // fist on the top handle
            EditorUtility.SetDirty(item);
            return item;
        }

        /// <summary>
        /// A telescopic spinning rod resting beside the beer box in the trunk; collapses to fit inside
        /// the boot while stowed, extends when picked up, and casts a bobber into the fjord or river.
        /// </summary>
        private static PickupItem BuildFishingRod(Transform golf, float seaLevel)
        {
            Transform old = golf.Find("FishingRod");
            if (old != null)
            {
                UnityEngine.Object.DestroyImmediate(old.gameObject);
            }

            const string dir = "Assets/Props/FishingRod";
            Directory.CreateDirectory(dir);
            AssetDatabase.Refresh();
            Material cork = BoxMaterial($"{dir}/Mat_FishingRod_Cork.mat", "#B5895A", 0.22f);
            Material graphite = BoxMaterial($"{dir}/Mat_FishingRod_Graphite.mat", "#1D262D", 0.68f);
            Material metal = BoxMaterial($"{dir}/Mat_FishingRod_Metal.mat", "#B8BCC2", 0.78f);
            Material spool = BoxMaterial($"{dir}/Mat_FishingRod_Spool.mat", "#C9A227", 0.65f);
            Material floatRed = BoxMaterial($"{dir}/Mat_FishingRod_FloatRed.mat", "#D15A32", 0.55f);
            Material floatWhite = BoxMaterial($"{dir}/Mat_FishingRod_FloatWhite.mat", "#EDEAE2", 0.55f);
            Material fishMat = BoxMaterial($"{dir}/Mat_FishingRod_Fish.mat", "#6E828F", 0.72f);
            Material lineMat = BoxMaterial($"{dir}/Mat_FishingRod_Line.mat", "#D8E0E5", 0.40f);

            var root = new GameObject("FishingRod").transform;
            root.SetParent(golf, false);
            root.localPosition = new Vector3(0.32f, 0.585f, -1.66f);
            root.localRotation = Quaternion.identity;

            var rodRig = new GameObject("RodRig").transform;
            rodRig.SetParent(root, false);

            BoxPart(PrimitiveType.Cylinder, "ButtCap", rodRig, new Vector3(0f, -0.105f, 0f), new Vector3(0.026f, 0.015f, 0.026f), metal);
            BoxPart(PrimitiveType.Cylinder, "CorkRearGrip", rodRig, new Vector3(0f, -0.045f, 0f), new Vector3(0.024f, 0.045f, 0.024f), cork);
            BoxPart(PrimitiveType.Cylinder, "ReelSeat", rodRig, new Vector3(0f, 0.025f, 0f), new Vector3(0.022f, 0.025f, 0.022f), metal);
            BoxPart(PrimitiveType.Cylinder, "CorkForeGrip", rodRig, new Vector3(0f, 0.075f, 0f), new Vector3(0.023f, 0.025f, 0.023f), cork);

            // Spinning reel under/ahead of the grip (-X becomes downward/forward when carried at +46 deg).
            BoxPart(PrimitiveType.Cube, "ReelArm", rodRig, new Vector3(-0.028f, 0.025f, 0f), new Vector3(0.045f, 0.014f, 0.014f), graphite);
            BoxPart(PrimitiveType.Cube, "ReelBody", rodRig, new Vector3(-0.052f, 0.025f, 0f), new Vector3(0.036f, 0.044f, 0.034f), graphite);
            BoxPart(PrimitiveType.Cylinder, "ReelSpool", rodRig, new Vector3(-0.052f, 0.055f, 0f), new Vector3(0.034f, 0.016f, 0.034f), spool);

            var reelCrank = new GameObject("ReelCrank").transform;
            reelCrank.SetParent(rodRig, false);
            reelCrank.localPosition = new Vector3(-0.052f, 0.020f, 0.020f);
            BoxPart(PrimitiveType.Cube, "CrankArm", reelCrank, new Vector3(0f, -0.012f, 0f), new Vector3(0.006f, 0.028f, 0.006f), metal);
            var knob = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            knob.name = "CrankKnob";
            UnityEngine.Object.DestroyImmediate(knob.GetComponent<Collider>());
            knob.transform.SetParent(reelCrank, false);
            knob.transform.localPosition = new Vector3(0f, -0.025f, 0.008f);
            knob.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            knob.transform.localScale = new Vector3(0.010f, 0.008f, 0.010f);
            knob.GetComponent<MeshRenderer>().sharedMaterial = cork;

            BoxPart(PrimitiveType.Cylinder, "LowerBlank", rodRig, new Vector3(0f, 0.30f, 0f), new Vector3(0.015f, 0.20f, 0.015f), graphite);
            BoxPart(PrimitiveType.Cylinder, "Guide0", rodRig, new Vector3(-0.014f, 0.48f, 0f), new Vector3(0.020f, 0.003f, 0.020f), metal);

            var telescopic = new GameObject("TelescopicSection").transform;
            telescopic.SetParent(rodRig, false);
            telescopic.localPosition = new Vector3(0f, 0.50f, 0f);
            BoxPart(PrimitiveType.Cylinder, "UpperBlank", telescopic, new Vector3(0f, 0.26f, 0f), new Vector3(0.010f, 0.26f, 0.010f), graphite);
            BoxPart(PrimitiveType.Cylinder, "TipBlank", telescopic, new Vector3(0f, 0.73f, 0f), new Vector3(0.006f, 0.23f, 0.006f), graphite);
            BoxPart(PrimitiveType.Cylinder, "Guide1", telescopic, new Vector3(-0.011f, 0.22f, 0f), new Vector3(0.015f, 0.003f, 0.015f), metal);
            BoxPart(PrimitiveType.Cylinder, "Guide2", telescopic, new Vector3(-0.009f, 0.46f, 0f), new Vector3(0.013f, 0.003f, 0.013f), metal);
            BoxPart(PrimitiveType.Cylinder, "Guide3", telescopic, new Vector3(-0.007f, 0.70f, 0f), new Vector3(0.011f, 0.003f, 0.011f), metal);
            BoxPart(PrimitiveType.Cylinder, "Guide4", telescopic, new Vector3(-0.005f, 0.955f, 0f), new Vector3(0.009f, 0.003f, 0.009f), metal);

            var rodTip = new GameObject("RodTip").transform;
            rodTip.SetParent(telescopic, false);
            rodTip.localPosition = new Vector3(0f, 0.96f, 0f);

            var bobber = new GameObject("Bobber").transform;
            bobber.SetParent(root, false);
            BoxPart(PrimitiveType.Sphere, "FloatRed", bobber, new Vector3(0f, 0.016f, 0f), new Vector3(0.032f, 0.036f, 0.032f), floatRed);
            BoxPart(PrimitiveType.Sphere, "FloatWhite", bobber, new Vector3(0f, -0.012f, 0f), new Vector3(0.030f, 0.034f, 0.030f), floatWhite);
            BoxPart(PrimitiveType.Cylinder, "Antenna", bobber, new Vector3(0f, 0.040f, 0f), new Vector3(0.005f, 0.015f, 0.005f), floatRed);
            BoxPart(PrimitiveType.Sphere, "Sinker", bobber, new Vector3(0f, -0.065f, 0f), new Vector3(0.014f, 0.018f, 0.014f), metal);

            var caughtFish = new GameObject("CaughtFish").transform;
            caughtFish.SetParent(bobber, false);
            BoxPart(PrimitiveType.Sphere, "FishBody", caughtFish, new Vector3(0f, -0.10f, 0f), new Vector3(0.055f, 0.22f, 0.095f), fishMat);
            BoxPart(PrimitiveType.Cube, "FishTail", caughtFish, new Vector3(0f, -0.22f, 0f), new Vector3(0.014f, 0.065f, 0.065f), fishMat);
            BoxPart(PrimitiveType.Cube, "DorsalFin", caughtFish, new Vector3(0f, -0.10f, -0.05f), new Vector3(0.010f, 0.075f, 0.045f), fishMat);
            caughtFish.gameObject.SetActive(false);

            var line = root.gameObject.AddComponent<LineRenderer>();
            line.widthMultiplier = 0.004f;
            line.useWorldSpace = true;
            line.positionCount = 8;
            line.sharedMaterial = lineMat;
            line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

            var trigger = root.gameObject.AddComponent<BoxCollider>();
            trigger.isTrigger = true;
            trigger.center = new Vector3(-0.06f, 0.04f, 0.08f);
            trigger.size = new Vector3(0.30f, 0.18f, 0.48f);

            var item = root.gameObject.AddComponent<PickupItem>();
            item.Configure("Fishing rod", 0f, 0.04f, true, "Cast", false);
            EditorUtility.SetDirty(item);

            var rod = root.gameObject.AddComponent<FishingRod>();
            rod.Configure(rodRig, telescopic, rodTip, reelCrank, bobber, caughtFish, line, seaLevel);
            rod.ApplyPose();
            EditorUtility.SetDirty(rod);
            return item;
        }

        private static void BoxPart(PrimitiveType type, string name, Transform parent, Vector3 pos, Vector3 scale, Material mat)
        {
            var go = GameObject.CreatePrimitive(type);
            go.name = name;
            UnityEngine.Object.DestroyImmediate(go.GetComponent<Collider>());
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            go.transform.localScale = scale;
            go.GetComponent<MeshRenderer>().sharedMaterial = mat;
        }

        private static Material BoxMaterial(string path, string hex, float smoothness)
        {
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
                AssetDatabase.CreateAsset(mat, path);
            }

            ColorUtility.TryParseHtmlString(hex, out Color c);
            mat.SetColor("_BaseColor", c);
            mat.SetFloat("_Smoothness", smoothness);
            EditorUtility.SetDirty(mat);
            return mat;
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

            BuildChurchParkingApron(data, footpathsRoot.transform, gravelMat);

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
                    Vector3[] outline = z.outline ?? Array.Empty<Vector3>();
                    if (z.name == "Beach" && outline.Length >= 3)
                    {
                        outline = StraightenBeachZoneOutline(outline, data.seaLevel);
                    }

                    zones.Add(new WalkableArea.Zone
                    {
                        name = z.name,
                        outline = outline,
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

        private static Vector3[] DensifyPath(Vector3[] pts, float maxStep)
        {
            var result = new List<Vector3>(pts.Length * 4) { pts[0] };
            for (int i = 1; i < pts.Length; i++)
            {
                Vector3 a = pts[i - 1];
                Vector3 b = pts[i];
                int steps = Mathf.Max(1, Mathf.CeilToInt(new Vector2(b.x - a.x, b.z - a.z).magnitude / maxStep));
                for (int s = 1; s <= steps; s++)
                {
                    result.Add(Vector3.Lerp(a, b, s / (float)steps));
                }
            }

            return result.ToArray();
        }

        /// <summary>
        /// Builds a wide, gently sloped gravel parking apron connecting the church parking bay
        /// directly down to VillageRoad so players can exit anywhere along the parking area.
        /// </summary>
        private static void BuildChurchParkingApron(WalkableFile data, Transform parent, Material gravelMat)
        {
            ZoneData zone = data.zones?.FirstOrDefault(z => z.name == "ChurchParking");
            Vector3[] bay = data.corridors?.FirstOrDefault(c => c.name == "ParkingBay")?.points;
            Vector3[] driveway = data.corridors?.FirstOrDefault(c => c.name == "ChurchDriveway")?.points;
            Vector3[] road = data.corridors?.FirstOrDefault(c => c.name == "VillageRoad")?.points;
            if (zone == null || zone.outline == null || zone.outline.Length < 3 || bay == null || road == null)
            {
                return;
            }

            Vector3[] upperLine = driveway != null ? driveway.Concat(bay).ToArray() : bay;
            const float Step = 0.75f;
            float xMin = zone.outline.Min(p => p.x) - Step * 2f;
            float xMax = zone.outline.Max(p => p.x) + Step * 2f;
            float zMin = zone.outline.Min(p => p.z) - Step * 2f;
            float zMax = zone.outline.Max(p => p.z) + Step * 2f;

            int nx = Mathf.CeilToInt((xMax - xMin) / Step) + 1;
            int nz = Mathf.CeilToInt((zMax - zMin) / Step) + 1;
            var inside = new bool[nx * nz];
            var verts = new Vector3[nx * nz];
            var uvs = new Vector2[nx * nz];

            for (int j = 0; j < nz; j++)
            {
                float z = zMin + j * Step;
                for (int i = 0; i < nx; i++)
                {
                    float x = xMin + i * Step;
                    int idx = j * nx + i;
                    bool inZone = PointInPolygonXZ(x, z, zone.outline);
                    inside[idx] = inZone;

                    float groundY = SurfaceY(new Vector3(x, 0f, z), includeFootpaths: false);
                    float y;
                    if (inZone)
                    {
                        ClosestOnPolyline(x, z, upperLine, out float dBay, out float yBay);
                        ClosestOnPolyline(x, z, road, out float dRoad, out float yRoad);
                        float t = dBay / Mathf.Max(0.001f, dBay + dRoad);
                        y = Mathf.Lerp(yBay, yRoad, Mathf.SmoothStep(0f, 1f, t));
                        if (dBay < 2.2f)
                        {
                            y = Mathf.Min(y, yBay - 0.015f);
                        }
                        else if (dRoad < 1.6f)
                        {
                            y = Mathf.Min(y, yRoad - 0.02f);
                        }
                        else
                        {
                            y = Mathf.Max(y, groundY + 0.04f);
                        }
                    }
                    else
                    {
                        y = groundY - 0.35f;
                    }

                    verts[idx] = new Vector3(x, y, z);
                    uvs[idx] = new Vector2((x - xMin) / 4f, (z - zMin) / 4f);
                }
            }

            var tris = new List<int>(nx * nz * 6);
            for (int j = 0; j < nz - 1; j++)
            {
                for (int i = 0; i < nx - 1; i++)
                {
                    int a = j * nx + i;
                    int b = a + 1;
                    int c = a + nx;
                    int d = c + 1;
                    if (!inside[a] && !inside[b] && !inside[c] && !inside[d])
                    {
                        continue;
                    }

                    tris.Add(a);
                    tris.Add(c);
                    tris.Add(d);
                    tris.Add(a);
                    tris.Add(d);
                    tris.Add(b);
                }
            }

            var mesh = new Mesh { name = "ChurchParkingApron" };
            mesh.vertices = verts;
            mesh.uv = uvs;
            mesh.triangles = tris.ToArray();
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();

            string meshPath = $"{VillageAssetDir}/ChurchParkingApron.asset";
            AssetDatabase.DeleteAsset(meshPath);
            AssetDatabase.CreateAsset(mesh, meshPath);

            var go = new GameObject("ChurchParkingApron") { isStatic = true };
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = gravelMat;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            go.AddComponent<MeshCollider>().sharedMesh = mesh;
        }

        private static void ClosestOnPolyline(float x, float z, Vector3[] line, out float bestDist, out float bestY)
        {
            bestDist = float.MaxValue;
            bestY = 0f;
            Vector2 p = new Vector2(x, z);
            for (int k = 0; k < line.Length - 1; k++)
            {
                Vector2 a = new Vector2(line[k].x, line[k].z);
                Vector2 b = new Vector2(line[k + 1].x, line[k + 1].z);
                Vector2 ab = b - a;
                float t = ab.sqrMagnitude < 1e-6f ? 0f : Mathf.Clamp01(Vector2.Dot(p - a, ab) / ab.sqrMagnitude);
                float d = Vector2.Distance(p, a + ab * t);
                if (d < bestDist)
                {
                    bestDist = d;
                    bestY = Mathf.Lerp(line[k].y, line[k + 1].y, t);
                }
            }
        }

        private static bool PointInPolygonXZ(float x, float z, Vector3[] poly)
        {
            bool inside = false;
            for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
            {
                float xi = poly[i].x, zi = poly[i].z;
                float xj = poly[j].x, zj = poly[j].z;
                if (((zi > z) != (zj > z)) && (x < (xj - xi) * (z - zi) / (zj - zi + 1e-9f) + xi))
                {
                    inside = !inside;
                }
            }

            return inside;
        }

        private static Vector3[] BuildFootpathMeshAndGetPoints(FootpathData fp, Transform parent, Material gravelMat)
        {
            // Resample to ~0.75 m so the flat ribbon can't span terrain bumps that poke through as grass.
            Vector3[] src = DensifyPath(fp.points, 0.75f);
            int count = src.Length;
            var distances = new float[count];
            for (int i = 1; i < count; i++)
            {
                float dx = src[i].x - src[i - 1].x;
                float dz = src[i].z - src[i - 1].z;
                distances[i] = distances[i - 1] + Mathf.Sqrt(dx * dx + dz * dz);
            }

            const float MaxRampGrade = 0.85f;
            var centerHeights = new float[count];
            for (int i = 0; i < count; i++)
            {
                float groundY = SurfaceY(src[i], includeFootpaths: false);
                centerHeights[i] = groundY + 0.06f;
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

                // The ribbon top is planar between centre and edge, so lift each half above the highest ground across it.
                float leftMid = SurfaceY(c - side * halfRibbon * 0.5f, includeFootpaths: false);
                float rightMid = SurfaceY(c + side * halfRibbon * 0.5f, includeFootpaths: false);
                c.y = Mathf.Max(c.y, Mathf.Max(leftMid, rightMid) + 0.06f);
                centerLine[i] = c;
                leftTopHeights[i] = Mathf.Max(c.y, Mathf.Max(leftGround, leftMid) + 0.06f);
                rightTopHeights[i] = Mathf.Max(c.y, Mathf.Max(rightGround, rightMid) + 0.06f);

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
        private const string BeachTerrainMeshPath = VillageAssetDir + "/Exit_Terrain_VillageBeach.asset";
        private const string TerrainDetailPath = "Assets/Environment/TunnelExit/Textures/Exit_Terrain_Detail.png";

        private static void GetBeachEnvelope(float z, out float xTop, out float xShore)
        {
            float houseGuard = 3.8f * Mathf.Exp(-Mathf.Pow((z - 324.5f) / 7.5f, 2f));
            float northGuard = 2.5f * Smooth(385f, 405f, z);
            xTop = 415.0f + houseGuard + northGuard;
            float tz = Mathf.Clamp01((z - 308f) / (403f - 308f));
            xShore = Mathf.Lerp(451.5f, 430.0f, tz);
        }

        private static Vector3[] StraightenBeachZoneOutline(Vector3[] outline, float seaLevel)
        {
            int nwIdx = -1;
            for (int i = 1; i < outline.Length; i++)
            {
                if (outline[i].z >= 400f && outline[i].x < 420f)
                {
                    nwIdx = i;
                    break;
                }
            }

            if (nwIdx <= 1)
            {
                return outline;
            }

            float zSouth = outline[0].z;
            float zNorth = outline[nwIdx].z;
            GetBeachEnvelope(zSouth, out _, out float xShoreSouth);
            GetBeachEnvelope(zNorth, out _, out float xShoreNorth);

            var result = new List<Vector3>(outline.Length - (nwIdx - 1) + 2)
            {
                outline[0],
                new Vector3(xShoreSouth - 0.8f, seaLevel + 0.1f, zSouth),
                new Vector3(xShoreNorth - 0.8f, seaLevel + 0.1f, zNorth),
            };
            for (int i = nwIdx; i < outline.Length; i++)
            {
                result.Add(outline[i]);
            }

            return result.ToArray();
        }

        /// <summary>
        /// The street is a ribbon lying above the coarse terrain, so on the seaward side it ends in a
        /// ledge the players can drop off but not climb back up. Lift the grass beside it into a
        /// bank that meets the road edge. Skips the bridge, where the gap is the Stórá itself.
        /// </summary>
        private static bool RaiseToRoadEdge(ref Vector3 w, Vector3[] road)
        {
            const float RoadHalf = 1.8f, Reach = 11f, BankSlope = 0.35f, Under = 0.12f, MaxGap = 3.5f;
            if (road == null || road.Length < 2 || w.x < 375f || w.x > 418f || w.z < 276f || w.z > 445f || (w.z >= 301f && w.z <= 311.5f))
            {
                return false;
            }

            float best = float.MaxValue, roadY = 0f;
            for (int k = 0; k < road.Length - 1; k++)
            {
                Vector2 a = new Vector2(road[k].x, road[k].z), b = new Vector2(road[k + 1].x, road[k + 1].z);
                Vector2 ab = b - a, p = new Vector2(w.x, w.z);
                float t = ab.sqrMagnitude < 1e-6f ? 0f : Mathf.Clamp01(Vector2.Dot(p - a, ab) / ab.sqrMagnitude);
                float d = Vector2.Distance(p, a + ab * t);
                if (d < best)
                {
                    best = d;
                    roadY = Mathf.Lerp(road[k].y, road[k + 1].y, t);
                }
            }

            if (best > Reach || roadY - w.y > MaxGap)
            {
                return false;
            }

            float target = roadY - Under - Mathf.Max(0f, best - RoadHalf) * BankSlope;
            if (target <= w.y)
            {
                return false;
            }

            w.y = target;
            return true;
        }

        private static void FitTerrainUnderBeach(GameObject terrainGo, float sea, Vector3[] road)
        {
            if (terrainGo == null || !terrainGo.TryGetComponent(out MeshFilter mf) || mf.sharedMesh == null)
            {
                return;
            }

            Mesh source = mf.sharedMesh;
            Transform tf = terrainGo.transform;
            Vector3[] local = source.vertices;
            int lowered = 0;

            for (int i = 0; i < local.Length; i++)
            {
                Vector3 w = tf.TransformPoint(local[i]);
                if (RaiseToRoadEdge(ref w, road))
                {
                    local[i] = tf.InverseTransformPoint(w);
                    lowered++;
                    continue;
                }

                if (w.x < 420f || w.x > 456f || w.z < 308f || w.z > 405f)
                {
                    continue;
                }

                GetBeachEnvelope(w.z, out float xTop, out float xShore);
                float yTop = -85.6f - 0.35f * Mathf.Sin((w.z - 310f) / 90f * Mathf.PI);
                float width = Mathf.Max(12f, xShore - xTop);
                float u = (w.x - xTop) / width;

                float targetY;
                if (u <= 1f)
                {
                    float s = 1f - Mathf.Pow(1f - Mathf.Clamp01(u), 1.35f);
                    targetY = Mathf.Lerp(yTop, sea, s) - 0.14f;
                }
                else
                {
                    targetY = sea - (w.x - xShore) * 0.14f - 0.14f;
                }

                if (w.x < 425f && w.z >= 318f && w.z <= 330f)
                {
                    targetY = Mathf.Max(targetY, w.y - 0.35f);
                }

                if (targetY < w.y)
                {
                    w.y = targetY;
                    local[i] = tf.InverseTransformPoint(w);
                    lowered++;
                }
            }

            if (lowered == 0)
            {
                return;
            }

            Mesh fitted = UnityEngine.Object.Instantiate(source);
            fitted.name = "Exit_Terrain_VillageBeach";
            fitted.vertices = local;
            fitted.RecalculateNormals();
            fitted.RecalculateBounds();
            AssetDatabase.DeleteAsset(BeachTerrainMeshPath);
            AssetDatabase.CreateAsset(fitted, BeachTerrainMeshPath);

            mf.sharedMesh = fitted;
            if (terrainGo.TryGetComponent(out MeshCollider mc))
            {
                mc.sharedMesh = fitted;
            }

            Physics.SyncTransforms();
        }

        /// <summary>
        /// Presses Exit_Terrain down beneath Exit_Road and ChurchForecourt along the village corridors
        /// so coarse hillside triangles don't poke through the asphalt.
        /// </summary>
        private static void FitTerrainUnderVillageRoads(WalkableFile data)
        {
            GameObject terrainGo = GameObject.Find("Exit_Terrain");
            if (terrainGo == null || !terrainGo.TryGetComponent(out MeshFilter terrainFilter) || terrainFilter.sharedMesh == null)
            {
                return;
            }

            Vector3[] route = data.corridors?
                .Where(c => c.points != null)
                .SelectMany(c => c.points)
                .ToArray();
            if (route == null || route.Length == 0)
            {
                return;
            }

            Collider[] surfaces = UnityEngine.Object.FindObjectsByType<MeshCollider>(FindObjectsInactive.Include)
                .Where(c => c.name == "Exit_Road" || c.name == "ChurchForecourt")
                .Cast<Collider>()
                .ToArray();
            if (surfaces.Length == 0)
            {
                return;
            }

            int fitted = RoadTerrainFit.Apply(terrainFilter, surfaces, route, 80f, BeachTerrainMeshPath);
            Debug.Log($"[VillageSceneSetup] lowered {fitted} terrain vertices under village roads");
            Physics.SyncTransforms();
        }

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
            FitTerrainUnderBeach(terrainGo, sea, data.corridors?.FirstOrDefault(c => c.name == "VillageRoad")?.points);
            Collider terrain = terrainGo != null ? terrainGo.GetComponent<Collider>() : null;

            var rawH = new float[nx * nz];
            for (int j = 0; j < nz; j++)
            {
                float z = z0 + j * BeachGridStep;
                for (int i = 0; i < nx; i++)
                {
                    float x = x0 + i * BeachGridStep;
                    if (terrain != null && terrain.Raycast(new Ray(new Vector3(x, 40f, z), Vector3.down), out RaycastHit hit, 220f))
                    {
                        rawH[j * nx + i] = hit.point.y;
                    }
                    else
                    {
                        rawH[j * nx + i] = SurfaceY(new Vector3(x, 0f, z), includeFootpaths: false);
                    }
                }
            }

            // ---- flatten the beach into a straight, gentle coastal slope down to the shoreline
            var rawTop = new float[nz];
            var xTops = new float[nz];
            var xShores = new float[nz];
            for (int j = 0; j < nz; j++)
            {
                float z = z0 + j * BeachGridStep;
                GetBeachEnvelope(z, out float xTop, out float xShore);
                xTops[j] = xTop;
                xShores[j] = xShore;
                float sampledTop = SampleGrid(rawH, nx, nz, (xTop - x0) / BeachGridStep, j);
                rawTop[j] = Mathf.Min(sampledTop, sea + 3.65f);
            }

            var yTop = new float[nz];
            Array.Copy(rawTop, yTop, nz);
            for (int pass = 0; pass < 16; pass++)
            {
                var next = new float[nz];
                next[0] = yTop[0];
                next[nz - 1] = yTop[nz - 1];
                for (int j = 1; j < nz - 1; j++)
                {
                    next[j] = 0.25f * yTop[j - 1] + 0.5f * yTop[j] + 0.25f * yTop[j + 1];
                }

                yTop = next;
            }

            var heights = new float[nx * nz];
            for (int j = 0; j < nz; j++)
            {
                float z = z0 + j * BeachGridStep;
                float xTop = xTops[j];
                float xShore = xShores[j];
                float width = Mathf.Max(12f, xShore - xTop);
                float shoreSlope = Mathf.Max(0.11f, (yTop[j] - sea) / width * 0.85f);
                float alongBlend = Smooth(z0 + 2f, z0 + 10f, z) * (1f - Smooth(z1 - 10f, z1 - 2f, z));

                for (int i = 0; i < nx; i++)
                {
                    int k = j * nx + i;
                    float x = x0 + i * BeachGridStep;
                    if (x <= xTop)
                    {
                        heights[k] = rawH[k];
                        continue;
                    }

                    float u = (x - xTop) / width;
                    float targetY;
                    if (u <= 1f)
                    {
                        float s = 1f - Mathf.Pow(1f - u, 1.35f);
                        targetY = Mathf.Lerp(yTop[j], sea, s);
                    }
                    else
                    {
                        targetY = Mathf.Max(sea - (x - xShore) * shoreSlope, sea - 8f);
                    }

                    float crossBlend = Smooth(0f, 3.5f, x - xTop);
                    float blendedHeight = Mathf.Lerp(rawH[k], targetY, alongBlend * crossBlend);
                    heights[k] = Mathf.Max(blendedHeight, rawH[k]);
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
                    float lift = BeachLift * Smooth(sea + 4.4f, sea + 3.2f, heights[k]);
                    verts[k] = new Vector3(x0 + i * BeachGridStep, heights[k] + lift, z0 + j * BeachGridStep);
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
            go.AddComponent<MeshCollider>().sharedMesh = mesh;
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

        // ------------------------------------------------------------------ shore rocks
        // Like the real shoreline: a band of basalt boulders and stones along the waterline,
        // from the north end of the beach out to the boathouse. One combined mesh, no colliders.
        private const string ShoreRockMeshPath = VillageAssetDir + "/ShoreRocks.asset";
        private const string ShoreRockMatPath = VillageAssetDir + "/Mat_ShoreRock.mat";
        private const string ShoreRockLightMatPath = VillageAssetDir + "/Mat_ShoreRock_Weathered.mat";

        private static void BuildShoreRocks(float sea)
        {
            GameObject existing = GameObject.Find("ShoreRocks");
            if (existing != null)
            {
                UnityEngine.Object.DestroyImmediate(existing);
            }

            GameObject terrainGo = GameObject.Find("Exit_Terrain");
            Collider terrain = terrainGo != null ? terrainGo.GetComponent<Collider>() : null;
            if (terrain == null)
            {
                Debug.LogWarning("[VillageSceneSetup] No Exit_Terrain - skipping shore rocks.");
                return;
            }

            bool Ground(float x, float z, out float y)
            {
                y = 0f;
                if (terrain.Raycast(new Ray(new Vector3(x, 40f, z), Vector3.down), out RaycastHit hit, 220f))
                {
                    y = hit.point.y;
                    return true;
                }

                return false;
            }

            var rng = new System.Random(4511);
            float R() => (float)rng.NextDouble();

            var prototypes = new List<Mesh>();
            for (int i = 0; i < 6; i++)
            {
                prototypes.Add(MakeRockPrototype(rng));
            }

            var dark = new CombineList();
            var light = new CombineList();

            const float zStart = 392f;
            const float zEnd = 452f;
            for (float z = zStart; z <= zEnd; z += 0.45f)
            {
                // shoreline: first point walking east where the terrain dips under the sea
                float shoreX = float.NaN;
                for (float x = 405f; x < 470f; x += 0.25f)
                {
                    if (Ground(x, z, out float sy) && sy < sea)
                    {
                        shoreX = x;
                        break;
                    }
                }

                if (float.IsNaN(shoreX))
                {
                    continue;
                }

                // fade in where the sand ends, and stop just short of the boathouse wall
                float density = Smooth(zStart, zStart + 10f, z) * (1f - Smooth(zEnd - 4f, zEnd, z));
                int perRow = 5 + rng.Next(4);
                for (int k = 0; k < perRow; k++)
                {
                    if (R() > density)
                    {
                        continue;
                    }

                    // spread from the bank down into the shallows, a few metres out at most
                    float across = Mathf.Lerp(-3.5f, 2.5f, R());
                    float x = shoreX + across;
                    float zz = z + (R() - 0.5f) * 0.6f;
                    if (!Ground(x, zz, out float gy) || gy < sea - 1.5f)
                    {
                        continue; // the seabed drops off fast; only the shallows get rocks
                    }

                    float big = R();
                    float size = big > 0.86f ? Mathf.Lerp(1.2f, 2.2f, R())
                        : big > 0.45f ? Mathf.Lerp(0.55f, 1.1f, R())
                        : Mathf.Lerp(0.2f, 0.5f, R());
                    var scale = new Vector3(size * Mathf.Lerp(0.85f, 1.35f, R()), size * Mathf.Lerp(0.45f, 0.8f, R()), size * Mathf.Lerp(0.85f, 1.3f, R()));
                    var rot = Quaternion.Euler((R() - 0.5f) * 25f, R() * 360f, (R() - 0.5f) * 25f);
                    float y = gy - scale.y * 0.3f;
                    if (gy < sea)
                    {
                        // in the shallows: stand the rock on the seabed but make it tall enough to break the surface
                        // the unit rock's top is ~0.45 above its pivot; poke out by a third of its size
                        float wantTop = sea + scale.y * 0.35f;
                        scale.y = Mathf.Max(scale.y, (wantTop - gy) / 0.4f);
                        scale.x = Mathf.Max(scale.x, scale.y * 1.1f);
                        scale.z = Mathf.Max(scale.z, scale.y * 1.1f);
                        y = gy - scale.y * 0.05f;
                    }

                    var pos = new Vector3(x, y, zz);
                    Mesh proto = prototypes[rng.Next(prototypes.Count)];
                    // wet rocks low down are dark; higher, drier ones are lighter and weathered
                    (gy < sea + 0.6f || R() < 0.4f ? dark : light).Add(proto, Matrix4x4.TRS(pos, rot, scale));
                }
            }

            Material darkMat = RockMaterial(ShoreRockMatPath, "Mat_ShoreRock", Palette.WetBasalt * 0.55f, 0.45f);
            Material lightMat = RockMaterial(ShoreRockLightMatPath, "Mat_ShoreRock_Weathered", Color.Lerp(Palette.WetBasalt, Palette.HarbourConcrete, 0.25f) * 0.8f, 0.15f);

            var combined = new Mesh { name = "ShoreRocks", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            combined.CombineMeshes(new[]
            {
                new CombineInstance { mesh = dark.Build(), transform = Matrix4x4.identity },
                new CombineInstance { mesh = light.Build(), transform = Matrix4x4.identity },
            }, mergeSubMeshes: false, useMatrices: true);
            combined.RecalculateBounds();
            AssetDatabase.DeleteAsset(ShoreRockMeshPath);
            AssetDatabase.CreateAsset(combined, ShoreRockMeshPath);

            var root = new GameObject("ShoreRocks") { isStatic = true };
            root.AddComponent<MeshFilter>().sharedMesh = combined;
            root.AddComponent<MeshRenderer>().sharedMaterials = new[] { darkMat, lightMat };
            Debug.Log($"[VillageSceneSetup] Shore rocks: {dark.Count + light.Count} rocks, {combined.vertexCount} verts.");
        }

        private const string BeachPebbleMeshPath = VillageAssetDir + "/BeachPebbles.asset";

        /// <summary>
        /// 20 hand-sized stones scattered on the dry sand. Each is its own <see cref="PickupItem"/> so the
        /// players can pick them up, carry and drop them like any other item.
        /// </summary>
        private static void BuildBeachPebbles(float sea, Vector3 avoid)
        {
            GameObject existing = GameObject.Find("BeachPebbles");
            if (existing != null) UnityEngine.Object.DestroyImmediate(existing);

            var rng = new System.Random(2077);
            float R() => (float)rng.NextDouble();

            // keep the shapes in one asset so the scene can reference them after the rebuild
            AssetDatabase.DeleteAsset(BeachPebbleMeshPath);
            var shapes = new List<Mesh>();
            for (int i = 0; i < 5; i++)
            {
                Mesh m = MakeRockPrototype(rng);
                m.name = $"BeachPebble_{i}";
                if (i == 0) AssetDatabase.CreateAsset(m, BeachPebbleMeshPath);
                else AssetDatabase.AddObjectToAsset(m, BeachPebbleMeshPath);
                shapes.Add(m);
            }

            Material mat = RockMaterial(ShoreRockLightMatPath, "Mat_ShoreRock_Weathered", Color.Lerp(Palette.WetBasalt, Palette.HarbourConcrete, 0.25f) * 0.8f, 0.15f);
            var root = new GameObject("BeachPebbles");
            int placed = 0;
            for (int attempt = 0; attempt < 400 && placed < 20; attempt++)
            {
                float z = Mathf.Lerp(316f, 392f, R());
                GetBeachEnvelope(z, out float xTop, out float xShore);
                float x = Mathf.Lerp(xTop + 1.5f, xShore - 2.5f, R());
                if (new Vector2(x - avoid.x, z - avoid.z).sqrMagnitude < 9f) continue;   // leave the sea mine clear
                if (!Physics.Raycast(new Vector3(x, 60f, z), Vector3.down, out RaycastHit hit, 200f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore)
                    || hit.collider.name != "VillageBeach" || hit.point.y < sea + 0.3f)
                {
                    continue;   // only on dry sand, not on rocks, props or in the water
                }

                float size = Mathf.Lerp(0.14f, 0.24f, R());
                var scale = new Vector3(size * Mathf.Lerp(0.9f, 1.3f, R()), size * Mathf.Lerp(0.55f, 0.8f, R()), size * Mathf.Lerp(0.9f, 1.25f, R()));
                var go = new GameObject($"BeachPebble_{placed + 1:00}");
                go.transform.SetParent(root.transform, false);
                go.transform.SetPositionAndRotation(hit.point + Vector3.up * scale.y * 0.3f, Quaternion.Euler(0f, R() * 360f, 0f));
                go.transform.localScale = scale;
                Mesh shape = shapes[rng.Next(shapes.Count)];
                go.AddComponent<MeshFilter>().sharedMesh = shape;
                go.AddComponent<MeshRenderer>().sharedMaterial = mat;
                go.AddComponent<MeshCollider>().sharedMesh = shape;
                go.GetComponent<MeshCollider>().convex = true;
                go.AddComponent<PickupItem>().Configure("Stone", 0f, scale.y * 0.3f, false, "Use", false);
                placed++;
            }

            Debug.Log($"[VillageSceneSetup] Beach pebbles: {placed} pickable stones.");
        }

        /// <summary>
        /// Each friend carries something of their own, hidden until taken out with I. Andrias: cigarettes and a lighter.
        /// </summary>
        private static void BuildPocketItems(string character, Transform charTransform)
        {
            var oldPockets = charTransform.GetComponent<CharacterPockets>();
            if (oldPockets != null)
            {
                foreach (PickupItem old in oldPockets.Items.ToArray()) if (old != null) UnityEngine.Object.DestroyImmediate(old.gameObject);
                UnityEngine.Object.DestroyImmediate(oldPockets);
            }

            if (character != "Andrias") return;
            var pockets = charTransform.gameObject.AddComponent<CharacterPockets>();
            pockets.Add(BuildCigarettePack());
            pockets.Add(BuildLighter());
            EditorUtility.SetDirty(pockets);
        }

        private const string PocketMeshPath = VillageAssetDir + "/PocketItems.asset";
        private const string CigPackLabelPath = VillageAssetDir + "/CigPack_Label.png";

        /// <summary>Saves a generated mesh into the shared pocket-items asset so the scene keeps it.</summary>
        private static Mesh KeepPocketMesh(Mesh mesh)
        {
            if (AssetDatabase.LoadMainAssetAtPath(PocketMeshPath) == null) AssetDatabase.CreateAsset(mesh, PocketMeshPath);
            else AssetDatabase.AddObjectToAsset(mesh, PocketMeshPath);
            return mesh;
        }

        private static GameObject PocketPart(string name, Transform parent, Mesh mesh, Material mat, Vector3 localPos, Vector3? scale = null, Quaternion? rot = null)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localRotation = rot ?? Quaternion.identity;
            go.transform.localScale = scale ?? Vector3.one;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = mat;
            return go;
        }

        /// <summary>Plain URP Lit material for small props (no terrain detail texture, unlike the rocks).</summary>
        private static Material PropMaterial(string file, Color color, float smoothness, float metallic = 0f, Texture2D albedo = null)
        {
            string path = VillageAssetDir + "/" + file + ".mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                mat = new Material(Shader.Find("Universal Render Pipeline/Lit")) { name = file };
                AssetDatabase.CreateAsset(mat, path);
            }

            color.a = 1f;
            mat.SetColor("_BaseColor", color);
            mat.SetTexture("_BaseMap", albedo);
            mat.SetFloat("_Smoothness", smoothness);
            mat.SetFloat("_Metallic", metallic);
            EditorUtility.SetDirty(mat);
            return mat;
        }

        /// <summary>Additive glowing material for the lighter flame (bright enough to catch the bloom).</summary>
        private static Material FlameMaterial(string file, Color color, float intensity)
        {
            string path = VillageAssetDir + "/" + file + ".mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                mat = new Material(Shader.Find("Universal Render Pipeline/Unlit")) { name = file };
                AssetDatabase.CreateAsset(mat, path);
            }

            mat.SetFloat("_Surface", 1f);
            mat.SetFloat("_Blend", 2f);
            mat.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.One);
            mat.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.One);
            mat.SetFloat("_ZWrite", 0f);
            mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
            mat.SetOverrideTag("RenderType", "Transparent");
            Color c = color * intensity;
            c.a = 1f;
            mat.SetColor("_BaseColor", c);
            EditorUtility.SetDirty(mat);
            return mat;
        }

        /// <summary>
        /// A box with rounded vertical edges (rounded-rectangle cross-section in XZ, extruded along Y), centred on
        /// its pivot. Side UVs wrap the perimeter (u 0..1 starting at the front-left corner, front → right → back →
        /// left) and v runs <paramref name="v0"/>..<paramref name="v1"/> so stacked parts share one label texture.
        /// Caps sample the texture at (<paramref name="capU"/>, v0/v1).
        /// </summary>
        private static Mesh RoundedPrism(string name, float w, float h, float d, float r, int cornerSegs, float v0 = 0f, float v1 = 1f, float capU = 0.125f)
        {
            r = Mathf.Min(r, Mathf.Min(w, d) * 0.5f - 0.0001f);
            var ring = new List<Vector3>();
            // corners in order: front-left, front-right, back-right, back-left (front is -Z, facing the viewer)
            var centres = new[]
            {
                new Vector2(-w * 0.5f + r, -d * 0.5f + r), new Vector2(w * 0.5f - r, -d * 0.5f + r),
                new Vector2(w * 0.5f - r, d * 0.5f - r), new Vector2(-w * 0.5f + r, d * 0.5f - r),
            };
            float[] startAngles = { 180f, 270f, 0f, 90f };
            for (int c = 0; c < 4; c++)
            {
                for (int k = 0; k <= cornerSegs; k++)
                {
                    float a = (startAngles[c] + 90f * k / cornerSegs) * Mathf.Deg2Rad;
                    ring.Add(new Vector3(centres[c].x + Mathf.Cos(a) * r, 0f, centres[c].y + Mathf.Sin(a) * r));
                }
            }

            // start the seam in the middle of the left side so u=0 sits at the front-left edge
            int n = ring.Count;
            var perim = new float[n + 1];
            for (int i = 1; i <= n; i++) perim[i] = perim[i - 1] + Vector3.Distance(ring[i - 1], ring[i % n]);
            float total = perim[n];
            // u origin at the start of the front face (first corner's end point)
            float uOrigin = perim[cornerSegs / 2];

            var verts = new List<Vector3>();
            var normals = new List<Vector3>();
            var uvs = new List<Vector2>();
            var tris = new List<int>();
            for (int i = 0; i <= n; i++)
            {
                Vector3 p = ring[i % n];
                Vector3 prev = ring[(i - 1 + n) % n], next = ring[(i + 1) % n];
                Vector3 tangent = (next - prev).normalized;
                Vector3 normal = Vector3.Cross(Vector3.up, tangent).normalized;
                float u = Mathf.Repeat((perim[i] - uOrigin) / total, 1f);
                if (i == n) u = Mathf.Repeat((total - uOrigin) / total, 1f);
                verts.Add(p + Vector3.down * h * 0.5f); normals.Add(normal); uvs.Add(new Vector2(u, v0));
                verts.Add(p + Vector3.up * h * 0.5f); normals.Add(normal); uvs.Add(new Vector2(u, v1));
            }

            // fix the wrap: duplicate column where u jumps from ~1 back to 0
            for (int i = 0; i < n; i++)
            {
                int a = i * 2, b = (i + 1) * 2;
                float ua = uvs[a].x, ub = uvs[b].x;
                if (ub < ua - 0.5f)
                {
                    int extra = verts.Count;
                    verts.Add(verts[b]); normals.Add(normals[b]); uvs.Add(new Vector2(1f, v0));
                    verts.Add(verts[b + 1]); normals.Add(normals[b + 1]); uvs.Add(new Vector2(1f, v1));
                    b = extra;
                }

                tris.AddRange(new[] { a, a + 1, b, b, a + 1, b + 1 });
            }

            foreach (float sign in new[] { 1f, -1f })
            {
                int centre = verts.Count;
                verts.Add(Vector3.up * sign * h * 0.5f); normals.Add(Vector3.up * sign); uvs.Add(new Vector2(capU, sign > 0 ? v1 - 0.002f : v0 + 0.002f));
                for (int i = 0; i < n; i++)
                {
                    verts.Add(ring[i] + Vector3.up * sign * h * 0.5f); normals.Add(Vector3.up * sign); uvs.Add(new Vector2(capU, sign > 0 ? v1 - 0.002f : v0 + 0.002f));
                }

                for (int i = 0; i < n; i++)
                {
                    int a = centre + 1 + i, b = centre + 1 + (i + 1) % n;
                    if (sign > 0) tris.AddRange(new[] { centre, b, a });
                    else tris.AddRange(new[] { centre, a, b });
                }
            }

            var mesh = new Mesh { name = name };
            mesh.SetVertices(verts);
            mesh.SetNormals(normals);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(tris, 0);
            // the ring winds clockwise seen from above; flip if the sides came out inside-out
            if (Vector3.Dot(mesh.normals[0], verts[0] - Vector3.down * h * 0.5f) < 0f) { }
            mesh.RecalculateBounds();
            mesh.RecalculateTangents();
            return KeepPocketMesh(mesh);
        }

        /// <summary>A surface of revolution around +Y from a (radius, height) profile, used for the flame and spark wheel.</summary>
        private static Mesh LatheMesh(string name, Vector2[] profile, int segs)
        {
            var verts = new List<Vector3>();
            var tris = new List<int>();
            for (int i = 0; i < profile.Length; i++)
            {
                for (int s = 0; s <= segs; s++)
                {
                    float a = 2f * Mathf.PI * s / segs;
                    verts.Add(new Vector3(Mathf.Cos(a) * profile[i].x, profile[i].y, Mathf.Sin(a) * profile[i].x));
                }
            }

            int row = segs + 1;
            for (int i = 0; i < profile.Length - 1; i++)
            {
                for (int s = 0; s < segs; s++)
                {
                    int a = i * row + s, b = a + 1, c = a + row, e = c + 1;
                    tris.AddRange(new[] { a, c, b, b, c, e });
                }
            }

            var mesh = new Mesh { name = name };
            mesh.SetVertices(verts);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return KeepPocketMesh(mesh);
        }

        // 5x7 bitmap glyphs for the made-up brand name on the pack
        private static readonly Dictionary<char, string[]> PackGlyphs = new Dictionary<char, string[]>
        {
            ['F'] = new[] { "11111", "10000", "10000", "11110", "10000", "10000", "10000" },
            ['J'] = new[] { "00111", "00010", "00010", "00010", "00010", "10010", "01100" },
            ['O'] = new[] { "01110", "10001", "10001", "10001", "10001", "10001", "01110" },
            ['R'] = new[] { "11110", "10001", "10001", "11110", "10100", "10010", "10001" },
            ['D'] = new[] { "11100", "10010", "10001", "10001", "10001", "10010", "11100" },
        };

        /// <summary>
        /// Paints the wrap-around label for the made-up "FJORD" brand: white card, red flip-top lid with a white
        /// chevron, a gold tear strip, the name on the front and a black health-warning panel on the back.
        /// Layout in u: front [0, fw), right side, back, left side — proportional to the pack's perimeter.
        /// </summary>
        private static Texture2D PaintCigPackLabel(float w, float d, float lidFrom)
        {
            const int W = 1024, H = 512;
            var px = new Color32[W * H];
            Color32 white = Color.Lerp(Palette.SeaFoam, Color.white, 0.6f);
            Color32 red = Palette.BoathouseRust * 1.05f;
            Color32 darkRed = Palette.BoathouseRust * 0.55f;
            Color32 gold = Color.Lerp(Palette.LanternAmber, Palette.SunbreakKey, 0.3f);
            Color32 black = Color.Lerp(Palette.DeepFjordSlate, Color.black, 0.7f);
            float perim = 2f * (w + d);
            int fx0 = 0, fx1 = Mathf.RoundToInt(W * w / perim);
            int sx1 = fx1 + Mathf.RoundToInt(W * d / perim);
            int bx1 = sx1 + Mathf.RoundToInt(W * w / perim);
            int lidY = Mathf.RoundToInt(H * lidFrom);

            void Rect(int x0, int y0, int x1, int y1, Color32 c)
            {
                for (int y = Mathf.Max(0, y0); y < Mathf.Min(H, y1); y++)
                    for (int x = Mathf.Max(0, x0); x < Mathf.Min(W, x1); x++)
                        px[y * W + x] = c;
            }

            Rect(0, 0, W, H, white);
            Rect(0, lidY, W, H, red);                         // flip-top lid all the way round
            Rect(0, lidY - 6, W, lidY - 1, gold);             // gold tear strip just under the lid line

            // white chevron down into the lid on front and back
            foreach (int x0 in new[] { fx0, sx1 })
            {
                int fw = fx1 - fx0;
                for (int y = lidY; y < H; y++)
                {
                    float t = (y - lidY) / (float)(H - lidY);
                    int half = Mathf.RoundToInt(fw * 0.5f * (1f - t * 1.6f));
                    if (half <= 0) continue;
                    int cx = x0 + fw / 2;
                    for (int x = cx - half; x < cx + half; x++)
                        if (Mathf.Abs(x - cx) > half - 14) px[y * W + x] = white;
                }
            }

            // brand name, pixel font, centred on the front below the lid
            const string brand = "FJORD";
            int scale = 9, gap = 2;
            int textW = brand.Length * (5 + gap) * scale - gap * scale;
            int tx = fx0 + (fx1 - fx0 - textW) / 2, ty = lidY - 40 - 7 * scale;
            for (int ci = 0; ci < brand.Length; ci++)
            {
                string[] g = PackGlyphs[brand[ci]];
                for (int gy = 0; gy < 7; gy++)
                    for (int gx = 0; gx < 5; gx++)
                        if (g[gy][gx] == '1')
                            Rect(tx + (ci * (5 + gap) + gx) * scale, ty + (6 - gy) * scale, tx + (ci * (5 + gap) + gx + 1) * scale, ty + (7 - gy) * scale, darkRed);
            }

            Rect(fx0 + 30, ty - 26, fx1 - 30, ty - 20, gold);   // thin gold rule under the name

            // back: black warning panel with white "text" lines
            int wy0 = 24, wy1 = Mathf.RoundToInt(lidY * 0.62f);
            Rect(sx1 + 14, wy0, bx1 - 14, wy1, black);
            for (int ly = wy1 - 34; ly > wy0 + 14; ly -= 26)
            {
                int len = (ly / 26) % 3 == 0 ? 120 : 30;
                Rect(sx1 + 34, ly - 10, bx1 - 34 - len, ly, white);
            }

            // right side: small barcode
            var rng = new System.Random(77);
            int bx = fx1 + 18;
            while (bx < sx1 - 18)
            {
                int bw = 1 + rng.Next(4);
                if (rng.Next(2) == 0) Rect(bx, 40, bx + bw, 120, black);
                bx += bw + 1 + rng.Next(2);
            }

            return SaveBeachTexture(CigPackLabelPath, px, W, H, srgb: true);
        }

        private static PickupItem BuildCigarettePack()
        {
            AssetDatabase.DeleteAsset(PocketMeshPath);
            const float w = 0.055f, h = 0.088f, d = 0.022f, r = 0.0025f;
            const float lidH = h * 0.30f, bodyH = h - lidH;
            float lidFrom = bodyH / h;
            Texture2D label = PaintCigPackLabel(w, d, lidFrom);
            Material card = PropMaterial("Mat_CigPack", Color.white, 0.42f, 0f, label);

            var root = new GameObject("CigarettePack");
            Mesh body = RoundedPrism("CigPack_Body", w, bodyH, d, r, 3, 0f, lidFrom - 0.004f, 0.125f);
            PocketPart("Body", root.transform, body, card, new Vector3(0f, -h * 0.5f + bodyH * 0.5f, 0f));

            // the flip-top lid hangs off a hinge on the back edge, so it can be opened later
            var hinge = new GameObject("LidHinge").transform;
            hinge.SetParent(root.transform, false);
            hinge.localPosition = new Vector3(0f, -h * 0.5f + bodyH, d * 0.5f);
            Mesh lid = RoundedPrism("CigPack_Lid", w * 1.012f, lidH, d * 1.03f, r, 3, lidFrom + 0.004f, 1f, 0.125f);
            PocketPart("Lid", hinge, lid, card, new Vector3(0f, lidH * 0.5f, -d * 0.5f));

            root.AddComponent<BoxCollider>().size = new Vector3(w, h, d);
            var item = root.AddComponent<PickupItem>();
            item.Configure("Cigarettes", 0.020f, h * 0.5f, true, "Use", false);
            item.SetHeldPose(new Vector3(0.016f, 0f, 0.002f), new Vector3(0f, 180f, 0f));

            Cigarette cigTemplate = BuildCigaretteTemplate(root.transform);
            root.AddComponent<CigarettePack>().Configure(hinge, cigTemplate);
            return item;
        }

        private static Cigarette BuildCigaretteTemplate(Transform parent)
        {
            const float radius = 0.0039f;
            const float filterLen = 0.022f;
            const float paperLen = 0.056f;
            const float tipLen = 0.004f;
            const float totalLen = filterLen + paperLen + tipLen;

            Material filterMat = PropMaterial("Mat_Cig_Filter", Color.Lerp(Palette.LanternAmber, Palette.DriftwoodTimber, 0.45f), 0.25f);
            Material paperMat = PropMaterial("Mat_Cig_Paper", Color.Lerp(Palette.UiTextParchment, Palette.SeaFoam, 0.25f), 0.18f);
            Material tipMat = PropMaterial("Mat_Cig_Tobacco", Palette.WetBasalt * 0.45f, 0.1f);
            Material emberMat = FlameMaterial("Mat_Cig_Ember", Color.Lerp(Palette.LanternAmber, Color.red, 0.35f), 1.4f);

            var cigRoot = new GameObject("CigaretteTemplate");
            cigRoot.transform.SetParent(parent, false);
            Quaternion toForward = Quaternion.Euler(90f, 0f, 0f);

            Mesh filterMesh = LatheMesh("Cig_Filter", new[]
            {
                new Vector2(0f, 0f), new Vector2(radius, 0f),
                new Vector2(radius, filterLen), new Vector2(0f, filterLen)
            }, 12);
            Mesh paperMesh = LatheMesh("Cig_Paper", new[]
            {
                new Vector2(0f, 0f), new Vector2(radius * 0.99f, 0f),
                new Vector2(radius * 0.99f, paperLen), new Vector2(0f, paperLen)
            }, 12);
            Mesh tipMesh = LatheMesh("Cig_Tip", new[]
            {
                new Vector2(0f, 0f), new Vector2(radius * 0.96f, 0f),
                new Vector2(radius * 0.92f, tipLen), new Vector2(0f, tipLen)
            }, 12);
            Mesh emberMesh = LatheMesh("Cig_Ember", new[]
            {
                new Vector2(0f, 0f), new Vector2(radius * 0.94f, 0f),
                new Vector2(radius * 0.85f, 0.003f), new Vector2(0f, 0.0045f)
            }, 12);

            PocketPart("Filter", cigRoot.transform, filterMesh, filterMat, Vector3.zero, null, toForward);
            PocketPart("Paper", cigRoot.transform, paperMesh, paperMat, new Vector3(0f, 0f, filterLen), null, toForward);
            PocketPart("Tobacco", cigRoot.transform, tipMesh, tipMat, new Vector3(0f, 0f, filterLen + paperLen), null, toForward);

            var emberGo = PocketPart("Ember", cigRoot.transform, emberMesh, emberMat, new Vector3(0f, 0f, totalLen - 0.001f), null, toForward);
            emberGo.GetComponent<MeshRenderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

            var tip = new GameObject("Tip").transform;
            tip.SetParent(cigRoot.transform, false);
            tip.localPosition = new Vector3(0f, 0f, totalLen);

            var glow = emberGo.AddComponent<Light>();
            glow.type = LightType.Point;
            glow.color = Color.Lerp(Palette.LanternAmber, Color.red, 0.35f);
            glow.range = 0.5f;
            glow.intensity = 0.12f;
            glow.shadows = LightShadows.None;

            var cig = cigRoot.AddComponent<Cigarette>();
            cig.Configure(emberGo, glow, tip);
            cigRoot.SetActive(false);
            return cig;
        }

        private static PickupItem BuildLighter()
        {
            const float w = 0.024f, d = 0.0125f, bodyH = 0.064f, hoodH = 0.013f;
            float h = bodyH + hoodH;
            Color plastic = Color.Lerp(Palette.DeepFjordSlate, new Color(0.1f, 0.35f, 0.75f), 0.55f);
            Material shell = PropMaterial("Mat_Lighter", plastic, 0.86f);
            Material steel = PropMaterial("Mat_Lighter_Metal", Palette.HarbourConcrete, 0.78f, 1f);
            Material darkSteel = PropMaterial("Mat_Lighter_Wheel", Palette.WetBasalt * 0.6f, 0.55f, 1f);
            Material button = PropMaterial("Mat_Lighter_Button", Palette.BoathouseRust, 0.5f);
            Material slot = PropMaterial("Mat_Lighter_Slot", Color.Lerp(Palette.DeepFjordSlate, Color.black, 0.8f), 0.2f);

            var root = new GameObject("Lighter");
            float baseY = -h * 0.5f;

            // plastic fuel body: soft oval sides like a disposable lighter
            PocketPart("Body", root.transform, RoundedPrism("Lighter_Body", w, bodyH, d, d * 0.48f, 6), shell, new Vector3(0f, baseY + bodyH * 0.5f, 0f));
            // a darker base band where the fuel tank is sealed
            PocketPart("Base", root.transform, RoundedPrism("Lighter_Base", w * 1.01f, 0.003f, d * 1.02f, d * 0.48f, 6), slot, new Vector3(0f, baseY + 0.0015f, 0f));

            // stamped steel hood with a dark wind slot at the front
            float hoodY = baseY + bodyH + hoodH * 0.5f;
            PocketPart("Hood", root.transform, RoundedPrism("Lighter_Hood", w * 0.9f, hoodH, d * 0.95f, d * 0.3f, 4), steel, new Vector3(-w * 0.04f, hoodY, 0f));
            PocketPart("HoodSlot", root.transform, RoundedPrism("Lighter_HoodSlot", w * 0.42f, hoodH * 0.55f, 0.0012f, 0.0004f, 2), slot, new Vector3(-w * 0.04f, hoodY + hoodH * 0.1f, -d * 0.48f));

            // spark wheel across the top at the back-right, and the red gas button beside it
            Mesh wheel = LatheMesh("Lighter_Wheel", new[]
            {
                new Vector2(0f, -0.004f), new Vector2(0.0042f, -0.004f), new Vector2(0.0046f, -0.002f),
                new Vector2(0.0046f, 0.002f), new Vector2(0.0042f, 0.004f), new Vector2(0f, 0.004f),
            }, 18);
            PocketPart("SparkWheel", root.transform, wheel, darkSteel, new Vector3(w * 0.28f, baseY + bodyH + 0.0045f, 0f), null, Quaternion.Euler(90f, 0f, 0f));
            PocketPart("Button", root.transform, RoundedPrism("Lighter_Button", w * 0.3f, 0.005f, d * 0.75f, 0.0015f, 3), button, new Vector3(w * 0.3f, baseY + bodyH + 0.001f, 0f), null, Quaternion.Euler(0f, 0f, 8f));

            // two-layer flame: a tall amber outer flame and a small blue core at the nozzle
            var flame = new GameObject("Flame");
            flame.transform.SetParent(root.transform, false);
            flame.transform.localPosition = new Vector3(-w * 0.04f, baseY + h + 0.001f, 0f);
            Mesh outer = LatheMesh("Lighter_FlameOuter", new[]
            {
                new Vector2(0.0010f, 0f), new Vector2(0.0040f, 0.004f), new Vector2(0.0050f, 0.010f),
                new Vector2(0.0042f, 0.018f), new Vector2(0.0024f, 0.026f), new Vector2(0.0006f, 0.032f), new Vector2(0f, 0.034f),
            }, 16);
            Mesh inner = LatheMesh("Lighter_FlameInner", new[]
            {
                new Vector2(0.0008f, 0f), new Vector2(0.0024f, 0.002f), new Vector2(0.0026f, 0.005f),
                new Vector2(0.0016f, 0.009f), new Vector2(0f, 0.012f),
            }, 12);
            var outerGo = PocketPart("FlameOuter", flame.transform, outer, FlameMaterial("Mat_LighterFlame", Color.Lerp(Palette.LanternAmber, Palette.SunbreakKey, 0.2f), 0.9f), Vector3.zero);
            var innerGo = PocketPart("FlameCore", flame.transform, inner, FlameMaterial("Mat_LighterFlameCore", Color.Lerp(Palette.SkyZenithMist, new Color(0.25f, 0.45f, 1f), 0.7f), 0.8f), Vector3.zero);
            outerGo.GetComponent<MeshRenderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            innerGo.GetComponent<MeshRenderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

            var glow = flame.AddComponent<Light>();
            glow.type = LightType.Point;
            glow.color = Palette.LanternAmber;
            glow.range = 2.5f;
            glow.intensity = 0.35f;
            glow.shadows = LightShadows.None;

            root.AddComponent<BoxCollider>().size = new Vector3(w, h, d);
            var item = root.AddComponent<PickupItem>();
            item.Configure("Lighter", 0.022f, h * 0.5f, true, "Light", false);
            item.SetHeldPose(new Vector3(0.014f, 0f, -0.004f), new Vector3(0f, 180f, 0f));
            root.AddComponent<Lighter>().Configure(flame, glow);
            return item;
        }

        /// <summary>Close-up of Andrias's pocket items (pack and a lit lighter) for checking their look.</summary>
        private static void RenderPocketItemsShot(Camera cam, string file)
        {
            CharacterPockets pockets = UnityEngine.Object.FindObjectsByType<CharacterPockets>(FindObjectsSortMode.None).FirstOrDefault();
            if (pockets == null || pockets.Items.Count == 0) return;

            var saved = new List<(PickupItem item, Transform parent, Vector3 pos, Quaternion rot, bool active)>();
            foreach (PickupItem it in pockets.Items)
                saved.Add((it, it.transform.parent, it.transform.localPosition, it.transform.localRotation, it.gameObject.activeSelf));

            Vector3 eye = cam.transform.position;
            Vector3 fwd = cam.transform.forward, right = cam.transform.right, up = cam.transform.up;
            Quaternion facing = Quaternion.LookRotation(Vector3.ProjectOnPlane(-fwd, Vector3.up).normalized, Vector3.up);
            var fill = new GameObject("PocketShotFill").AddComponent<Light>();
            fill.type = LightType.Point;
            fill.range = 2f;
            fill.intensity = 0.18f;
            fill.color = Palette.SunbreakKey;
            fill.transform.position = eye + up * 0.15f - right * 0.2f;
            float keepFov = cam.fieldOfView;
            try
            {
                for (int i = 0; i < saved.Count; i++)
                {
                    Transform t = saved[i].item.transform;
                    t.SetParent(null, true);
                    t.gameObject.SetActive(true);
                    t.position = eye + fwd * 0.32f + right * (i == 0 ? -0.04f : 0.035f);
                    // pack: front and a bit of side; lighter: three-quarter so the hood and wheel read
                    t.rotation = facing * Quaternion.Euler(0f, i == 0 ? 180f + 28f : 180f - 20f, 0f);
                    if (t.TryGetComponent(out Lighter lighter))
                    {
                        Transform flame = t.Find("Flame");
                        if (flame != null) flame.gameObject.SetActive(true);
                        Light l = flame != null ? flame.GetComponent<Light>() : null;
                        if (l != null) l.enabled = true;
                    }
                }

                cam.fieldOfView = 26f;
                Shot(cam, null);
                Shot(cam, file);
            }
            finally
            {
                cam.fieldOfView = keepFov;
                UnityEngine.Object.DestroyImmediate(fill.gameObject);
                foreach (var sv in saved)
                {
                    Transform t = sv.item.transform;
                    if (t.TryGetComponent(out Lighter lighter))
                    {
                        Transform flame = t.Find("Flame");
                        if (flame != null) flame.gameObject.SetActive(false);
                        Light l = flame != null ? flame.GetComponent<Light>() : null;
                        if (l != null) l.enabled = false;
                    }

                    t.SetParent(sv.parent, false);
                    t.localPosition = sv.pos;
                    t.localRotation = sv.rot;
                    t.gameObject.SetActive(sv.active);
                }
            }
        }

        private const string DriftwoodMatPath = VillageAssetDir + "/Mat_Driftwood.mat";

        /// <summary>
        /// Washed-up driftwood logs and old planks on the dry sand. Each is a <see cref="PickupItem"/> with a
        /// <see cref="Firewood"/> marker, so the players can gather them for a fire later.
        /// </summary>
        private static void BuildBeachWood(float sea, Vector3 avoid)
        {
            GameObject existing = GameObject.Find("BeachWood");
            if (existing != null) UnityEngine.Object.DestroyImmediate(existing);

            var rng = new System.Random(3301);
            float R() => (float)rng.NextDouble();
            Mesh cube = Resources.GetBuiltinResource<Mesh>("Cube.fbx");
            Mesh cylinder = Resources.GetBuiltinResource<Mesh>("Cylinder.fbx");
            Material log = RockMaterial(DriftwoodMatPath, "Mat_Driftwood", Palette.DriftwoodTimber * 0.85f, 0.1f);
            Material plank = RockMaterial(DriftwoodMatPath.Replace("Driftwood", "OldPlank"), "Mat_OldPlank", Palette.DriftwoodTimber * 0.65f, 0.08f);

            var root = new GameObject("BeachWood");
            int placed = 0;
            const int count = 10;
            for (int attempt = 0; attempt < 400 && placed < count; attempt++)
            {
                float z = Mathf.Lerp(316f, 392f, R());
                GetBeachEnvelope(z, out float xTop, out float xShore);
                float x = Mathf.Lerp(xTop + 2f, xShore - 3f, R());
                if (new Vector2(x - avoid.x, z - avoid.z).sqrMagnitude < 9f) continue;   // leave the sea mine clear
                if (!Physics.Raycast(new Vector3(x, 60f, z), Vector3.down, out RaycastHit hit, 200f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore)
                    || hit.collider.name != "VillageBeach" || hit.point.y < sea + 0.3f)
                {
                    continue;   // dry sand only, never on top of a stone or another piece
                }

                bool isPlank = placed % 2 == 1;
                float yaw = R() * 360f;
                GameObject go;
                float halfHeight;
                if (isPlank)
                {
                    go = new GameObject($"Plank_{placed / 2 + 1:00}");
                    var scale = new Vector3(Mathf.Lerp(0.14f, 0.2f, R()), 0.03f, Mathf.Lerp(0.8f, 1.3f, R()));
                    go.transform.localScale = scale;
                    go.transform.rotation = Quaternion.Euler(0f, yaw, (R() - 0.5f) * 6f);
                    go.AddComponent<MeshFilter>().sharedMesh = cube;
                    go.AddComponent<MeshRenderer>().sharedMaterial = plank;
                    go.AddComponent<BoxCollider>();
                    halfHeight = scale.y * 0.5f;
                }
                else
                {
                    go = new GameObject($"Driftwood_{placed / 2 + 1:00}");
                    float d = Mathf.Lerp(0.07f, 0.13f, R());
                    float len = Mathf.Lerp(0.6f, 1.1f, R());
                    go.transform.localScale = new Vector3(d, len * 0.5f, d);   // the primitive cylinder is 2 m tall
                    go.transform.rotation = Quaternion.Euler(90f, yaw, 0f);    // lying on its side
                    go.AddComponent<MeshFilter>().sharedMesh = cylinder;
                    go.AddComponent<MeshRenderer>().sharedMaterial = log;
                    go.AddComponent<CapsuleCollider>();
                    halfHeight = d * 0.5f;
                }

                go.transform.SetParent(root.transform, true);
                go.transform.position = hit.point + Vector3.up * halfHeight;
                var woodItem = go.AddComponent<PickupItem>();
                woodItem.Configure(isPlank ? "Plank" : "Driftwood", 0f, halfHeight, false, "Use", false);
                woodItem.SetTwoHanded(true);   // carried crosswise in both hands, not dangling from one fist
                go.AddComponent<Firewood>().Configure(isPlank ? 45f : 90f);
                placed++;
            }

            Debug.Log($"[VillageSceneSetup] Beach wood: {placed} pickable pieces.");
        }

        private sealed class CombineList
        {
            private readonly List<CombineInstance> items = new List<CombineInstance>();
            public int Count => items.Count;

            public void Add(Mesh mesh, Matrix4x4 m) => items.Add(new CombineInstance { mesh = mesh, transform = m });

            public Mesh Build()
            {
                var mesh = new Mesh { indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
                mesh.CombineMeshes(items.ToArray(), mergeSubMeshes: true, useMatrices: true);
                return mesh;
            }
        }

        private static Material RockMaterial(string path, string name, Color color, float smoothness)
        {
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                mat = new Material(Shader.Find("Universal Render Pipeline/Lit")) { name = name };
                AssetDatabase.CreateAsset(mat, path);
            }

            color.a = 1f;
            mat.SetColor("_BaseColor", color);
            mat.SetFloat("_Metallic", 0f);
            mat.SetFloat("_Smoothness", smoothness);
            var detail = AssetDatabase.LoadAssetAtPath<Texture2D>(TerrainDetailPath);
            if (detail != null)
            {
                mat.SetTexture("_DetailAlbedoMap", detail);
                mat.SetFloat("_DetailAlbedoMapScale", 0.8f);
                mat.EnableKeyword("_DETAIL_MULX2");
            }

            EditorUtility.SetDirty(mat);
            return mat;
        }

        /// <summary>A lumpy, faceted unit boulder: a sphere pushed around by noise and flattened underneath.</summary>
        private static Mesh MakeRockPrototype(System.Random rng)
        {
            const int rings = 4;
            const int segs = 6;
            float ox = (float)rng.NextDouble() * 100f;
            float oz = (float)rng.NextDouble() * 100f;
            var grid = new Vector3[(rings + 1) * segs];
            for (int r = 0; r <= rings; r++)
            {
                float phi = Mathf.PI * r / rings;
                for (int s = 0; s < segs; s++)
                {
                    float th = 2f * Mathf.PI * s / segs;
                    var dir = new Vector3(Mathf.Sin(phi) * Mathf.Cos(th), Mathf.Cos(phi), Mathf.Sin(phi) * Mathf.Sin(th));
                    float n = Mathf.PerlinNoise(dir.x * 1.6f + ox, dir.z * 1.6f + dir.y * 0.9f + oz);
                    Vector3 p = dir * (0.5f * (0.72f + 0.5f * n));
                    if (p.y < -0.12f)
                    {
                        p.y = -0.12f + (p.y + 0.12f) * 0.3f;
                    }

                    grid[r * segs + s] = p;
                }
            }

            // flat-shaded: each triangle gets its own vertices, which reads as chipped stone
            var verts = new List<Vector3>();
            var tris = new List<int>();
            void Tri(Vector3 a, Vector3 b, Vector3 c)
            {
                tris.Add(verts.Count); verts.Add(a);
                tris.Add(verts.Count); verts.Add(b);
                tris.Add(verts.Count); verts.Add(c);
            }

            for (int r = 0; r < rings; r++)
            {
                for (int s = 0; s < segs; s++)
                {
                    int s1 = (s + 1) % segs;
                    Vector3 a = grid[r * segs + s], b = grid[r * segs + s1];
                    Vector3 c = grid[(r + 1) * segs + s], d = grid[(r + 1) * segs + s1];
                    if (r > 0) Tri(a, b, c);
                    if (r < rings - 1) Tri(b, d, c);
                }
            }

            var mesh = new Mesh { name = "RockProto" };
            mesh.SetVertices(verts);
            mesh.SetTriangles(tris, 0);
            mesh.SetUVs(0, verts.Select(v => new Vector2(v.x + v.y, v.z + v.y)).ToList());
            mesh.RecalculateNormals();
            return mesh;
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

                Transform handBone = null;
                Transform lowerArmBone = null;
                Transform beerTf = null;
                Vector3 gripLocal = Vector3.zero;
                float gripBelow = 0.02f;

                if (charTransform.TryGetComponent(out BeerDrinker drinker))
                {
                    drinker.StartLowered = true;
                    drinker.Pose(0f, 0f);
                    handBone = drinker.Hand;
                    lowerArmBone = drinker.LowerArm;
                    beerTf = drinker.Beer;
                    gripLocal = drinker.GripLocal;
                    gripBelow = drinker.GripBelowCentre;
                    drinker.enabled = false;
                    EditorUtility.SetDirty(drinker);
                }

                if (handBone == null)
                {
                    handBone = charTransform.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == "RightHand");
                }

                if (lowerArmBone == null)
                {
                    lowerArmBone = charTransform.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == "RightLowerArm");
                }

                if (gripLocal == Vector3.zero && handBone != null)
                {
                    gripLocal = CharacterItemHolder.ComputeFistCentre(charTransform, handBone, lowerArmBone);
                }

                if (beerTf == null)
                {
                    beerTf = charTransform.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == "Beer");
                }

                PickupItem beerItem = null;
                if (beerTf != null)
                {
                    beerItem = GetOrAdd<PickupItem>(beerTf.gameObject);
                    beerItem.Configure("Beer", gripBelow, 0.075f, true, "Drink", true);
                    var bottle = GetOrAdd<BeerBottle>(beerTf.gameObject);
                    bottle.SetStartSipsLeft(2);   // 3 of 5 sips drunk in the car
                    EditorUtility.SetDirty(bottle);
                    EditorUtility.SetDirty(beerItem);
                }

                var itemHolder = GetOrAdd<CharacterItemHolder>(charTransform.gameObject);
                itemHolder.Configure(handBone, gripLocal, beerItem);
                itemHolder.PoseHeldItem();
                EditorUtility.SetDirty(itemHolder);
                BuildPocketItems(name, charTransform);

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

                var drunk = GetOrAdd<CharacterDrunk>(charTransform.gameObject);
                drunk.SetStartingBeers(0.6f);   // three sips taken during the drive

                EditorUtility.SetDirty(animator);
                EditorUtility.SetDirty(controller);
                EditorUtility.SetDirty(motor);
                EditorUtility.SetDirty(drunk);
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

        private static void SetupCameraPartyAndHud(Camera cam, CharacterMotor[] motors, GameObject partyGo, SeaMineBomb seaMine)
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

            if (seaMine != null)
            {
                var seaMineUi = GetOrAdd<SeaMineBombUi>(partyGo);
                seaMineUi.Configure(seaMine, partyController);
                EditorUtility.SetDirty(seaMineUi);
            }

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
                string heldName = motor.TryGetComponent(out CharacterItemHolder holder) && holder.HeldItem != null
                    ? holder.HeldItem.DisplayName
                    : "None";
                log += $"\n  Start {motor.DisplayName}: {p} inside={inside} groundY={groundY:F2} delta={groundDelta:F3} areaBound={motor.Area == area} held={heldName}";
                if (!inside || !hasGround || groundDelta > 0.05f || motor.Area != area || heldName != "Beer")
                {
                    Debug.LogError($"[VillageSceneSetup] Player {motor.DisplayName} invalid start state at {p} (inside={inside}, hasGround={hasGround}, groundDelta={groundDelta:F3}, areaBound={motor.Area == area}, held={heldName})!");
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

        private static void RenderPreviewFrames(Camera cam, CharacterMotor[] motors)
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

                LookShot("village_shore_rocks.png",
                    new Vector3(426f, -85.5f, 400f),
                    new Vector3(442f, -89f, 440f), 50f);

                LookShot("village_overview.png",
                    new Vector3(355f, -55f, 255f),
                    new Vector3(412f, -84f, 345f), 42f);

                if (cam.TryGetComponent(out ThirdPersonCameraRig cameraRig))
                {
                    cameraRig.SetFirstPerson(true, snap: true);
                    Shot(cam, Path.Combine(previewDir, "village_firstperson.png"));
                    cameraRig.SetFirstPerson(false, snap: true);
                }

                if (motors != null && motors.Length > 0 && motors[0] != null)
                {
                    CharacterMotor player = motors[0];
                    LookShot("village_beer_carry.png",
                        player.transform.TransformPoint(new Vector3(1.15f, 1.35f, 1.10f)),
                        player.transform.TransformPoint(new Vector3(0.05f, 0.95f, 0.10f)), 40f);

                    var cc = player.GetComponent<CharacterController>();
                    Vector3 keepCharPos = player.transform.position;
                    Quaternion keepCharRot = player.transform.rotation;

                    Vector3 beachStart = new Vector3(434f, SurfaceY(new Vector3(434f, 0f, 338f), false) + 0.15f, 338f);
                    if (cc != null) cc.enabled = false;
                    player.transform.SetPositionAndRotation(beachStart, Quaternion.Euler(0f, 8f, 0f));
                    if (cc != null) cc.enabled = true;
                    Physics.SyncTransforms();

                    if (cc != null)
                    {
                        for (int step = 0; step < 20; step++)
                        {
                            cc.Move(new Vector3(0.02f, -0.08f, 0.20f));
                            Physics.SyncTransforms();
                        }
                    }

                    Vector3 walkedPos = player.transform.position;
                    float beachY = SurfaceY(walkedPos, false);
                    bool grounded = cc != null && cc.isGrounded;
                    Debug.Log($"[VillageSceneSetup] Beach walk test: pos={walkedPos:F2}, surfaceY={beachY:F2}, grounded={grounded}");

                    LookShot("village_beach_player.png",
                        walkedPos + new Vector3(-1.4f, 2.1f, -4.2f),
                        walkedPos + new Vector3(0.8f, 0.8f, 6.0f), 46f);

                    if (cc != null) cc.enabled = false;
                    player.transform.SetPositionAndRotation(keepCharPos, keepCharRot);
                    if (cc != null) cc.enabled = true;
                    Physics.SyncTransforms();
                }

                cam.transform.SetPositionAndRotation(keepPos, keepRot);
                cam.fieldOfView = keepFov;
                RenderPocketItemsShot(cam, Path.Combine(previewDir, "village_pocket_items.png"));
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

        private static void VerifyDrunkProgression(CharacterMotor[] motors, Camera cam)
        {
            AssertDrunkCondition(motors != null && motors.Length > 0 && motors[0] != null, "Missing player CharacterMotor.");
            CharacterMotor player = motors[0];
            CharacterDrunk drunk = player.GetComponent<CharacterDrunk>();
            CharacterItemHolder itemHolder = player.GetComponent<CharacterItemHolder>();
            AssertDrunkCondition(drunk != null, "Player missing CharacterDrunk component.");
            AssertDrunkCondition(cam != null, "Missing Camera for drunk verification renders.");
            drunk.SetBeersDrunk(0f);   // players start at 0.6 beers; test the progression from sober

            AssertDrunkCondition(
                drunk.Stage == CharacterDrunk.DrunkStage.Sober && Mathf.Approximately(drunk.BeersDrunk, 0f),
                $"Expected sober start, got stage={drunk.Stage}, beers={drunk.BeersDrunk:F2}.");

            for (int i = 0; i < 5; i++)
            {
                drunk.RecordSip(0.2f);
            }
            AssertDrunkCondition(
                drunk.Stage == CharacterDrunk.DrunkStage.SlightlyBuzzed,
                $"Expected SlightlyBuzzed after 5 sips, got {drunk.Stage}.");
            drunk.Tick(0.3f);
            Vector3 slightlyBuzzedVel = drunk.ModifyDesiredVelocity(new Vector3(0f, 0f, 1.75f), false, 0.05f);
            AssertDrunkCondition(
                Mathf.Abs(slightlyBuzzedVel.x) > 0.001f,
                $"Expected lateral walking drift at 1 beer, got x={slightlyBuzzedVel.x:F4}.");

            for (int i = 0; i < 5; i++)
            {
                drunk.RecordSip(0.2f);
            }
            AssertDrunkCondition(
                drunk.Stage == CharacterDrunk.DrunkStage.Buzzed,
                $"Expected Buzzed after 10 sips, got {drunk.Stage}.");
            AssertDrunkCondition(
                drunk.GetTurnSpeedMultiplier() < 0.9f,
                $"Expected reduced turn speed at 2 beers, got {drunk.GetTurnSpeedMultiplier():F3}.");

            for (int i = 0; i < 5; i++)
            {
                drunk.RecordSip(0.2f);
            }
            AssertDrunkCondition(
                drunk.Stage == CharacterDrunk.DrunkStage.HardToWalk,
                $"Expected HardToWalk after 15 sips, got {drunk.Stage}.");
            AssertDrunkCondition(
                drunk.GetTurnSpeedMultiplier() < 0.6f,
                $"Expected heavily impaired turn speed at 3 beers, got {drunk.GetTurnSpeedMultiplier():F3}.");
            Vector3 hardRunVel = drunk.ModifyDesiredVelocity(new Vector3(0f, 0f, player.RunSpeed), true, 0.2f);
            AssertDrunkCondition(
                hardRunVel.magnitude < player.RunSpeed * 0.8f,
                $"Expected impaired run speed at 3 beers, got {hardRunVel.magnitude:F2} vs RunSpeed {player.RunSpeed:F2}.");

            for (int i = 0; i < 5; i++)
            {
                drunk.RecordSip(0.2f);
            }
            AssertDrunkCondition(
                drunk.Stage == CharacterDrunk.DrunkStage.PukingAndFalling,
                $"Expected PukingAndFalling after 20 sips, got {drunk.Stage}.");

            drunk.Tick(0.1f);
            AssertDrunkCondition(
                drunk.IsIncapacitated && drunk.IsPuking,
                $"Expected incapacitated and puking at 0.1s, got incapacitated={drunk.IsIncapacitated}, puking={drunk.IsPuking}.");
            AssertDrunkCondition(
                drunk.ModifyDesiredVelocity(new Vector3(0f, 0f, 2f), false, 0.1f) == Vector3.zero,
                "Expected zero desired velocity while puking.");

            Vector3 savedCamPos = cam.transform.position;
            Quaternion savedCamRot = cam.transform.rotation;
            float savedCamFov = cam.fieldOfView;
            try
            {
                string previewDir = Path.GetFullPath(Path.Combine(Application.dataPath, "../Docs/preview"));
                Directory.CreateDirectory(previewDir);

                player.gameObject.SetActive(false);
                try
                {
                    Shot(cam, null);
                }
                finally
                {
                    player.gameObject.SetActive(true);
                }

                for (int i = 0; i < 19; i++)
                {
                    drunk.Tick(0.05f);
                    drunk.ApplyBonePose();
                    if (itemHolder != null)
                    {
                        itemHolder.PoseHeldItem();
                    }
                }
                AssertDrunkCondition(
                    GameObject.Find("VomitPuddle") != null,
                    "Expected VomitPuddle GameObject to exist after puking heave at 1.05s.");

                cam.transform.position = player.transform.position
                    + player.transform.forward * 2.25f
                    + player.transform.right * 1.55f
                    + Vector3.up * 1.15f;
                cam.transform.LookAt(player.transform.position + player.transform.forward * 0.35f + Vector3.up * 0.72f);
                cam.fieldOfView = 46f;
                RenderPosedPlayerShot(cam, player, Path.Combine(previewDir, "village_drunk_puking.png"));

                for (int i = 0; i < 12; i++)
                {
                    drunk.Tick(0.2f);
                }
                drunk.ApplyBonePose();
                if (itemHolder != null)
                {
                    itemHolder.PoseHeldItem();
                }

                Transform hips = FindChildByName(player.transform, "Hips");
                AssertDrunkCondition(hips != null, "Could not locate Hips transform on player character.");
                float hipHeight = hips.position.y - player.transform.position.y;
                AssertDrunkCondition(
                    drunk.IsFallen && hipHeight < 0.35f,
                    $"Expected fallen pose at 3.45s, got IsFallen={drunk.IsFallen}, hipHeight={hipHeight:F3}m.");

                cam.transform.position = player.transform.position
                    + player.transform.forward * 2.25f
                    + player.transform.right * 1.65f
                    + Vector3.up * 1.25f;
                cam.transform.LookAt(player.transform.position + player.transform.forward * 0.35f + Vector3.up * 0.25f);
                cam.fieldOfView = 45f;
                RenderPosedPlayerShot(cam, player, Path.Combine(previewDir, "village_drunk_fallen.png"));

                for (int i = 0; i < 18; i++)
                {
                    drunk.Tick(0.25f);
                }
                drunk.ApplyBonePose();
                if (itemHolder != null)
                {
                    itemHolder.PoseHeldItem();
                }
                AssertDrunkCondition(
                    !drunk.IsIncapacitated,
                    $"Expected character to recover by 7.95s, still incapacitated={drunk.IsIncapacitated}.");
            }
            finally
            {
                player.gameObject.SetActive(true);
                cam.transform.position = savedCamPos;
                cam.transform.rotation = savedCamRot;
                cam.fieldOfView = savedCamFov;
                drunk.ClearSpawnedVomit();
                drunk.SetBeersDrunk(0f);
                drunk.ApplyBonePose();
                if (itemHolder != null)
                {
                    itemHolder.PoseHeldItem();
                }
            }

            Debug.Log("[VillageSceneSetup] Drunk progression verification: OK (0->1->2->3->4 beers, puke, fall, recover)");
        }

        private static void VerifyFishingRod(GameObject carGo, CharacterMotor[] motors, Camera cam)
        {
            AssertCondition("Fishing rod", carGo != null, "Missing VWGolf_WithPlayers.");
            AssertCondition("Fishing rod", motors != null && motors.Length > 0 && motors[0] != null, "Missing player CharacterMotor.");
            AssertCondition("Fishing rod", cam != null, "Missing Camera for fishing rod renders.");

            Transform golf = carGo.transform.Find("VWGolf");
            Transform tailgate = golf != null ? golf.Find("Tailgate") : null;
            CarTrunk trunk = tailgate != null ? tailgate.GetComponent<CarTrunk>() : null;
            Transform rodTf = golf != null ? golf.Find("FishingRod") : null;
            FishingRod rod = rodTf != null ? rodTf.GetComponent<FishingRod>() : null;
            PickupItem rodItem = rodTf != null ? rodTf.GetComponent<PickupItem>() : null;

            CharacterMotor player = motors[0];
            CharacterItemHolder itemHolder = player.GetComponent<CharacterItemHolder>();
            CharacterController cc = player.GetComponent<CharacterController>();

            AssertCondition("Fishing rod", trunk != null && rod != null && rodItem != null && itemHolder != null,
                "Missing CarTrunk, FishingRod, PickupItem, or CharacterItemHolder.");

            trunk.Toggle();
            trunk.Toggle();
            AssertCondition("Fishing rod", !trunk.IsOpen && !rodItem.enabled,
                $"Expected rod pickup disabled while trunk closed (IsOpen={trunk.IsOpen}, enabled={rodItem.enabled}).");

            trunk.Toggle();
            AssertCondition("Fishing rod", trunk.IsOpen && rodItem.enabled,
                $"Expected rod pickup enabled when trunk opened (IsOpen={trunk.IsOpen}, enabled={rodItem.enabled}).");

            Quaternion savedTailgateRot = trunk.transform.localRotation;
            Vector3 savedCamPos = cam.transform.position;
            Quaternion savedCamRot = cam.transform.rotation;
            float savedCamFov = cam.fieldOfView;
            Vector3 savedPlayerPos = player.transform.position;
            Quaternion savedPlayerRot = player.transform.rotation;
            PickupItem previousHeld = itemHolder.HeldItem;

            try
            {
                string previewDir = Path.GetFullPath(Path.Combine(Application.dataPath, "../Docs/preview"));
                Directory.CreateDirectory(previewDir);

                Vector3 hingeAxis = Vector3.right;
                MeshRenderer tailMesh = trunk.GetComponentInChildren<MeshRenderer>();
                if (tailMesh != null)
                {
                    Vector3 lever = tailMesh.bounds.center - trunk.transform.position;
                    Vector3 axisWorld = trunk.transform.TransformDirection(hingeAxis);
                    if ((Quaternion.AngleAxis(30f, axisWorld) * lever).y < lever.y)
                    {
                        hingeAxis = -hingeAxis;
                    }
                }

                trunk.transform.localRotation = savedTailgateRot * Quaternion.AngleAxis(78f, hingeAxis);
                Physics.SyncTransforms();

                cam.transform.position = trunk.transform.position + carGo.transform.forward * -2.1f + Vector3.up * 0.75f;
                cam.transform.LookAt(rod.transform.position - carGo.transform.right * 0.25f + Vector3.up * 0.05f);
                cam.fieldOfView = 46f;

                player.gameObject.SetActive(false);
                try
                {
                    Shot(cam, Path.Combine(previewDir, "village_trunk_fishing_rod.png"));
                }
                finally
                {
                    player.gameObject.SetActive(true);
                }

                trunk.transform.localRotation = savedTailgateRot;

                AssertCondition("Fishing rod", itemHolder.TryPickup(rodItem) && itemHolder.HeldItem == rodItem,
                    "Player failed to pick up FishingRod from open trunk.");
                rod.ApplyPose();
                AssertCondition("Fishing rod", rod.State == FishingRod.RodState.Idle,
                    $"Expected Idle state on pickup, got {rod.State}.");

                Vector3 beachPos = new Vector3(441.2f, SurfaceY(new Vector3(441.2f, 0f, 342f), false), 342f);
                if (cc != null) cc.enabled = false;
                player.transform.SetPositionAndRotation(beachPos, Quaternion.Euler(0f, 90f, 0f));
                if (cc != null) cc.enabled = true;
                Physics.SyncTransforms();
                itemHolder.PoseHeldItem();
                rod.ApplyPose();

                AssertCondition("Fishing rod", itemHolder.TryUseHeldItem(),
                    "Failed to use FishingRod to cast from beach shoreline.");

                for (int i = 0; i < 7; i++)
                {
                    rod.Tick(0.1f);
                    itemHolder.PoseHeldItem();
                    rod.ApplyPose();
                }

                AssertCondition("Fishing rod", rod.State == FishingRod.RodState.Fishing && rod.IsLineOut,
                    $"Expected Fishing state at 0.7s on beach shoreline, got {rod.State} (IsLineOut={rod.IsLineOut}).");

                cam.transform.position = player.transform.position
                    - player.transform.forward * 2.2f
                    - player.transform.right * 2.4f
                    + Vector3.up * 1.65f;
                cam.transform.LookAt(player.transform.position + player.transform.forward * 2.8f + Vector3.up * 0.55f);
                cam.fieldOfView = 50f;
                RenderPosedPlayerShot(cam, player, Path.Combine(previewDir, "village_fishing_cast.png"));

                for (int i = 0; i < 10; i++)
                {
                    rod.Tick(0.25f);
                    itemHolder.PoseHeldItem();
                    rod.ApplyPose();
                }

                AssertCondition("Fishing rod", rod.State == FishingRod.RodState.Bite,
                    $"Expected Bite state after 2.5s waiting, got {rod.State}.");

                AssertCondition("Fishing rod", itemHolder.TryUseHeldItem() && rod.State == FishingRod.RodState.Reeling,
                    $"Expected Reeling state when using rod during Bite, got {rod.State}.");

                for (int i = 0; i < 9; i++)
                {
                    rod.Tick(0.1f);
                    itemHolder.PoseHeldItem();
                    rod.ApplyPose();
                }

                AssertCondition("Fishing rod", rod.State == FishingRod.RodState.Idle && rod.FishCaughtCount == 1,
                    $"Expected Idle state with FishCaughtCount == 1, got state={rod.State}, caught={rod.FishCaughtCount}.");
            }
            finally
            {
                player.gameObject.SetActive(true);
                if (cc != null) cc.enabled = false;
                player.transform.SetPositionAndRotation(savedPlayerPos, savedPlayerRot);
                if (cc != null) cc.enabled = true;
                Physics.SyncTransforms();

                if (itemHolder != null && itemHolder.HeldItem == rodItem)
                {
                    itemHolder.DropHeldItem();
                }

                if (rodItem != null && golf != null)
                {
                    rodItem.transform.SetParent(golf, false);
                    rodItem.transform.localPosition = new Vector3(0.32f, 0.585f, -1.66f);
                    rodItem.transform.localRotation = Quaternion.identity;
                }

                if (rod != null)
                {
                    rod.ResetToStowed();
                }

                if (previousHeld != null && itemHolder != null && itemHolder.HeldItem != previousHeld)
                {
                    itemHolder.TryPickup(previousHeld);
                    itemHolder.PoseHeldItem();
                }

                if (trunk != null)
                {
                    trunk.transform.localRotation = savedTailgateRot;
                    if (trunk.IsOpen)
                    {
                        trunk.Toggle();
                    }
                }

                cam.transform.SetPositionAndRotation(savedCamPos, savedCamRot);
                cam.fieldOfView = savedCamFov;
            }

            Debug.Log("[VillageSceneSetup] Fishing rod verification: OK (trunk gate, pickup, beach cast, bite, reel 1 fish)");
        }

        private static SeaMineBomb BuildSeaMineBomb(float seaLevel)
        {
            GameObject existing = GameObject.Find("AtomBombSeaMine");
            if (existing != null)
            {
                UnityEngine.Object.DestroyImmediate(existing);
            }

            string matDir = Path.Combine(Application.dataPath, "Props/SeaMine");
            Directory.CreateDirectory(matDir);

            Material ironMat = SeaMineMaterial("Mat_SeaMine_Iron", Color.Lerp(Palette.DeepFjordSlate, Palette.WetBasalt, 0.52f), 0.42f, 0.62f);
            Material rustMat = SeaMineMaterial("Mat_SeaMine_Rust", Palette.BoathouseRust, 0.28f, 0.25f);
            Material brassMat = SeaMineMaterial("Mat_SeaMine_Brass", Color.Lerp(Palette.LanternAmber, Palette.SunbreakKey, 0.35f), 0.58f, 0.78f);
            Material atomicMat = SeaMineMaterial("Mat_SeaMine_AtomicAmber", Palette.UiAccentAmber, 0.38f, 0.12f);
            Material panelMat = SeaMineMaterial("Mat_SeaMine_Panel", Palette.UiPanelSlate, 0.30f, 0.48f);
            Material steelMat = SeaMineMaterial("Mat_SeaMine_Steel", Palette.HarbourConcrete, 0.50f, 0.72f);
            Material kelpMat = SeaMineMaterial("Mat_SeaMine_Kelp", Color.Lerp(Palette.TurfMoss, Palette.DeepFjordSlate, 0.35f), 0.32f, 0.0f);
            Material ledRedMat = SeaMineMaterial("Mat_SeaMine_LedRed", Color.Lerp(Palette.BoathouseRust, new Color(0.95f, 0.16f, 0.14f, 1f), 0.7f), 0.78f, 0.1f, new Color(1.4f, 0.16f, 0.12f, 1f));
            Material ledGreenMat = SeaMineMaterial("Mat_SeaMine_LedGreen", Palette.UiConfirmGreen, 0.78f, 0.1f, Palette.UiConfirmGreen * 1.35f);
            Material ledAmberMat = SeaMineMaterial("Mat_SeaMine_LedAmber", Palette.UiAccentAmber, 0.78f, 0.1f, Palette.UiAccentAmber * 1.25f);
            Material beachSandMat = AssetDatabase.LoadAssetAtPath<Material>("Assets/Environment/Village/Mat_Village_BeachSand.mat")
                ?? SeaMineMaterial("Mat_SeaMine_WetSand", Color.Lerp(Palette.HarbourConcrete, Palette.DriftwoodTimber, 0.35f), 0.25f, 0.0f);

            Vector3 minePos = new Vector3(438.2f, 0f, 334.0f);
            minePos.y = TrySurfaceY(minePos, includeFootpaths: false, out float beachY) ? beachY : seaLevel + 0.55f;

            var mineGo = new GameObject("AtomBombSeaMine");
            mineGo.transform.position = minePos;
            mineGo.transform.rotation = Quaternion.Euler(0f, -22f, 0f);

            // Trigger sphere handles click-to-inspect raycasts while Layer-2 MineObstacle stops characters walking through the hull.
            var clickTrigger = mineGo.AddComponent<SphereCollider>();
            clickTrigger.center = new Vector3(0f, 0.44f, 0f);
            clickTrigger.radius = 0.92f;
            clickTrigger.isTrigger = true;

            var obstacleGo = new GameObject("MineObstacle");
            obstacleGo.layer = 2;
            obstacleGo.transform.SetParent(mineGo.transform, false);
            var solidCol = obstacleGo.AddComponent<SphereCollider>();
            solidCol.center = new Vector3(0f, 0.44f, 0f);
            solidCol.radius = 0.58f;

            var hullGo = new GameObject("MineHull");
            hullGo.transform.SetParent(mineGo.transform, false);
            hullGo.transform.localPosition = new Vector3(0f, 0.44f, 0f);
            hullGo.transform.localRotation = Quaternion.Euler(5f, 0f, -8f);

            BoxPart(PrimitiveType.Sphere, "MainSphere", hullGo.transform, Vector3.zero, new Vector3(1.12f, 1.12f, 1.12f), ironMat);
            BoxPart(PrimitiveType.Cylinder, "EquatorialBelt", hullGo.transform, Vector3.zero, new Vector3(1.18f, 0.042f, 1.18f), rustMat);

            for (int r = 0; r < 12; r++)
            {
                float angle = r * (360f / 12f) * Mathf.Deg2Rad;
                Vector3 rivetPos = new Vector3(Mathf.Cos(angle) * 0.60f, 0.0f, Mathf.Sin(angle) * 0.60f);
                BoxPart(PrimitiveType.Sphere, $"BeltRivet_{r:D2}", hullGo.transform, rivetPos, new Vector3(0.036f, 0.036f, 0.036f), brassMat);
            }

            // Hazard atomic collar ring around the upper crown (radius comfortably outside the sphere skin to avoid z-fighting).
            BoxPart(PrimitiveType.Cylinder, "AtomicBand", hullGo.transform, new Vector3(0f, 0.34f, 0f), new Vector3(0.95f, 0.035f, 0.95f), atomicMat);

            // Top armed warning beacon visible from afar on the beach.
            BoxPart(PrimitiveType.Cylinder, "BeaconCollar", hullGo.transform, new Vector3(0f, 0.56f, 0f), new Vector3(0.16f, 0.045f, 0.16f), steelMat);
            var beaconGo = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            beaconGo.name = "ArmedBeacon";
            UnityEngine.Object.DestroyImmediate(beaconGo.GetComponent<Collider>());
            beaconGo.transform.SetParent(hullGo.transform, false);
            beaconGo.transform.localPosition = new Vector3(0f, 0.62f, 0f);
            beaconGo.transform.localScale = new Vector3(0.12f, 0.11f, 0.12f);
            var beaconRenderer = beaconGo.GetComponent<MeshRenderer>();
            beaconRenderer.sharedMaterial = ledRedMat;

            // Naval Hertz contact horns crowning sides, back, and dome (leaving the -X control hatch unobstructed).
            Vector3[] hornDirs =
            {
                new Vector3(0.24f, 0.88f, 0.40f).normalized,
                new Vector3(0.24f, 0.88f, -0.40f).normalized,
                new Vector3(-0.35f, 0.86f, 0.36f).normalized,
                new Vector3(-0.35f, 0.86f, -0.36f).normalized,
                new Vector3(0.62f, 0.66f, 0.42f).normalized,
                new Vector3(0.62f, 0.66f, -0.42f).normalized,
                new Vector3(0.05f, 0.42f, 0.90f).normalized,
                new Vector3(0.05f, 0.42f, -0.90f).normalized,
                new Vector3(-0.52f, 0.32f, 0.79f).normalized,
                new Vector3(-0.52f, 0.32f, -0.79f).normalized,
            };

            for (int i = 0; i < hornDirs.Length; i++)
            {
                Vector3 dir = hornDirs[i];
                var hornPivot = new GameObject($"ContactHorn_{i:D2}");
                hornPivot.transform.SetParent(hullGo.transform, false);
                hornPivot.transform.localPosition = dir * 0.54f;
                hornPivot.transform.localRotation = Quaternion.FromToRotation(Vector3.up, dir);

                BoxPart(PrimitiveType.Cylinder, "Collar", hornPivot.transform, new Vector3(0f, 0.02f, 0f), new Vector3(0.105f, 0.025f, 0.105f), rustMat);
                BoxPart(PrimitiveType.Cylinder, "Stem", hornPivot.transform, new Vector3(0f, 0.085f, 0f), new Vector3(0.055f, 0.075f, 0.055f), steelMat);
                BoxPart(PrimitiveType.Sphere, "LeadTip", hornPivot.transform, new Vector3(0f, 0.165f, 0f), new Vector3(0.068f, 0.058f, 0.068f), brassMat);
            }

            // Atomic trefoil placard mounted proudly above the decipher control bay.
            var emblemGo = new GameObject("AtomicTrefoilEmblem");
            emblemGo.transform.SetParent(mineGo.transform, false);
            emblemGo.transform.localPosition = new Vector3(-0.44f, 0.87f, 0f);
            emblemGo.transform.localRotation = Quaternion.LookRotation(new Vector3(-0.74f, 0.67f, 0f).normalized, Vector3.up);
            BoxPart(PrimitiveType.Cylinder, "BadgeDisk", emblemGo.transform, Vector3.zero, new Vector3(0.22f, 0.018f, 0.22f), panelMat);
            FindChildByName(emblemGo.transform, "BadgeDisk").localRotation = Quaternion.Euler(90f, 0f, 0f);
            BoxPart(PrimitiveType.Cylinder, "CoreDot", emblemGo.transform, new Vector3(0f, 0f, 0.020f), new Vector3(0.048f, 0.008f, 0.048f), atomicMat);
            FindChildByName(emblemGo.transform, "CoreDot").localRotation = Quaternion.Euler(90f, 0f, 0f);
            for (int b = 0; b < 3; b++)
            {
                float rad = (b * 120f + 90f) * Mathf.Deg2Rad;
                var bladeGo = GameObject.CreatePrimitive(PrimitiveType.Cube);
                bladeGo.name = $"TrefoilBlade_{b}";
                UnityEngine.Object.DestroyImmediate(bladeGo.GetComponent<Collider>());
                bladeGo.transform.SetParent(emblemGo.transform, false);
                bladeGo.transform.localPosition = new Vector3(Mathf.Cos(rad) * 0.055f, Mathf.Sin(rad) * 0.055f, 0.020f);
                bladeGo.transform.localRotation = Quaternion.Euler(0f, 0f, b * 120f);
                bladeGo.transform.localScale = new Vector3(0.064f, 0.048f, 0.012f);
                bladeGo.GetComponent<MeshRenderer>().sharedMaterial = atomicMat;
            }

            // Washed-up sand ridge, mooring chain links, and dried seaweed around base.
            BoxPart(PrimitiveType.Cylinder, "SandBedMound", mineGo.transform, new Vector3(0.06f, 0.01f, 0f), new Vector3(1.36f, 0.08f, 1.28f), beachSandMat);
            for (int c = 0; c < 6; c++)
            {
                Vector3 linkPos = new Vector3(0.42f + c * 0.14f, 0.045f + (c == 0 ? 0.06f : 0f), Mathf.Sin(c * 0.7f) * 0.05f);
                var linkGo = GameObject.CreatePrimitive(PrimitiveType.Cube);
                linkGo.name = $"MooringChain_{c:D2}";
                UnityEngine.Object.DestroyImmediate(linkGo.GetComponent<Collider>());
                linkGo.transform.SetParent(mineGo.transform, false);
                linkGo.transform.localPosition = linkPos;
                linkGo.transform.localRotation = Quaternion.Euler(c % 2 == 0 ? 18f : 72f, 15f + c * 6f, 0f);
                linkGo.transform.localScale = new Vector3(0.14f, 0.045f, 0.07f);
                linkGo.GetComponent<MeshRenderer>().sharedMaterial = rustMat;
            }

            Vector3[] kelpOffsets =
            {
                new Vector3(-0.34f, 0.045f, 0.44f),
                new Vector3(0.38f, 0.045f, -0.42f),
                new Vector3(0.54f, 0.045f, 0.24f),
            };
            for (int k = 0; k < kelpOffsets.Length; k++)
            {
                var kelp = GameObject.CreatePrimitive(PrimitiveType.Cube);
                kelp.name = $"WashedSeaweed_{k}";
                UnityEngine.Object.DestroyImmediate(kelp.GetComponent<Collider>());
                kelp.transform.SetParent(mineGo.transform, false);
                kelp.transform.localPosition = kelpOffsets[k];
                kelp.transform.localRotation = Quaternion.Euler(4f, k * 42f + 15f, -3f);
                kelp.transform.localScale = new Vector3(0.32f, 0.032f, 0.14f);
                kelp.GetComponent<MeshRenderer>().sharedMaterial = kelpMat;
            }

            // Inland-facing (+Z outward toward crouch position) angled decipher control hatch positioned clear of the sphere belt.
            var panelRoot = new GameObject("ControlPanel");
            panelRoot.transform.SetParent(mineGo.transform, false);
            panelRoot.transform.localPosition = new Vector3(-0.62f, 0.49f, 0f);
            panelRoot.transform.localRotation = Quaternion.LookRotation(new Vector3(-0.90f, 0.44f, 0f).normalized, Vector3.up);

            BoxPart(PrimitiveType.Cube, "HatchCollar", panelRoot.transform, new Vector3(0f, 0f, -0.09f), new Vector3(0.54f, 0.40f, 0.16f), ironMat);
            BoxPart(PrimitiveType.Cube, "HatchBacking", panelRoot.transform, Vector3.zero, new Vector3(0.58f, 0.44f, 0.055f), panelMat);
            BoxPart(PrimitiveType.Cube, "HazardFrameTop", panelRoot.transform, new Vector3(0f, 0.21f, 0.025f), new Vector3(0.58f, 0.026f, 0.020f), atomicMat);
            BoxPart(PrimitiveType.Cube, "HazardFrameBottom", panelRoot.transform, new Vector3(0f, -0.21f, 0.025f), new Vector3(0.58f, 0.026f, 0.020f), atomicMat);
            BoxPart(PrimitiveType.Cube, "HazardFrameLeft", panelRoot.transform, new Vector3(-0.28f, 0f, 0.025f), new Vector3(0.026f, 0.42f, 0.020f), atomicMat);
            BoxPart(PrimitiveType.Cube, "HazardFrameRight", panelRoot.transform, new Vector3(0.28f, 0f, 0.025f), new Vector3(0.026f, 0.42f, 0.020f), atomicMat);

            // 6 module indicator lights along the top bar.
            var moduleLights = new Renderer[6];
            for (int m = 0; m < 6; m++)
            {
                float lx = -0.19f + m * 0.076f;
                BoxPart(PrimitiveType.Cylinder, $"ModuleSocket_{m}", panelRoot.transform, new Vector3(lx, 0.155f, 0.028f), new Vector3(0.044f, 0.012f, 0.044f), steelMat);
                FindChildByName(panelRoot.transform, $"ModuleSocket_{m}").localRotation = Quaternion.Euler(90f, 0f, 0f);

                var bulb = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                bulb.name = $"ModuleLight_{m}";
                UnityEngine.Object.DestroyImmediate(bulb.GetComponent<Collider>());
                bulb.transform.SetParent(panelRoot.transform, false);
                bulb.transform.localPosition = new Vector3(lx, 0.155f, 0.039f);
                bulb.transform.localScale = new Vector3(0.032f, 0.032f, 0.020f);
                Renderer r = bulb.GetComponent<MeshRenderer>();
                r.sharedMaterial = m < 2 ? ledRedMat : ledAmberMat;
                moduleLights[m] = r;
            }

            // Module 1: 4 physical tumbler drums.
            BoxPart(PrimitiveType.Cube, "WordBayRecess", panelRoot.transform, new Vector3(-0.11f, 0.065f, 0.026f), new Vector3(0.24f, 0.085f, 0.018f), ironMat);
            for (int t = 0; t < 4; t++)
            {
                float tx = -0.195f + t * 0.056f;
                BoxPart(PrimitiveType.Cylinder, $"WordTumbler_{t}", panelRoot.transform, new Vector3(tx, 0.065f, 0.039f), new Vector3(0.040f, 0.022f, 0.040f), steelMat);
                FindChildByName(panelRoot.transform, $"WordTumbler_{t}").localRotation = Quaternion.Euler(0f, 0f, 90f);
            }

            // Arm/Deactivate guard button beside Word Bay.
            BoxPart(PrimitiveType.Cylinder, "TimerPlate", panelRoot.transform, new Vector3(0.145f, 0.065f, 0.029f), new Vector3(0.15f, 0.012f, 0.082f), rustMat);
            FindChildByName(panelRoot.transform, "TimerPlate").localRotation = Quaternion.Euler(90f, 0f, 0f);

            // Module 2: 10 contact toggle switches.
            BoxPart(PrimitiveType.Cube, "SwitchBusBar", panelRoot.transform, new Vector3(0f, -0.028f, 0.027f), new Vector3(0.49f, 0.082f, 0.016f), ironMat);
            var switchLevers = new Transform[SeaMineBomb.ContactCount];
            for (int s = 0; s < SeaMineBomb.ContactCount; s++)
            {
                float sx = -0.207f + s * 0.046f;
                BoxPart(PrimitiveType.Cylinder, $"SwitchBase_{s}", panelRoot.transform, new Vector3(sx, -0.028f, 0.036f), new Vector3(0.032f, 0.010f, 0.032f), brassMat);
                FindChildByName(panelRoot.transform, $"SwitchBase_{s}").localRotation = Quaternion.Euler(90f, 0f, 0f);

                var pivot = new GameObject($"ContactSwitchLever_{s}");
                pivot.transform.SetParent(panelRoot.transform, false);
                pivot.transform.localPosition = new Vector3(sx, -0.028f, 0.040f);
                BoxPart(PrimitiveType.Cube, "Shaft", pivot.transform, new Vector3(0f, 0f, 0.022f), new Vector3(0.014f, 0.014f, 0.044f), steelMat);
                BoxPart(PrimitiveType.Sphere, "Cap", pivot.transform, new Vector3(0f, 0f, 0.046f), new Vector3(0.024f, 0.024f, 0.024f), atomicMat);
                switchLevers[s] = pivot.transform;
            }

            // Modules 3..6: 4 sealed placeholder bays along the bottom row.
            for (int p = 0; p < 4; p++)
            {
                float px = -0.18f + p * 0.12f;
                BoxPart(PrimitiveType.Cube, $"LockedBay_{p + 3}", panelRoot.transform, new Vector3(px, -0.132f, 0.029f), new Vector3(0.10f, 0.072f, 0.016f), ironMat);
                BoxPart(PrimitiveType.Cube, $"LockedSeal_{p + 3}", panelRoot.transform, new Vector3(px, -0.132f, 0.038f), new Vector3(0.05f, 0.018f, 0.008f), atomicMat);
            }

            var seaMine = mineGo.AddComponent<SeaMineBomb>();
            seaMine.ConfigureVisuals(
                panelRoot.transform,
                switchLevers,
                moduleLights,
                beaconRenderer,
                ledRedMat,
                ledGreenMat,
                ledAmberMat,
                new Vector3(-1.38f, 0f, 0f));
            seaMine.ResetBomb();

            EditorUtility.SetDirty(mineGo);
            EditorUtility.SetDirty(seaMine);
            return seaMine;
        }

        private static Material SeaMineMaterial(string assetName, Color baseColor, float smoothness, float metallic, Color? emission = null)
        {
            string path = $"Assets/Props/SeaMine/{assetName}.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
                AssetDatabase.CreateAsset(mat, path);
            }

            mat.SetColor("_BaseColor", baseColor);
            mat.SetFloat("_Smoothness", smoothness);
            mat.SetFloat("_Metallic", metallic);

            if (emission.HasValue)
            {
                mat.EnableKeyword("_EMISSION");
                mat.SetColor("_EmissionColor", emission.Value);
            }
            else
            {
                mat.DisableKeyword("_EMISSION");
                mat.SetColor("_EmissionColor", Color.black);
            }

            EditorUtility.SetDirty(mat);
            return mat;
        }

        private static void VerifySeaMineBomb(SeaMineBomb bomb, CharacterMotor[] motors, Camera cam)
        {
            AssertCondition("Sea mine bomb", bomb != null, "Missing AtomBombSeaMine / SeaMineBomb.");
            AssertCondition("Sea mine bomb", motors != null && motors.Length >= 2 && motors[0] != null && motors[1] != null,
                "Missing playable CharacterMotors for Olavur and Leivur.");
            AssertCondition("Sea mine bomb", cam != null, "Missing Camera for sea mine renders.");

            var walkable = UnityEngine.Object.FindAnyObjectByType<WalkableArea>();
            var party = UnityEngine.Object.FindAnyObjectByType<PartyController>();
            var cameraRig = cam.GetComponent<ThirdPersonCameraRig>();
            var mineUi = UnityEngine.Object.FindAnyObjectByType<SeaMineBombUi>();

            AssertCondition("Sea mine bomb", walkable != null && party != null && cameraRig != null && mineUi != null,
                "Missing WalkableArea, PartyController, ThirdPersonCameraRig, or SeaMineBombUi.");

            AssertCondition("Sea mine bomb", walkable.Contains(bomb.transform.position),
                $"Expected sea mine {bomb.transform.position} to sit inside WalkableArea Beach zone.");
            AssertCondition("Sea mine bomb", TrySurfaceY(bomb.transform.position, includeFootpaths: false, out float groundY)
                && Mathf.Abs(bomb.transform.position.y - groundY) <= 0.05f,
                $"Expected sea mine Y={bomb.transform.position.y:F2} to rest cleanly on beach sand Y={groundY:F2}.");

            bomb.ResetBomb();
            AssertCondition("Sea mine bomb", Mathf.Approximately(bomb.RemainingSeconds, SeaMineBomb.DefaultDurationSeconds),
                $"Expected 1-hour timer (3600s) initially, got {bomb.RemainingSeconds}.");
            AssertCondition("Sea mine bomb", !bomb.IsDisarmed && !bomb.IsExploded && bomb.TotalModules == 6,
                "Expected active 6-module bomb before solving.");

            CharacterMotor olavur = motors[0];
            CharacterMotor leivur = motors[1];
            CharacterController olavurCc = olavur.GetComponent<CharacterController>();
            CharacterController leivurCc = leivur.GetComponent<CharacterController>();
            CharacterItemHolder olavurHolder = olavur.GetComponent<CharacterItemHolder>();

            Vector3 savedOlavurPos = olavur.transform.position;
            Quaternion savedOlavurRot = olavur.transform.rotation;
            Vector3 savedLeivurPos = leivur.transform.position;
            Quaternion savedLeivurRot = leivur.transform.rotation;
            Vector3 savedCamPos = cam.transform.position;
            Quaternion savedCamRot = cam.transform.rotation;
            float savedCamFov = cam.fieldOfView;
            int savedActiveIdx = party.ActiveIndex;

            try
            {
                string previewDir = Path.GetFullPath(Path.Combine(Application.dataPath, "../Docs/preview"));
                Directory.CreateDirectory(previewDir);

                party.SelectCharacter(0);

                Vector3 standPoint = bomb.GetInspectStandPoint();
                standPoint.y = SurfaceY(standPoint, includeFootpaths: true);
                AssertCondition("Sea mine bomb", walkable.Contains(standPoint),
                    $"Inspect stand point {standPoint} must lie inside WalkableArea.");

                if (olavurCc != null) olavurCc.enabled = false;
                olavur.transform.position = standPoint;
                Vector3 toMine = bomb.transform.position - standPoint;
                toMine.y = 0f;
                if (toMine.sqrMagnitude > 1e-4f)
                {
                    olavur.transform.rotation = Quaternion.LookRotation(toMine.normalized, Vector3.up);
                }

                if (olavurCc != null) olavurCc.enabled = true;
                Physics.SyncTransforms();

                AssertCondition("Sea mine bomb", bomb.IsCharacterInReach(olavur),
                    "Olavur at inspect stand point should be within interaction reach.");

                Transform hips = FindChildByName(olavur.transform, "Hips");
                AssertCondition("Sea mine bomb", hips != null, "Missing Hips bone on Olavur.");
                float standingHipsRelY = hips.position.y - olavur.transform.position.y;

                bomb.StartInspection(olavur);
                olavurHolder?.PoseHeldItem();
                bomb.ApplyInspectionPose();
                bomb.SyncInspectionCamera(snapCamera: true);
                cam.fieldOfView = 56f;

                float bendingHipsRelY = hips.position.y - olavur.transform.position.y;
                AssertCondition("Sea mine bomb", bomb.IsInspecting(olavur) && cameraRig.IsInspectingOverride,
                    "Expected Olavur inspecting and camera rig in first-person inspection override.");
                AssertCondition("Sea mine bomb", bendingHipsRelY < standingHipsRelY - 0.15f,
                    $"Expected Olavur to bend down while inspecting (standing hips relY={standingHipsRelY:F3}, bending={bendingHipsRelY:F3}).");

                Shot(cam, Path.Combine(previewDir, "village_bomb_firstperson.png"));

                // Switch active player to Leivur so other players observe Olavur bending down over the mine.
                Vector3 observerPos = walkable.Clamp(standPoint + new Vector3(-2.15f, 0f, 1.55f));
                observerPos.y = SurfaceY(observerPos, includeFootpaths: true);
                if (leivurCc != null) leivurCc.enabled = false;
                leivur.transform.position = observerPos;
                Vector3 lookAtMine = bomb.transform.position - observerPos;
                lookAtMine.y = 0f;
                if (lookAtMine.sqrMagnitude > 1e-4f)
                {
                    leivur.transform.rotation = Quaternion.LookRotation(lookAtMine.normalized, Vector3.up);
                }

                if (leivurCc != null) leivurCc.enabled = true;
                Physics.SyncTransforms();

                party.SelectCharacter(1);
                olavurHolder?.PoseHeldItem();
                bomb.ApplyInspectionPose();

                AssertCondition("Sea mine bomb", bomb.IsInspecting(olavur) && !bomb.IsActiveCharacterInspecting && !cameraRig.IsInspectingOverride,
                    "Switching to Leivur should exit first-person override while Olavur keeps bending over the mine.");

                foreach (Renderer r in olavur.GetComponentsInChildren<Renderer>(true))
                {
                    AssertCondition("Sea mine bomb", r.shadowCastingMode == UnityEngine.Rendering.ShadowCastingMode.On,
                        $"Expected renderer {r.name} on inspecting companion Olavur to remain visible to other players.");
                }

                cam.transform.position = standPoint + new Vector3(-0.35f, 1.42f, -2.95f);
                cam.transform.LookAt(Vector3.Lerp(olavur.transform.position, bomb.transform.position, 0.50f) + Vector3.up * 0.50f);
                cam.fieldOfView = 50f;
                Shot(cam, Path.Combine(previewDir, "village_bomb_bending.png"));

                // Verify Module 1 (TØNT), word penalty flash, Module 2 (10 contacts = 946), -60s wrong attempt penalty, and deactivation.
                AssertCondition("Sea mine bomb", !bomb.TrySubmitWord("TONT")
                    && Mathf.Approximately(bomb.RemainingSeconds, 3540f)
                    && bomb.WordPenaltyFlashTimer > 0f,
                    $"Expected wrong word TONT to deduct 60s and flash red (RemainingSeconds={bomb.RemainingSeconds}, WordFlash={bomb.WordPenaltyFlashTimer}).");
                AssertCondition("Sea mine bomb", bomb.TrySubmitWord("TØNT")
                    && bomb.IsWordSolved
                    && Mathf.Approximately(bomb.WordPenaltyFlashTimer, 0f)
                    && Mathf.Approximately(bomb.RemainingSeconds, 3540f),
                    "Expected exact word TØNT to solve Module 1, clear red flash, and incur no penalty.");

                AssertCondition("Sea mine bomb", !bomb.TrySubmitContacts() && Mathf.Approximately(bomb.RemainingSeconds, 3480f),
                    $"Expected wrong contacts to deduct 60s (RemainingSeconds={bomb.RemainingSeconds}).");

                for (int i = 0; i < SeaMineBomb.DefaultTargetContacts.Length; i++)
                {
                    bomb.SetContact(i, SeaMineBomb.DefaultTargetContacts[i]);
                }

                AssertCondition("Sea mine bomb", bomb.TrySubmitContacts() && bomb.IsContactsSolved && !bomb.CanDeactivate,
                    "Expected target contacts to solve Module 2 while Module 3 (wires) still blocks deactivation.");

                // Module 3: no cutters = no cut; with cutters the right wire solves it, a wrong one detonates.
                PickupItem cutterStandIn = olavurHolder != null ? olavurHolder.HeldItem : null;
                AssertCondition("Sea mine bomb", cutterStandIn != null, "Expected the inspecting character to hold an item to stand in for wire cutters.");
                int wrongWire = (bomb.CorrectWireIndex + 1) % SeaMineBomb.WireCount;
                AssertCondition("Sea mine bomb", !bomb.TryCutWire(bomb.CorrectWireIndex) && !bomb.IsWireCut(bomb.CorrectWireIndex),
                    "Expected cutting a wire without wire cutters to do nothing.");
                var tempCutters = cutterStandIn.gameObject.AddComponent<WireCutters>();
                try
                {
                    AssertCondition("Sea mine bomb", bomb.TryCutWire(bomb.CorrectWireIndex) && bomb.IsWiresSolved && bomb.CanDeactivate && !bomb.IsExploded,
                        "Expected the correct wire to solve Module 3 and unlock deactivation.");
                    AssertCondition("Sea mine bomb", bomb.TryDeactivate() && bomb.IsDisarmed,
                        "Expected TryDeactivate() to disarm the bomb once active modules are solved.");

                    bomb.ResetBomb();
                    AssertCondition("Sea mine bomb", bomb.TryCutWire(wrongWire) && bomb.IsExploded && !bomb.IsWiresSolved,
                        "Expected a wrong wire to detonate the mine (game lost).");
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(tempCutters);
                }
            }
            finally
            {
                bomb.StopInspection();
                bomb.ResetBomb();
                cameraRig.ClearInspectionOverride(snap: false);

                if (olavurCc != null) olavurCc.enabled = false;
                olavur.transform.SetPositionAndRotation(savedOlavurPos, savedOlavurRot);
                if (olavurCc != null) olavurCc.enabled = true;

                if (leivurCc != null) leivurCc.enabled = false;
                leivur.transform.SetPositionAndRotation(savedLeivurPos, savedLeivurRot);
                if (leivurCc != null) leivurCc.enabled = true;

                Physics.SyncTransforms();

                party.SelectCharacter(savedActiveIdx >= 0 ? savedActiveIdx : 0);
                olavurHolder?.PoseHeldItem();

                cam.transform.SetPositionAndRotation(savedCamPos, savedCamRot);
                cam.fieldOfView = savedCamFov;
                cameraRig.SetOrbit(16f, 18f, 5.6f, snap: true);
            }

            Debug.Log("[VillageSceneSetup] Sea mine bomb verification: OK (3600s timer, -60s penalty, TØNT + 10 contacts (946) + wires (right cut / wrong detonates), FP & third-person bending pose)");
        }

        private static void AssertCondition(string area, bool condition, string message)
        {
            if (condition)
            {
                return;
            }

            string full = $"[VillageSceneSetup] {area} verification FAILED: {message}";
            Debug.LogError(full);
            throw new System.InvalidOperationException(full);
        }

        private static void RenderPosedPlayerShot(Camera cam, CharacterMotor player, string filePath)
        {
            GameObject clone = UnityEngine.Object.Instantiate(player.gameObject, player.transform.parent);
            clone.name = player.name + "_PoseShot";
            player.gameObject.SetActive(false);
            try
            {
                Shot(cam, filePath);
            }
            finally
            {
                player.gameObject.SetActive(true);
                UnityEngine.Object.DestroyImmediate(clone);
            }
        }

        private static Transform FindChildByName(Transform root, string targetName)
        {
            foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
            {
                if (t.name == targetName)
                {
                    return t;
                }
            }
            return null;
        }

        private static void AssertDrunkCondition(bool condition, string message)
        {
            if (condition)
            {
                return;
            }

            string full = $"[VillageSceneSetup] Drunk verification FAILED: {message}";
            Debug.LogError(full);
            throw new System.InvalidOperationException(full);
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
                try
                {
                    if (File.Exists(file)) File.Delete(file);
                    File.WriteAllBytes(file, tex.EncodeToPNG());
                }
                catch (IOException ex)
                {
                    Debug.LogWarning($"[VillageSceneSetup] Could not overwrite preview {file}: {ex.Message}");
                }
                RenderTexture.active = null;
                UnityEngine.Object.DestroyImmediate(tex);
            }

            cam.targetTexture = null;
            UnityEngine.Object.DestroyImmediate(rt);
        }
    }
}
