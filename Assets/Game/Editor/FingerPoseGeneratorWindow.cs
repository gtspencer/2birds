#if UNITY_INCLUDE_INSTRUMENTATION
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace TwoBirds.Editor
{
    public sealed class FingerPoseGeneratorWindow : EditorWindow
    {
        private const string AvatarRegistryPath = "Assets/Game/Settings/Avatars/AvatarRegistry.asset";
        private const string ScenePath = "Assets/Scenes/FingerPoseGenerator.unity";
        private const string ClipFolder = "Assets/Art/Animations/Hands";
        private static readonly string[] Sides = { "Right", "Left" };
        private static readonly string[] Fingers = { "Thumb", "Index", "Middle", "Ring", "Little" };
        private const int Parts = 4, PerHand = 20, Slots = 40;
        // Slot = hand * PerHand + finger * Parts + part; parts 0-2 are the joints (1/2/3 Stretched), 3 is Spread.
        private static readonly int[] Muscles = new int[Slots];
        private static readonly EditorCurveBinding[] Bindings = new EditorCurveBinding[Slots];

        [SerializeField] private AvatarId avatarId;
        [SerializeField] private bool firstPerson, left;
        [SerializeField] private AnimationClip sourceClip;
        [SerializeField] private float[] values = new float[Slots], loaded = new float[Slots];
        [SerializeField] private bool[] jointsOpen = new bool[Fingers.Length];
        private AvatarRegistry registry;
        private GameObject preview;
        private Animator animator;
        private HumanPoseHandler handler;
        private HumanPose pose;
        private readonly List<Action> refreshers = new();

        static FingerPoseGeneratorWindow()
        {
            for (int hand = 0; hand < 2; hand++)
                for (int finger = 0; finger < Fingers.Length; finger++)
                    for (int part = 0; part < Parts; part++)
                    {
                        string side = Sides[hand], name = Fingers[finger];
                        bool spread = part == Parts - 1;
                        int slot = hand * PerHand + finger * Parts + part;
                        Muscles[slot] = Array.IndexOf(HumanTrait.MuscleName, spread ? $"{side} {name} Spread" : $"{side} {name} {part + 1} Stretched");
                        Bindings[slot] = EditorCurveBinding.FloatCurve("", typeof(Animator),
                            spread ? $"{side}Hand.{name} Spread" : $"{side}Hand.{name}.{part + 1} Stretched");
                    }
        }

        [MenuItem("Two Birds/Finger Pose Generator")]
        public static void Open() => GetWindow<FingerPoseGeneratorWindow>("Finger Pose Generator");

        private void OnEnable()
        {
            registry = AssetDatabase.LoadAssetAtPath<AvatarRegistry>(AvatarRegistryPath);
            if (registry && !avatarId.IsValid) avatarId = registry.DefaultId;
            if (!sourceClip && registry && registry.Animations)
            {
                sourceClip = registry.Animations.RelaxedFingers;
                ReadClip();
            }
            EditorApplication.playModeStateChanged += PlayModeChanged;
            EditorSceneManager.sceneOpened += SceneOpened;
            Undo.undoRedoPerformed += UndoRedo;
            Spawn();
        }

        private void OnDisable()
        {
            EditorApplication.playModeStateChanged -= PlayModeChanged;
            EditorSceneManager.sceneOpened -= SceneOpened;
            Undo.undoRedoPerformed -= UndoRedo;
            Despawn();
        }

        public void CreateGUI() => Rebuild();

        private void PlayModeChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingEditMode) Despawn();
            if (state == PlayModeStateChange.EnteredEditMode) Spawn();
            if (state is PlayModeStateChange.EnteredEditMode or PlayModeStateChange.EnteredPlayMode) Rebuild();
        }

        private void SceneOpened(Scene scene, OpenSceneMode mode) => Spawn();

        private void UndoRedo() { Refresh(); ApplyPose(); }

        private void Spawn()
        {
            Despawn();
            if (EditorApplication.isPlayingOrWillChangePlaymode || !registry || !registry.TryResolve(avatarId, out var entry) || !entry.Settings) return;
            var data = firstPerson ? entry.Settings.FirstPersonGenerated : entry.Settings.Generated;
            if (!data.Source || !data.HumanoidAvatar) return;
            preview = Instantiate(data.Source, Vector3.zero, Quaternion.identity);
            preview.name = $"Finger preview ({entry.Settings.DisplayName})";
            foreach (var child in preview.GetComponentsInChildren<Transform>(true)) child.gameObject.hideFlags = HideFlags.DontSave;
            animator = preview.GetComponent<Animator>();
            if (!animator) animator = preview.AddComponent<Animator>();
            animator.avatar = data.HumanoidAvatar;
            handler = new HumanPoseHandler(data.HumanoidAvatar, preview.transform);
            ApplyPose();
        }

        private void Despawn()
        {
            handler?.Dispose();
            handler = null;
            if (preview) DestroyImmediate(preview);
            preview = null; animator = null;
        }

        private void ApplyPose()
        {
            if (handler == null) return;
            handler.GetHumanPose(ref pose);
            for (int slot = 0; slot < Slots; slot++) if (Muscles[slot] >= 0) pose.muscles[Muscles[slot]] = values[slot];
            handler.SetHumanPose(ref pose);
            SceneView.RepaintAll();
        }

        private void ReadClip()
        {
            if (!sourceClip) return;
            for (int slot = 0; slot < Slots; slot++)
            {
                var curve = AnimationUtility.GetEditorCurve(sourceClip, Bindings[slot]);
                values[slot] = loaded[slot] = curve != null ? curve.Evaluate(0f) : 0f;
            }
        }

        private void Change(string undo, Action change)
        {
            Undo.RecordObject(this, undo);
            change();
            Refresh(); ApplyPose();
        }

        private void Rebuild()
        {
            var root = rootVisualElement;
            root.Clear(); refreshers.Clear();
            root.style.paddingLeft = 4; root.style.paddingRight = 4;
            if (!registry) { root.Add(new Label("Avatar registry not found.")); return; }
            var open = new Button(OpenScene) { text = "Open scene", tooltip = ScenePath };
            open.SetEnabled(!EditorApplication.isPlaying);
            root.Add(open);
            if (EditorApplication.isPlaying) { root.Add(new Label("Exit Play Mode to edit finger poses.")); return; }

            var entries = registry.Entries.Where(entry => entry != null && entry.Settings).ToList();
            if (entries.Count > 0)
            {
                int index = Mathf.Max(0, entries.FindIndex(entry => entry.Id == avatarId));
                var row = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center } };
                row.Add(new Label("Avatar") { style = { width = 60 } });
                void Step(int delta) { avatarId = entries[(index + delta + entries.Count) % entries.Count].Id; Spawn(); Rebuild(); }
                row.Add(new Button(() => Step(-1)) { text = "◀" });
                row.Add(new Label(entries[index].Settings.DisplayName) { style = { flexGrow = 1, unityTextAlign = TextAnchor.MiddleCenter } });
                row.Add(new Button(() => Step(1)) { text = "▶" });
                root.Add(row);
            }
            var view = new RadioButtonGroup("View", new List<string> { "Third person", "First person" }) { value = firstPerson ? 1 : 0 };
            view.RegisterValueChangedCallback(evt => { firstPerson = evt.newValue == 1; Spawn(); Rebuild(); });
            root.Add(view);
            if (!preview) root.Add(new Label("The avatar has no generated source or Humanoid avatar for this view."));

            var clipRow = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, marginTop = 4 } };
            var clipField = new ObjectField("Source clip") { objectType = typeof(AnimationClip), allowSceneObjects = false, value = sourceClip, style = { flexGrow = 1 } };
            clipField.RegisterValueChangedCallback(evt => sourceClip = evt.newValue as AnimationClip);
            clipRow.Add(clipField);
            clipRow.Add(new Button(() => Change("Load finger pose", ReadClip)) { text = "Load", tooltip = "Read the finger muscles from the clip." });
            root.Add(clipRow);

            var hand = new RadioButtonGroup("Hand", new List<string> { "Right", "Left" }) { value = left ? 1 : 0, style = { marginTop = 4 } };
            hand.RegisterValueChangedCallback(evt => { left = evt.newValue == 1; Rebuild(); });
            root.Add(hand);
            int offset = left ? PerHand : 0;
            for (int finger = 0; finger < Fingers.Length; finger++) FingerControls(root, offset + finger * Parts, finger);

            var tools = new VisualElement { style = { flexDirection = FlexDirection.Row, flexWrap = Wrap.Wrap, marginTop = 4 } };
            tools.Add(new Button(() => Change("Mirror finger pose", () => Array.Copy(values, 0, values, PerHand, PerHand))) { text = "Mirror R→L" });
            tools.Add(new Button(() => Change("Mirror finger pose", () => Array.Copy(values, PerHand, values, 0, PerHand))) { text = "Mirror L→R" });
            tools.Add(new Button(() => Change("Reset finger pose", () => loaded.CopyTo(values, 0))) { text = "Reset to loaded" });
            tools.Add(new Button(FrameHand) { text = "Frame hand" });
            root.Add(tools);
            var save = new VisualElement { style = { flexDirection = FlexDirection.Row, flexWrap = Wrap.Wrap, marginTop = 4 } };
            save.Add(new Button(Save) { text = "Save", tooltip = "Overwrite the source clip in place. References to it stay valid." });
            save.Add(new Button(SaveAs) { text = "Save as…", tooltip = "Write a copy of the source clip with these finger muscles." });
            root.Add(save);
            Refresh();
        }

        private void FingerControls(VisualElement root, int first, int finger)
        {
            var box = new Box { style = { marginTop = 4, paddingLeft = 4, paddingRight = 4, paddingTop = 2, paddingBottom = 2 } };
            box.Add(new Label(Fingers[finger]) { style = { unityFontStyleAndWeight = FontStyle.Bold } });
            var curl = new Slider("Curl", -1f, 1f) { showInputField = true, tooltip = "Sets all three joints." };
            curl.RegisterValueChangedCallback(evt => Change("Curl finger", () => { for (int part = 0; part < 3; part++) values[first + part] = evt.newValue; }));
            refreshers.Add(() => curl.SetValueWithoutNotify((values[first] + values[first + 1] + values[first + 2]) / 3f));
            box.Add(curl);
            box.Add(MuscleSlider("Spread", first + 3));
            var joints = new Foldout { text = "Joints", value = jointsOpen[finger] };
            joints.RegisterValueChangedCallback(evt => { if (evt.target == joints) jointsOpen[finger] = evt.newValue; });
            for (int part = 0; part < 3; part++) joints.Add(MuscleSlider($"Joint {part + 1}", first + part));
            box.Add(joints);
            root.Add(box);
        }

        private Slider MuscleSlider(string label, int slot)
        {
            var slider = new Slider(label, -1f, 1f) { showInputField = true };
            slider.RegisterValueChangedCallback(evt => Change("Edit finger muscle", () => values[slot] = evt.newValue));
            refreshers.Add(() => slider.SetValueWithoutNotify(values[slot]));
            return slider;
        }

        private void Refresh() { foreach (var refresh in refreshers) refresh(); }

        private void FrameHand()
        {
            var bone = animator ? animator.GetBoneTransform(left ? HumanBodyBones.LeftHand : HumanBodyBones.RightHand) : null;
            if (bone) SceneView.lastActiveSceneView?.Frame(new Bounds(bone.position, Vector3.one * 0.25f), false);
        }

        private void OpenScene()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            EditorSceneManager.OpenScene(ScenePath);
        }

        private void WriteCurves(AnimationClip clip)
        {
            for (int slot = 0; slot < Slots; slot++)
                AnimationUtility.SetEditorCurve(clip, Bindings[slot], new AnimationCurve(new Keyframe(0f, values[slot])));
        }

        private void Save()
        {
            if (!sourceClip || !EditorUtility.DisplayDialog("Finger Pose Generator",
                $"Overwrite {sourceClip.name}? Every item and avatar using it will change.", "Overwrite", "Cancel")) return;
            Undo.RecordObject(sourceClip, "Save finger pose");
            WriteCurves(sourceClip);
            EditorUtility.SetDirty(sourceClip);
            AssetDatabase.SaveAssetIfDirty(sourceClip);
            values.CopyTo(loaded, 0);
        }

        private void SaveAs()
        {
            if (!sourceClip) return;
            string path = EditorUtility.SaveFilePanelInProject("Save finger pose", sourceClip.name, "anim", "Save the finger pose as a clip.", ClipFolder);
            if (string.IsNullOrEmpty(path)) return;
            var clip = Instantiate(sourceClip);
            clip.name = Path.GetFileNameWithoutExtension(path);
            WriteCurves(clip);
            var existing = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
            if (existing)
            {
                EditorUtility.CopySerialized(clip, existing);
                DestroyImmediate(clip);
                clip = existing;
                AssetDatabase.SaveAssetIfDirty(clip);
            }
            else AssetDatabase.CreateAsset(clip, path);
            sourceClip = clip;
            values.CopyTo(loaded, 0);
            Rebuild();
        }
    }
}
#endif
