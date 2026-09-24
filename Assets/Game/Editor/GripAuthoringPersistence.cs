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
        static GripAuthoringPersistence()
        {
            GripAuthoringScene.EditorSaveRequested += record => Save(GripAuthoringSession.Drafts, record);
            GripAuthoringSession.Changed += SessionChanged;
            EditorApplication.playModeStateChanged += PlayModeChanged;
        }
        private static void SessionChanged()
        {
            if (GripAuthoringSession.Drafts != null) IncludeForPlay();
            else RestoreScenes();
        }
        private static void PlayModeChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingEditMode && UnityEngine.SceneManagement.SceneManager.GetActiveScene().path == GripAuthoringSession.ScenePath)
                IncludeForPlay();
            if (state == PlayModeStateChange.EnteredEditMode) RestoreScenes();
        }
        private static void IncludeForPlay()
        {
            if (EditorBuildSettings.scenes.Any(scene => scene.path == GripAuthoringSession.ScenePath && scene.enabled)) return;
            var previousScenes = EditorBuildSettings.scenes;
            if (string.IsNullOrEmpty(SessionState.GetString(SceneBackupKey, "")))
                SessionState.SetString(SceneBackupKey, JsonUtility.ToJson(new SceneBackup
                { Paths = previousScenes.Select(scene => scene.path).ToArray(), Enabled = previousScenes.Select(scene => scene.enabled).ToArray() }));
            EditorBuildSettings.scenes = previousScenes.Where(scene => scene.path != GripAuthoringSession.ScenePath)
                .Append(new EditorBuildSettingsScene(GripAuthoringSession.ScenePath, true)).ToArray();
        }
        private static void RestoreScenes()
        {
            string json = SessionState.GetString(SceneBackupKey, "");
            if (string.IsNullOrEmpty(json)) return;
            var previous = JsonUtility.FromJson<SceneBackup>(json);
            EditorBuildSettings.scenes = previous.Paths.Select((path, i) => new EditorBuildSettingsScene(path, previous.Enabled[i])).ToArray();
            SessionState.EraseString(SceneBackupKey);
        }
        public static void Save(GripAuthoringDrafts drafts, GripAuthoringDraft record)
        {
            if (record == null) return;
            string path = string.IsNullOrEmpty(record.SourceIdentity) ? "" : AssetDatabase.GUIDToAssetPath(record.SourceIdentity);
            var source = string.IsNullOrEmpty(path) ? record.Source : AssetDatabase.LoadAssetAtPath<ScriptableObject>(path);
            if (!source) throw new InvalidOperationException($"Source asset for {record.DisplayName} is missing.");
            Undo.RecordObject(source, "Save " + record.DisplayName + " grip authoring");
            GripAuthoringDraft.CopyFields(record.Values, source);
            GripAuthoringDraft.Notify(source);
            EditorUtility.SetDirty(source); AssetDatabase.SaveAssetIfDirty(source);
            record.Source = source; drafts.Saved(record);
        }
    }
}
#endif
