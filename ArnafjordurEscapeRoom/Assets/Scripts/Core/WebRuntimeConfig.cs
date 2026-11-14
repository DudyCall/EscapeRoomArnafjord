using UnityEngine;

namespace Arnafjordur.Core
{
    /// <summary>
    /// Aligns runtime frame pacing with the browser's requestAnimationFrame loop in WebGL builds.
    /// </summary>
    public static class WebRuntimeConfig
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void ApplyBrowserFramePacing()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            Application.targetFrameRate = -1;
            QualitySettings.vSyncCount = 0;
#endif
        }
    }
}
