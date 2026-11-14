using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Arnafjordur.Editor
{
    /// <summary>
    /// Builds URP materials for the VW Golf model (exported from Vehicles/Blender/build_golf.py),
    /// remaps them onto the FBX and saves a ready-to-use prefab.
    /// </summary>
    public static class GolfCarSetup
    {
        private const string Dir = "Assets/Vehicles/Golf";
        private const string FbxPath = Dir + "/VWGolf.fbx";
        private const string MatDir = Dir + "/Materials";
        private const string PrefabPath = Dir + "/VWGolf.prefab";

        private struct Def
        {
            public string Name;
            public Color Color;
            public float Metallic, Smoothness;
            public Color Emission;
            public bool Transparent;

            public Def(string name, string hex, float metallic, float smoothness, string emissionHex = null, float alpha = 1f)
            {
                Name = name;
                ColorUtility.TryParseHtmlString(hex, out Color);
                Color.a = alpha;
                Metallic = metallic;
                Smoothness = smoothness;
                Emission = Color.black;
                if (emissionHex != null) ColorUtility.TryParseHtmlString(emissionHex, out Emission);
                Transparent = alpha < 1f;
            }
        }

        private static readonly Def[] Defs =
        {
            new Def("CarPaint", "#1C4D94", 0.65f, 0.86f),
            new Def("Glass", "#20282C", 0f, 0.96f, null, 0.42f),
            new Def("TrimBlack", "#111113", 0f, 0.75f),
            new Def("Underbody", "#1A1A1C", 0f, 0.15f),
            new Def("Interior", "#232427", 0f, 0.2f),
            new Def("Seat", "#34353A", 0f, 0.1f),
            new Def("HeadlightLens", "#9AA3AD", 1f, 0.9f),
            new Def("TaillightLens", "#B00D0D", 0f, 0.88f, "#400000"),
            new Def("Chrome", "#E6E8EA", 1f, 0.92f),
            new Def("Tyre", "#141415", 0f, 0.1f),
            new Def("Alloy", "#CFD1D4", 1f, 0.72f),
            new Def("BrakeDisc", "#8E8E90", 1f, 0.55f),
            new Def("PlateWhite", "#EFEFEA", 0f, 0.45f),
            new Def("PlateBlue", "#123C9C", 0f, 0.45f),
        };

        [MenuItem("Tools/Vehicles/Rebuild VW Golf Prefab")]
        public static void Build()
        {
            var importer = AssetImporter.GetAtPath(FbxPath) as ModelImporter;
            if (importer == null)
            {
                Debug.LogError($"[GolfCarSetup] Model not found at {FbxPath}");
                return;
            }

            var shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null)
            {
                Debug.LogError("[GolfCarSetup] URP Lit shader not found");
                return;
            }

            Directory.CreateDirectory(MatDir);
            AssetDatabase.Refresh();

            foreach (var def in Defs)
            {
                string path = $"{MatDir}/Mat_Golf_{def.Name}.mat";
                var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (mat == null)
                {
                    mat = new Material(shader);
                    AssetDatabase.CreateAsset(mat, path);
                }
                Apply(mat, def);
                EditorUtility.SetDirty(mat);
                importer.AddRemap(new AssetImporter.SourceAssetIdentifier(typeof(Material), def.Name), mat);
            }

            importer.importAnimation = false;
            importer.animationType = ModelImporterAnimationType.None;
            importer.importCameras = false;
            importer.importLights = false;
            importer.importBlendShapes = false;
            importer.isReadable = false;
            importer.SaveAndReimport();
            AssetDatabase.SaveAssets();

            var model = AssetDatabase.LoadAssetAtPath<GameObject>(FbxPath);
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(model);
            instance.name = "VWGolf";
            PrefabUtility.SaveAsPrefabAsset(instance, PrefabPath);

            // Sanity report so a batch-mode run can be verified from the log.
            var bounds = new Bounds(Vector3.zero, Vector3.zero);
            bool first = true;
            int tris = 0, missing = 0;
            foreach (var r in instance.GetComponentsInChildren<MeshRenderer>())
            {
                if (first) { bounds = r.bounds; first = false; } else bounds.Encapsulate(r.bounds);
                tris += r.GetComponent<MeshFilter>().sharedMesh.triangles.Length / 3;
                foreach (var m in r.sharedMaterials)
                    if (m == null || !AssetDatabase.GetAssetPath(m).StartsWith(MatDir)) missing++;
            }
            var report = $"[GolfCarSetup] OK size={bounds.size} centre={bounds.center} tris={tris} unmappedMaterials={missing}";
            foreach (Transform t in instance.transform)
                report += $"\n  {t.name} pos={t.localPosition} rot={t.localEulerAngles} scale={t.localScale}";
            Debug.Log(report);
            Object.DestroyImmediate(instance);
        }

        private static void Apply(Material mat, Def def)
        {
            mat.SetColor("_BaseColor", def.Color);
            mat.SetColor("_Color", def.Color);
            mat.SetFloat("_Metallic", def.Metallic);
            mat.SetFloat("_Smoothness", def.Smoothness);

            if (def.Emission != Color.black)
            {
                mat.EnableKeyword("_EMISSION");
                mat.SetColor("_EmissionColor", def.Emission);
                mat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.BakedEmissive;
            }

            if (def.Transparent)
            {
                mat.SetFloat("_Surface", 1f);
                mat.SetFloat("_Blend", 0f);
                mat.SetFloat("_BlendModePreserveSpecular", 0f);
                mat.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
                mat.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
                mat.SetFloat("_SrcBlendAlpha", (float)BlendMode.One);
                mat.SetFloat("_DstBlendAlpha", (float)BlendMode.OneMinusSrcAlpha);
                mat.SetFloat("_ZWrite", 0f);
                mat.SetFloat("_Cull", (float)CullMode.Off);
                mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                mat.SetOverrideTag("RenderType", "Transparent");
                mat.renderQueue = (int)RenderQueue.Transparent;
                mat.SetShaderPassEnabled("ShadowCaster", false);
                mat.SetShaderPassEnabled("DepthOnly", false);
            }
        }
    }
}
