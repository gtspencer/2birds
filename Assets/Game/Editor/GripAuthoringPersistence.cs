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
            GripAuthoringScene.EditorPoseSaveRequested += capture => SavePose(GripAuthoringSession.Drafts, capture);
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
        public static void SavePose(GripAuthoringDrafts drafts, GripPoseCapture capture)
        {
            var record = drafts.Find("shared-held-item-settings");
            string slot = (capture.FirstPerson ? "FirstPerson" : "ThirdPerson") + (capture.Charged ? "Charged" : "Hold");
            string field = capture.Mode + "." + slot;
            var clip = new AnimationClip { name = capture.Mode + slot };
            for (int i = 0; i < HumanTrait.MuscleCount; i++)
                if ((HumanBodyBones)HumanTrait.BoneFromMuscle(i) is HumanBodyBones.LeftShoulder or HumanBodyBones.RightShoulder or
                    HumanBodyBones.LeftUpperArm or HumanBodyBones.RightUpperArm or HumanBodyBones.LeftLowerArm or
                    HumanBodyBones.RightLowerArm or HumanBodyBones.LeftHand or HumanBodyBones.RightHand)
                    AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("", typeof(Animator), HumanTrait.MuscleName[i]),
                        new AnimationCurve(new Keyframe(0f, capture.Muscles[i])));
            const string folder = "Assets/Art/Animations/HeldPoses";
            var existing = GripAuthoringFields.Get(record.Values, field) as AnimationClip;
            if (existing && AssetDatabase.IsMainAsset(existing) && AssetDatabase.GetAssetPath(existing).StartsWith(folder + "/"))
            {
                clip.name = existing.name;
                EditorUtility.CopySerialized(clip, existing); UnityEngine.Object.DestroyImmediate(clip); clip = existing;
                EditorUtility.SetDirty(clip); AssetDatabase.SaveAssetIfDirty(clip);
            }
            else
            {
                if (!AssetDatabase.IsValidFolder(folder)) AssetDatabase.CreateFolder("Assets/Art/Animations", "HeldPoses");
                AssetDatabase.CreateAsset(clip, AssetDatabase.GenerateUniqueAssetPath($"{folder}/{clip.name}.anim"));
            }
            drafts.Edit(record, () => GripAuthoringFields.Set(record.Values, field, clip));
        }
    }
}
#endif
