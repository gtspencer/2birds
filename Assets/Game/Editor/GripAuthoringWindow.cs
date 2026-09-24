#if UNITY_INCLUDE_INSTRUMENTATION
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace TwoBirds.Editor
{
    public sealed class GripAuthoringWindow : EditorWindow
    {
        [SerializeField] private GripAuthoringDrafts drafts = new();
        [SerializeField] private string handlePath = "RightPalmContact";
        private GripAuthoringPanel panel;
        private readonly Dictionary<string, ScriptableObject> runtime = new();
        private bool dragging;

        [MenuItem("Two Birds/Grip Authoring")]
        public static void Open() => GetWindow<GripAuthoringWindow>("Grip Authoring");

        private void OnEnable()
        {
            if (GripAuthoringSession.Drafts != null) drafts = GripAuthoringSession.Drafts;
            EnsureDrafts();
            GripAuthoringSession.EditorDrafts = SupplyDrafts;
            GripAuthoringSession.BeforeEditorEdit = RecordEdit;
            GripAuthoringSession.RetainForEditor(drafts);
            GripAuthoringScene.SceneChanged += Rebuild;
            drafts.Changed += Changed;
            Undo.undoRedoPerformed += UndoRedo;
            EditorApplication.playModeStateChanged += PlayModeChanged;
            SceneView.duringSceneGui += SceneGUI;
            Changed();
        }
        private GripAuthoringDrafts SupplyDrafts() { EnsureDrafts(); return drafts; }
        private void EnsureDrafts()
        {
            if (!drafts.Items) drafts.Initialize(
                AssetDatabase.LoadAssetAtPath<ItemRegistry>("Assets/Game/ScriptableObjects/ItemRegistry.asset"),
                AssetDatabase.LoadAssetAtPath<AvatarRegistry>("Assets/Game/Settings/Avatars/AvatarRegistry.asset"));
            foreach (var record in drafts.Records)
            {
                record.SourceIdentity = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(record.Source));
                runtime[record.Key] = record.Runtime;
            }
        }
        public void CreateGUI() => Rebuild();
        private void Rebuild()
        {
            if (!drafts.Items) return;
            panel?.Dispose(); rootVisualElement.Clear();
            var toolbar = new Toolbar();
            toolbar.Add(new ToolbarButton(StartAuthoring) { text = "Open Scene / Start Authoring" });
            toolbar.Add(new ToolbarButton(FrameSelected) { text = "Frame Selected" });
            var handle = new ToolbarMenu { text = handlePath };
            foreach (string path in new[] { "RightPalmContact", "LeftPalmContact", "PullingPalmContact", "LeftPalmCorrection", "RightPalmCorrection",
                "Pose.RightPalm", "Pose.LeftPalm" })
            { string selected = path; handle.menu.AppendAction(path, _ => { handlePath = selected; handle.text = selected; SceneView.RepaintAll(); }); }
            toolbar.Add(handle); rootVisualElement.Add(toolbar);
            var content = new VisualElement { style = { flexGrow = 1 } }; rootVisualElement.Add(content);
            AssetDatabase.LoadAssetAtPath<VisualTreeAsset>("Assets/Game/UI/GripAuthoring/GripAuthoring.uxml").CloneTree(content);
            panel = new GripAuthoringPanel(content, drafts, GripAuthoringScene.Instance && GripAuthoringScene.Instance.Views != null ? GripAuthoringScene.Instance : null)
            {
                Save = record => GripAuthoringPersistence.Save(drafts, record), OpenFolder = GripAuthoringExport.OpenFolder,
                SavePose = capture => GripAuthoringPersistence.SavePose(drafts, capture),
                BeforeEdit = RecordEdit, Inspect = value => { Selection.activeObject = value; EditorGUIUtility.PingObject(value); },
                SelectHandle = path => { handlePath = path; SceneView.RepaintAll(); },
                ClipField = (path, value, changed) =>
                {
                    var field = new ObjectField(path) { objectType = typeof(AnimationClip), allowSceneObjects = false, value = value };
                    field.RegisterValueChangedCallback(evt => changed(evt.newValue as AnimationClip)); return field;
                }
            };
            panel.Rebuild();
        }
        private void RecordEdit() => Undo.RecordObject(this, "Edit grip draft");
        private void Changed()
        {
            hasUnsavedChanges = drafts.Records.Any(record => record.Dirty);
            saveChangesMessage = "Save these grip authoring contexts?\n" + string.Join("\n", drafts.Records.Where(record => record.Dirty).Select(record => record.DisplayName));
            EditorUtility.SetDirty(this); Repaint();
        }
        private void UndoRedo()
        {
            foreach (var record in drafts.Records)
            {
                if (!record.Runtime && runtime.TryGetValue(record.Key, out var copy)) record.Runtime = copy;
                record.RefreshBaseline();
            }
            drafts.Refresh(); Rebuild(); SceneView.RepaintAll();
        }
        private void PlayModeChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.EnteredEditMode)
            {
                GripAuthoringSession.ResetEditorSession();
                drafts.DisposeRuntime(); EnsureDrafts(); Rebuild();
            }
            if (state == PlayModeStateChange.EnteredPlayMode) { EnsureDrafts(); Rebuild(); }
        }
        private void StartAuthoring()
        {
            if (EditorApplication.isPlaying)
            { if (SessionController.Instance) SessionController.Instance.StartGripAuthoring(); return; }
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            EditorSceneManager.OpenScene(GripAuthoringSession.ScenePath);
            EditorApplication.isPlaying = true;
        }
        private void SceneGUI(SceneView view)
        {
            var scene = GripAuthoringScene.Instance;
            if (!EditorApplication.isPlaying || !scene) return;
            if (handlePath.StartsWith("Pose."))
            {
                bool right = handlePath == "Pose.RightPalm";
                if (!scene.TryPoseHandle(right, out var palm)) return;
                Handles.color = Color.yellow; Handles.Label(palm.position, handlePath);
                EditorGUI.BeginChangeCheck();
                Vector3 palmPosition = Handles.PositionHandle(palm.position, palm.rotation);
                Quaternion palmRotation = Handles.RotationHandle(palm.rotation, palm.position);
                if (EditorGUI.EndChangeCheck()) scene.SetPosePalm(right, new Pose(palmPosition, palmRotation));
                return;
            }
            if (!CompatibleHandle() || !scene.TryHandle(handlePath, out var handle)) return;
            Pose pose = handle.World;
            Handles.color = Color.yellow; Handles.Label(pose.position, handlePath);
            EditorGUI.BeginChangeCheck();
            Vector3 position = Handles.PositionHandle(pose.position, pose.rotation);
            Quaternion rotation = string.IsNullOrEmpty(handle.EulerField) ? pose.rotation : Handles.RotationHandle(pose.rotation, pose.position);
            if (EditorGUI.EndChangeCheck())
            {
                if (!dragging) { Undo.RegisterCompleteObjectUndo(this, "Move grip authoring handle"); dragging = true; }
                scene.SetEditing(true);
                drafts.Edit(drafts.Current, () => handle.Apply(drafts.Current, new Pose(position, rotation)));
            }
            if (Event.current.rawType == EventType.MouseUp) dragging = false;
            if (scene.TryPalmReadout(handlePath.StartsWith("FirstPerson"), out var requested, out var evaluated, out var left, out var evaluatedLeft, out bool hasLeft))
            {
                DrawContact(requested, evaluated); if (hasLeft) DrawContact(left, evaluatedLeft);
            }
        }
        private bool CompatibleHandle() => drafts.Current != null && (drafts.Context == GripAuthoringContext.AvatarCalibration
            ? handlePath.EndsWith("Correction")
            : drafts.Context == GripAuthoringContext.Item && handlePath.EndsWith("Contact") &&
                (handlePath != "LeftPalmContact" || ((ItemDefinition)drafts.Current.Runtime).HoldMode == ItemHoldMode.TwoHand) &&
                (handlePath != "PullingPalmContact" || drafts.Current.Runtime is SlingshotDefinition));
        private static void DrawContact(Pose requested, Pose evaluated)
        {
            Handles.color = Color.yellow; Handles.SphereHandleCap(0, requested.position, requested.rotation, 0.018f, EventType.Repaint);
            Handles.color = Color.cyan; Handles.SphereHandleCap(0, evaluated.position, evaluated.rotation, 0.012f, EventType.Repaint);
            Handles.DrawLine(requested.position, evaluated.position);
        }
        private void FrameSelected()
        {
            if (GripAuthoringScene.Instance && CompatibleHandle() && GripAuthoringScene.Instance.TryHandle(handlePath, out var handle))
                SceneView.lastActiveSceneView?.Frame(new Bounds(handle.World.position, Vector3.one * 0.7f), false);
        }
        private void Update() { panel?.UpdateReadouts(); }
        public override void SaveChanges()
        {
            foreach (var record in drafts.Records.Where(record => record.Dirty).ToArray()) GripAuthoringPersistence.Save(drafts, record);
            base.SaveChanges();
        }
        public override void DiscardChanges()
        {
            foreach (var record in drafts.Records.Where(record => record.Dirty).ToArray()) drafts.Revert(record);
            base.DiscardChanges();
        }
        private void OnDisable()
        {
            panel?.Dispose(); panel = null;
            drafts.Changed -= Changed; GripAuthoringScene.SceneChanged -= Rebuild;
            Undo.undoRedoPerformed -= UndoRedo; SceneView.duringSceneGui -= SceneGUI;
            EditorApplication.playModeStateChanged -= PlayModeChanged;
            GripAuthoringSession.EditorDrafts = null;
            GripAuthoringSession.BeforeEditorEdit = null;
            if (GripAuthoringSession.Drafts == drafts) GripAuthoringSession.ReleaseToSession(); else drafts.DisposeRuntime();
        }
    }
}
#endif
