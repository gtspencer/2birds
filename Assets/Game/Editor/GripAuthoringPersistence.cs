#if UNITY_INCLUDE_INSTRUMENTATION
using System;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace TwoBirds.Editor
{
    [InitializeOnLoad]
    public static class GripAuthoringPersistence
    {
        private const string SceneBackupKey = "TwoBirds.GripAuthoring.PlayScenes";
        [Serializable] private sealed class SceneBackup { public string[] Paths; public bool[] Enabled; }
        static GripAuthoringPersistence() => EditorApplication.playModeStateChanged += PlayModeChanged;
        private static void PlayModeChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingEditMode && UnityEngine.SceneManagement.SceneManager.GetActiveScene().path == GripAuthoringScene.ScenePath)
                IncludeForPlay();
            if (state == PlayModeStateChange.EnteredEditMode) RestoreScenes();
        }
        private static void IncludeForPlay()
        {
            if (EditorBuildSettings.scenes.Any(scene => scene.path == GripAuthoringScene.ScenePath && scene.enabled)) return;
            var previousScenes = EditorBuildSettings.scenes;
            if (string.IsNullOrEmpty(SessionState.GetString(SceneBackupKey, "")))
                SessionState.SetString(SceneBackupKey, JsonUtility.ToJson(new SceneBackup
                { Paths = previousScenes.Select(scene => scene.path).ToArray(), Enabled = previousScenes.Select(scene => scene.enabled).ToArray() }));
            EditorBuildSettings.scenes = previousScenes.Where(scene => scene.path != GripAuthoringScene.ScenePath)
                .Append(new EditorBuildSettingsScene(GripAuthoringScene.ScenePath, true)).ToArray();
        }
        private static void RestoreScenes()
        {
            string json = SessionState.GetString(SceneBackupKey, "");
            if (string.IsNullOrEmpty(json)) return;
            var previous = JsonUtility.FromJson<SceneBackup>(json);
            EditorBuildSettings.scenes = previous.Paths.Select((path, i) => new EditorBuildSettingsScene(path, previous.Enabled[i])).ToArray();
            SessionState.EraseString(SceneBackupKey);
        }
    }
}
#endif
