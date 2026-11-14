using UnityEditor;
using UnityEditor.Build;
using UnityEngine;

namespace Arnafjordur.Editor
{
    /// <summary>
    /// Applies WebGL size, compression, memory, and frame-pacing settings compatible with PC, iOS, and Android browsers.
    /// </summary>
    public static class WebOptimizer
    {
        [MenuItem("Tools/Apply Web Release Settings")]
        public static void Optimize()
        {
            var target = NamedBuildTarget.WebGL;
            PlayerSettings.SetIl2CppCodeGeneration(target, Il2CppCodeGeneration.OptimizeSize);
            PlayerSettings.SetManagedStrippingLevel(target, ManagedStrippingLevel.High);
            PlayerSettings.stripUnusedMeshComponents = true;

            PlayerSettings.WebGL.dataCaching = true;
            PlayerSettings.WebGL.compressionFormat = WebGLCompressionFormat.Brotli;
            // Fallback lets static hosts serve compressed bundles even without Content-Encoding: br headers.
            PlayerSettings.WebGL.decompressionFallback = true;
            // Keep explicit C# exceptions so UGS Auth, Multiplayer, and Netcode try/catch blocks handle errors cleanly.
            PlayerSettings.WebGL.exceptionSupport = WebGLExceptionSupport.ExplicitlyThrownExceptionsOnly;
            PlayerSettings.WebGL.debugSymbolMode = WebGLDebugSymbolMode.Off;
            PlayerSettings.WebGL.wasm2023 = true;
            PlayerSettings.WebGL.maximumMemorySize = 512;
            PlayerSettings.runInBackground = true;

            UnityEditor.WebGL.UserBuildSettings.codeOptimization =
                UnityEditor.WebGL.WasmCodeOptimization.DiskSizeLTO;

            // Ensure vSyncCount is 0 across quality levels so WebGL relies cleanly on browser requestAnimationFrame.
            int levelCount = QualitySettings.names.Length;
            int previousLevel = QualitySettings.GetQualityLevel();
            for (int i = 0; i < levelCount; i++)
            {
                QualitySettings.SetQualityLevel(i, false);
                QualitySettings.vSyncCount = 0;
            }

            // Default WebGL to index 0 (Mobile URP profile) for consistent cross-device performance on phones and PC.
            int webQualityIndex = System.Array.IndexOf(QualitySettings.names, "Mobile");
            if (webQualityIndex < 0)
            {
                webQualityIndex = previousLevel;
            }
            QualitySettings.SetQualityLevel(webQualityIndex, true);

            AssetDatabase.SaveAssets();
            Debug.Log($"[WebOptimizer] Applied WebGL release settings (Quality={QualitySettings.names[webQualityIndex]}, Compression={PlayerSettings.WebGL.compressionFormat}, ExceptionSupport={PlayerSettings.WebGL.exceptionSupport}, MaxMemMB={PlayerSettings.WebGL.maximumMemorySize}).");
        }
    }
}
