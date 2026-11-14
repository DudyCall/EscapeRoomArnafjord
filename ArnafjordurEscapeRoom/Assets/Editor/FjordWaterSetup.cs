using UnityEditor;
using UnityEngine;

namespace Arnafjordur.Editor
{
    /// <summary>
    /// Puts the fjord water shader (Arnafjordur/FjordWater) on Mat_Exit_FjordWater, the one material
    /// every outdoor scene uses for the sea. Called by the scene builders so a rebuild keeps the look.
    /// </summary>
    public static class FjordWaterSetup
    {
        public const string MaterialPath = "Assets/Environment/TunnelExit/Materials/Mat_Exit_FjordWater.mat";
        private const string ShaderPath = "Assets/Environment/TunnelExit/Shaders/FjordWater.shader";

        [MenuItem("Tools/Village/Apply Fjord Water Look")]
        public static void ApplyToExisting()
        {
            var mat = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
            if (mat == null)
            {
                Debug.LogWarning($"[FjordWaterSetup] Missing {MaterialPath} - run Tools/Intro/Rebuild Tunnel Exit Scene first.");
                return;
            }

            Apply(mat);
            AssetDatabase.SaveAssets();
        }

        public static Material Apply(Material mat)
        {
            var shader = AssetDatabase.LoadAssetAtPath<Shader>(ShaderPath);
            if (shader == null)
            {
                AssetDatabase.ImportAsset(ShaderPath);
                shader = AssetDatabase.LoadAssetAtPath<Shader>(ShaderPath);
            }

            if (shader == null)
            {
                Debug.LogError($"[FjordWaterSetup] Missing {ShaderPath}.");
                return mat;
            }

            mat.shader = shader;
            mat.renderQueue = -1;
            mat.SetColor("_DeepColor", new Color(0.035f, 0.10f, 0.14f));
            mat.SetColor("_ShallowColor", new Color(0.20f, 0.43f, 0.42f));
            mat.SetColor("_FoamColor", new Color(0.92f, 0.95f, 0.96f));
            mat.SetFloat("_DepthFade", 2.2f);
            mat.SetFloat("_ShallowClarity", 0.8f);
            mat.SetFloat("_FoamWidth", 0.6f);
            mat.SetFloat("_WaveStrength", 0.85f);
            mat.SetFloat("_WaveSpeed", 1f);
            mat.SetFloat("_Reflection", 1f);
            mat.SetFloat("_SunGlint", 1.4f);
            EditorUtility.SetDirty(mat);
            return mat;
        }
    }
}
