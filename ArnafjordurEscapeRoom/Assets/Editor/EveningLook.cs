using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Arnafjordur.Editor
{
    /// <summary>
    /// The outdoor light for every scene outside the tunnel: evening, with a low warm sun, a dimmer sky
    /// and dusky fog - clearly darker than day, but nowhere near the dark of the tunnel.
    /// One place, so the tunnel exit, church arrival, car exit and village all match.
    /// </summary>
    public static class EveningLook
    {
        private const string SkyPath = "Assets/Environment/TunnelExit/Materials/Mat_Exit_Sky.mat";

        public static void Apply(Light sun)
        {
            sun.color = Hex("#FFD0A8");
            sun.intensity = 0.95f;
            sun.transform.rotation = Quaternion.Euler(26f, 140f, 0f);
            EditorUtility.SetDirty(sun);

            var sky = AssetDatabase.LoadAssetAtPath<Material>(SkyPath);
            if (sky != null)
            {
                sky.SetColor("_SkyTint", Hex("#70839A"));
                sky.SetColor("_GroundColor", Hex("#808D98"));
                sky.SetFloat("_AtmosphereThickness", 1.35f);
                sky.SetFloat("_Exposure", 0.8f);
                sky.SetFloat("_SunSize", 0.03f);
                EditorUtility.SetDirty(sky);
                RenderSettings.skybox = sky;
            }

            RenderSettings.sun = sun;
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = Hex("#687A90");
            RenderSettings.ambientEquatorColor = Hex("#6E7A86");
            RenderSettings.ambientGroundColor = Hex("#323830");
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.Exponential;
            RenderSettings.fogColor = Hex("#808D98");
            RenderSettings.fogDensity = 0.00062f;
        }

        private static Color Hex(string hex)
        {
            ColorUtility.TryParseHtmlString(hex, out var c);
            return c;
        }
    }
}
