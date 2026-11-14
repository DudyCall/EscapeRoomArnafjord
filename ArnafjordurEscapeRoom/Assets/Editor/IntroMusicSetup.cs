using System.IO;
using Arnafjordur.Intro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Arnafjordur.Editor
{
    /// <summary>
    /// Imports the intro song and puts an <see cref="IntroMusic"/> player in every intro scene.
    /// </summary>
    public static class IntroMusicSetup
    {
        private const string SongPath = "Assets/Audio/Music/HowYouRemindMe.ogg";      // converted from the downloaded file, which is MP4 audio despite its .mp3 name
        private const float StartAtSeconds = 62f;
        private const float Volume = 0.22f;

        private static readonly string[] Scenes =
        {
            "Assets/Scenes/Intro_TunnelDrive.unity",
            "Assets/Scenes/Intro_TunnelExit.unity",
            "Assets/Scenes/Intro_ChurchArrival.unity",
            "Assets/Scenes/Intro_CarExit.unity",
        };

        [MenuItem("Tools/Intro/Add Music To Intro Scenes")]
        public static void Build()
        {
            if (LoadSong() == null)
            {
                Debug.LogError("[IntroMusicSetup] song missing at " + SongPath);
                return;
            }
            string opened = EditorSceneManager.GetActiveScene().path;
            var report = "[IntroMusicSetup] OK";
            foreach (string path in Scenes)
            {
                if (!File.Exists(path)) continue;
                var scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
                AddToOpenScene();
                EditorSceneManager.MarkSceneDirty(scene);
                EditorSceneManager.SaveScene(scene);
                report += "\n  " + path;
            }
            if (!string.IsNullOrEmpty(opened) && File.Exists(opened)) EditorSceneManager.OpenScene(opened, OpenSceneMode.Single);
            Debug.Log(report);
        }

        /// <summary>Adds or refreshes the music player in the scene being built. Safe to call repeatedly.</summary>
        public static void AddToOpenScene()
        {
            AudioClip song = LoadSong();
            if (song == null) return;                    // no song imported: the intro simply plays silent
            var music = Object.FindAnyObjectByType<IntroMusic>();
            if (music == null) music = new GameObject("IntroMusic").AddComponent<IntroMusic>();
            music.Configure(song, StartAtSeconds, Volume);
            EditorUtility.SetDirty(music);
        }

        private static AudioClip LoadSong()
        {
            var importer = AssetImporter.GetAtPath(SongPath) as AudioImporter;
            if (importer == null) return null;
            var settings = importer.defaultSampleSettings;
            if (settings.loadType != AudioClipLoadType.CompressedInMemory || !Mathf.Approximately(settings.quality, 0.6f))
            {
                settings.loadType = AudioClipLoadType.CompressedInMemory;      // long track: keep it compressed, also on Web
                settings.compressionFormat = AudioCompressionFormat.Vorbis;
                settings.quality = 0.6f;
                importer.defaultSampleSettings = settings;
                importer.loadInBackground = false;
                importer.SaveAndReimport();
            }
            return AssetDatabase.LoadAssetAtPath<AudioClip>(SongPath);
        }
    }
}
