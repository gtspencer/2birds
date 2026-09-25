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
        private enum EditLayer { HandPose, ItemOffset, AvatarOffset, None }
        private const string ItemRegistryPath = "Assets/Game/ScriptableObjects/ItemRegistry.asset";
        private const string AvatarRegistryPath = "Assets/Game/Settings/Avatars/AvatarRegistry.asset";
        private static readonly string[] PhaseNames = { "Live", "Hold", "Charged" };
        [SerializeField] private int itemId;
        [SerializeField] private AvatarId avatarId;
        [SerializeField] private bool firstPerson, lookThrough, ownerHidden, toolsHidden, savedToolsHidden;
        [SerializeField] private int savedLayers;
        [SerializeField] private GripAuthoringPhase phase;
        [SerializeField] private EditLayer layer = EditLayer.HandPose;
        private ItemRegistry items;
        private AvatarRegistry avatars;
        private readonly List<Action> refreshers = new();
        private Label readouts, unsaved;
        private ToolbarButton save;
        private RadioButtonGroup phaseGroup;
        private Toggle walk;
        private VisualElement actions;
        private bool dragging, draggingRight;
        private static Pose gizmoStart;

        [MenuItem("Two Birds/Grip Authoring")]
        public static void Open() => GetWindow<GripAuthoringWindow>("Grip Authoring");

        private static GripAuthoringScene Scene =>
            EditorApplication.isPlaying && GripAuthoringScene.Instance && GripAuthoringScene.Instance.Attached ? GripAuthoringScene.Instance : null;
        private ItemDefinition Definition => items ? items.Get((byte)itemId) : null;

        private void OnEnable()
        {
            items = AssetDatabase.LoadAssetAtPath<ItemRegistry>(ItemRegistryPath);
            avatars = AssetDatabase.LoadAssetAtPath<AvatarRegistry>(AvatarRegistryPath);
            if (!Definition && items) itemId = items.Items.FirstOrDefault(item => item)?.ItemId ?? 0;
            if (!avatarId.IsValid && avatars) avatarId = avatars.DefaultId;
            GripAuthoringScene.SceneChanged += SceneChanged;
            GripAuthoringAssets.Changed += AssetsChanged;
            Undo.undoRedoPerformed += UndoRedo;
            EditorApplication.playModeStateChanged += PlayModeChanged;
            SceneView.duringSceneGui += SceneGUI;
            AssetsChanged();
        }

        private void OnDisable()
        {
            GripAuthoringScene.SceneChanged -= SceneChanged;
            GripAuthoringAssets.Changed -= AssetsChanged;
            Undo.undoRedoPerformed -= UndoRedo;
            EditorApplication.playModeStateChanged -= PlayModeChanged;
            SceneView.duringSceneGui -= SceneGUI;
            HideOwner(false); HideTools(false);
        }

        public void CreateGUI() => Rebuild();

        private void SceneChanged()
        {
            var scene = Scene;
            if (scene)
            {
                scene.SelectAvatar(avatarId); scene.SelectItem((byte)itemId); scene.SetPhase(phase);
            }
            HideOwner(scene); HideTools(scene && layer != EditLayer.None);
            Rebuild();
        }

        private void PlayModeChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingPlayMode)
            {
                dragging = false;
                if (GripAuthoringAssets.Unsaved.Count > 0)
                {
                    if (EditorUtility.DisplayDialog("Grip Authoring", saveChangesMessage, "Save", "Revert")) GripAuthoringAssets.SaveAll();
                    else GripAuthoringAssets.RevertAll();
                }
                HideOwner(false); HideTools(false);
            }
            if (state is PlayModeStateChange.EnteredEditMode or PlayModeStateChange.EnteredPlayMode) Rebuild();
        }

        private void HideOwner(bool hide)
        {
            if (hide == ownerHidden) return;
            ownerHidden = hide;
            if (hide) { savedLayers = Tools.visibleLayers; Tools.visibleLayers &= ~LayerMask.GetMask("GripAuthoringOwner"); }
            else Tools.visibleLayers = savedLayers;
            SceneView.RepaintAll();
        }

        private void HideTools(bool hide)
        {
            if (hide == toolsHidden) return;
            toolsHidden = hide;
            if (hide) { savedToolsHidden = Tools.hidden; Tools.hidden = true; }
            else Tools.hidden = savedToolsHidden;
        }

        private void AssetsChanged()
        {
            var names = GripAuthoringAssets.Unsaved.Where(asset => asset).Select(asset => asset.name).ToArray();
            hasUnsavedChanges = names.Length > 0;
            saveChangesMessage = "Save grip authoring changes?\n" + string.Join("\n", names);
            if (save != null) save.text = $"Save ({names.Length})";
            if (unsaved != null) unsaved.text = names.Length > 0 ? "Unsaved: " + string.Join(", ", names) : "No unsaved changes";
        }

        private void UndoRedo()
        {
            GripAuthoringAssets.Notify();
            Scene?.ContentEdited();
        }

        public override void SaveChanges() { GripAuthoringAssets.SaveAll(); base.SaveChanges(); }
        public override void DiscardChanges() { GripAuthoringAssets.RevertAll(); Scene?.ContentEdited(); base.DiscardChanges(); }

        private void Rebuild()
        {
            var root = rootVisualElement;
            root.Clear(); refreshers.Clear();
            if (!items || !avatars) { root.Add(new Label("Item or avatar registry not found.")); return; }
            var scene = Scene;
            var toolbar = new Toolbar();
            toolbar.Add(new ToolbarButton(StartAuthoring) { text = "Start Authoring" });
            toolbar.Add(new ToolbarButton(Frame) { text = "Frame" });
            var look = new ToolbarToggle { text = "Look through FP camera", value = lookThrough };
            look.SetEnabled(firstPerson);
            look.RegisterValueChangedCallback(evt => lookThrough = evt.newValue);
            toolbar.Add(look);
            toolbar.Add(new ToolbarSpacer { flex = true });
            save = new ToolbarButton(() => GripAuthoringAssets.SaveAll());
            toolbar.Add(save);
            toolbar.Add(new ToolbarButton(() => { GripAuthoringAssets.RevertAll(); Scene?.ContentEdited(); Rebuild(); }) { text = "Revert" });
            root.Add(toolbar);
            var body = new ScrollView { style = { flexGrow = 1, paddingLeft = 4, paddingRight = 4 } };
            root.Add(body);

            var itemList = items.Items.Where(item => item).ToList();
            if (itemList.Count > 0)
            {
                var itemField = new PopupField<ItemDefinition>("Item", itemList, Mathf.Max(0, itemList.FindIndex(item => item.ItemId == itemId)),
                    item => item.ItemName, item => item.ItemName);
                itemField.RegisterValueChangedCallback(evt => { itemId = evt.newValue.ItemId; Scene?.SelectItem(evt.newValue.ItemId); Rebuild(); });
                body.Add(itemField);
            }
            var avatarList = avatars.Entries.Where(entry => entry != null && entry.Settings).ToList();
            if (avatarList.Count > 0)
            {
                var avatarField = new PopupField<AvatarRegistry.Entry>("Avatar", avatarList, Mathf.Max(0, avatarList.FindIndex(entry => entry.Id == avatarId)),
                    entry => entry.Settings.DisplayName, entry => entry.Settings.DisplayName);
                avatarField.RegisterValueChangedCallback(evt => { avatarId = evt.newValue.Id; Scene?.SelectAvatar(avatarId); Rebuild(); });
                body.Add(avatarField);
            }
            var view = new RadioButtonGroup("View", new List<string> { "Third person", "First person" }) { value = firstPerson ? 1 : 0 };
            view.RegisterValueChangedCallback(evt => { firstPerson = evt.newValue == 1; Rebuild(); });
            body.Add(view);
            phaseGroup = new RadioButtonGroup("Phase", PhaseNames.ToList()) { value = (int)phase };
            phaseGroup.RegisterValueChangedCallback(evt => SetPhase((GripAuthoringPhase)evt.newValue));
            body.Add(phaseGroup);
            var edit = new RadioButtonGroup("Edit", new List<string> { "Hand pose", "Item offset", "Avatar offset", "None" }) { value = (int)layer };
            edit.RegisterValueChangedCallback(evt =>
            {
                layer = (EditLayer)evt.newValue;
                if (layer == EditLayer.HandPose && phase == GripAuthoringPhase.Live) SetPhase(GripAuthoringPhase.Hold);
                HideTools(Scene && layer != EditLayer.None);
                Rebuild(); SceneView.RepaintAll();
            });
            body.Add(edit);

            var panel = new Box { style = { marginTop = 6, marginBottom = 6, paddingLeft = 4, paddingRight = 4, paddingTop = 4, paddingBottom = 4 } };
            body.Add(panel);
            var definition = Definition;
            if (!definition) panel.Add(new Label("Select an item."));
            else if (layer == EditLayer.HandPose) HandPosePanel(panel, definition);
            else if (layer == EditLayer.ItemOffset) ItemOffsetPanel(panel, definition);
            else if (layer == EditLayer.AvatarOffset) AvatarOffsetPanel(panel, definition);

            actions = new VisualElement { style = { flexDirection = FlexDirection.Row, flexWrap = Wrap.Wrap } };
            void Action(string text, Action<GripAuthoringScene> action) => actions.Add(new Button(() => { if (Scene) action(Scene); }) { text = text });
            Action("Equip", scene => scene.EquipAction()); Action("Dequip", scene => scene.Dequip());
            Action("Hold", scene => scene.Hold()); Action("Release", scene => scene.Release());
            Action("Throw", scene => scene.Throw()); Action("Cancel", scene => scene.CancelAction());
            Action("Drop", scene => scene.Drop()); Action("Use", scene => scene.Use());
            var reequip = new Toggle("Auto re-equip") { value = !scene || scene.AutoReequip };
            reequip.RegisterValueChangedCallback(evt => { if (Scene) Scene.AutoReequip = evt.newValue; });
            actions.Add(reequip);
            walk = new Toggle("Walk (F2)") { value = scene && scene.Walking };
            walk.RegisterValueChangedCallback(evt => Scene?.SetWalking(evt.newValue));
            actions.Add(walk);
            actions.SetEnabled(scene);
            body.Add(new Label("Actions"));
            body.Add(actions);

            readouts = new Label { style = { marginTop = 6, whiteSpace = WhiteSpace.Normal } };
            body.Add(readouts);
            unsaved = new Label { style = { marginTop = 6, whiteSpace = WhiteSpace.Normal } };
            body.Add(unsaved);
            AssetsChanged();
            Refresh();
        }

        private void SetPhase(GripAuthoringPhase value)
        {
            phase = value;
            phaseGroup?.SetValueWithoutNotify((int)value);
            Scene?.SetPhase(value);
            SceneView.RepaintAll();
        }

        private void HandPosePanel(VisualElement panel, ItemDefinition item)
        {
            var owner = item.HoldClass;
            var classField = new ObjectField("Class") { objectType = typeof(HoldClass), value = owner };
            classField.RegisterValueChangedCallback(_ => classField.SetValueWithoutNotify(owner));
            panel.Add(classField);
            if (!owner) { panel.Add(new Label("The item has no hold class.")); return; }
            panel.Add(new Label($"Mode: {owner.Mode}"));
            var clips = new Label();
            panel.Add(clips);
            refreshers.Add(() =>
            {
                var slot = owner.View(firstPerson);
                clips.text = $"{(firstPerson ? "First" : "Third")} person · Hold: {Name(slot.Hold)} · Charged: {Name(slot.Charged)}" +
                    (owner.Mode == ItemHoldMode.TwoHand ? $"\nSpread: {slot.HoldSpread:F3} m / {slot.ChargedSpread:F3} m" : "");
            });
            Swivel(panel, true);
            if (owner.Mode != ItemHoldMode.OneHand) Swivel(panel, false);
            var buttons = new VisualElement { style = { flexDirection = FlexDirection.Row } };
            buttons.Add(new Button(() => GripAuthoringAssets.CopyHoldToCharged(owner, firstPerson)) { text = "Copy Hold → Charged" });
            buttons.Add(new Button(() => GripAuthoringAssets.Clear(owner, firstPerson, phase == GripAuthoringPhase.Charged)) { text = "Clear pose" });
            panel.Add(buttons);
            GripAuthoringAssets.Snapshot(owner);
            var inspector = new InspectorElement(owner);
            inspector.RegisterCallback<SerializedPropertyChangeEvent>(_ => GripAuthoringAssets.TouchIfModified(owner));
            panel.Add(inspector);
        }

        private static string Name(AnimationClip clip) => clip ? clip.name : "not authored";

        private void Swivel(VisualElement panel, bool right)
        {
            var slider = new Slider(right ? "Right elbow swivel °" : "Left elbow swivel °", -180f, 180f);
            slider.RegisterValueChangedCallback(evt => Scene?.SetSwivel(firstPerson, right, evt.newValue));
            slider.RegisterCallback<PointerUpEvent>(_ => { BakeEdit(); slider.SetValueWithoutNotify(0f); }, TrickleDown.TrickleDown);
            panel.Add(slider);
        }

        private void ItemOffsetPanel(VisualElement panel, ItemDefinition item)
        {
            panel.Add(new Label($"Item offset · {(firstPerson ? "first" : "third")} person · item root in the grip frame, metres / degrees"));
            OffsetFields(panel, () => firstPerson ? item.FirstPersonGrip : item.ThirdPersonGrip, value => EditItem(item, "Edit item offset", () =>
            {
                if (firstPerson) item.FirstPersonGrip = value; else item.ThirdPersonGrip = value;
            }));
            panel.Add(new Button(() => EditItem(item, "Copy TP → FP", () => item.FirstPersonGrip = item.ThirdPersonGrip)) { text = "Copy TP → FP" });
            if (item is not SlingshotDefinition slingshot) return;
            var pouch = new Vector3Field("Pouch offset") { value = slingshot.PouchOffset };
            pouch.RegisterValueChangedCallback(evt => EditItem(item, "Edit pouch offset", () => slingshot.PouchOffset = evt.newValue));
            refreshers.Add(() => { if (pouch.value != slingshot.PouchOffset) pouch.SetValueWithoutNotify(slingshot.PouchOffset); });
            panel.Add(pouch);
        }

        private void AvatarOffsetPanel(VisualElement panel, ItemDefinition item)
        {
            string name = avatars.TryResolve(avatarId, out var entry) ? entry.Settings.DisplayName : avatarId.ToString();
            panel.Add(new Label($"Avatar offset · {name} · {(firstPerson ? "first" : "third")} person · in the grip frame, metres / degrees"));
            OffsetFields(panel, () => AvatarGrip(item), value => SetAvatarGrip(item, value, "Edit avatar offset"));
            panel.Add(new Button(() => EditItem(item, "Clear avatar offset", () =>
            {
                int index = item.AvatarGrips.FindIndex(grip => grip.Avatar == avatarId);
                if (index < 0) return;
                var grip = item.AvatarGrips[index];
                if (firstPerson) grip.FirstPerson = default; else grip.ThirdPerson = default;
                if (grip.FirstPerson.IsZero && grip.ThirdPerson.IsZero) item.AvatarGrips.RemoveAt(index);
                else item.AvatarGrips[index] = grip;
            })) { text = "Clear" });
            var list = new Label();
            panel.Add(list);
            refreshers.Add(() => list.text = "Avatars with offsets: " + (item.AvatarGrips.Count == 0 ? "none" : string.Join(", ",
                item.AvatarGrips.Select(grip => avatars.TryResolve(grip.Avatar, out var owner) ? owner.Settings.DisplayName : grip.Avatar.ToString()))));
        }

        private void SetAvatarGrip(ItemDefinition item, GripOffset value, string undo) => EditItem(item, undo, () =>
        {
            int index = item.AvatarGrips.FindIndex(grip => grip.Avatar == avatarId);
            var grip = index >= 0 ? item.AvatarGrips[index] : new AvatarGripOffset { Avatar = avatarId };
            if (firstPerson) grip.FirstPerson = value; else grip.ThirdPerson = value;
            if (index >= 0) item.AvatarGrips[index] = grip; else item.AvatarGrips.Add(grip);
        });

        private GripOffset AvatarGrip(ItemDefinition item)
        {
            int index = item.AvatarGrips.FindIndex(grip => grip.Avatar == avatarId);
            if (index < 0) return default;
            return firstPerson ? item.AvatarGrips[index].FirstPerson : item.AvatarGrips[index].ThirdPerson;
        }

        private void OffsetFields(VisualElement panel, Func<GripOffset> read, Action<GripOffset> write)
        {
            var position = new Vector3Field("Position");
            var euler = new Vector3Field("Euler");
            position.RegisterValueChangedCallback(evt => { var value = read(); value.Position = evt.newValue; write(value); });
            euler.RegisterValueChangedCallback(evt => { var value = read(); value.Euler = evt.newValue; write(value); });
            refreshers.Add(() =>
            {
                var value = read();
                if (position.value != value.Position) position.SetValueWithoutNotify(value.Position);
                if (euler.value != value.Euler) euler.SetValueWithoutNotify(value.Euler);
            });
            panel.Add(position); panel.Add(euler);
        }

        private static void EditItem(ItemDefinition item, string undo, Action change)
        {
            GripAuthoringAssets.Touch(item);
            Undo.RecordObject(item, undo);
            change();
            EditorUtility.SetDirty(item);
            item.NotifyContentChanged();
            Scene?.ContentEdited();
        }

        private void Update()
        {
            var scene = Scene;
            if (scene && lookThrough && firstPerson) LookThrough(scene);
            if (rootVisualElement.panel != null) Refresh();
        }

        private void Refresh()
        {
            foreach (var refresh in refreshers) refresh();
            if (readouts == null) return;
            var scene = Scene;
            actions?.SetEnabled(scene);
            if (!scene) { readouts.text = "Start Authoring to enter Play Mode in the authoring scene."; return; }
            if (scene.Phase != phase) { phase = scene.Phase; phaseGroup?.SetValueWithoutNotify((int)phase); }
            walk?.SetValueWithoutNotify(scene.Walking);
            string strip = scene.StripReadouts();
            readouts.text = $"Action: {scene.ActionPhase}{(string.IsNullOrEmpty(scene.Message) ? "" : " · " + scene.Message)}\n\n" +
                $"First person\n{scene.Readout(true)}\n\nThird person\n{scene.Readout(false)}" + (strip.Length > 0 ? "\n\nStrip\n" + strip : "");
        }

        private static void LookThrough(GripAuthoringScene scene)
        {
            var camera = scene.ViewCamera;
            var view = SceneView.lastActiveSceneView;
            if (!camera || !view) return;
            var pose = camera.transform;
            var settings = view.cameraSettings;
            settings.fieldOfView = camera.fieldOfView; settings.dynamicClip = false; settings.nearClip = camera.nearClipPlane;
            view.cameraSettings = settings;
            view.orthographic = false;
            view.LookAtDirect(pose.position, pose.rotation, 1f);
            view.LookAtDirect(pose.position + pose.forward * view.cameraDistance, pose.rotation, 1f);
            view.Repaint();
        }

        private void StartAuthoring()
        {
            if (EditorApplication.isPlaying)
            { if (SessionController.Instance) SessionController.Instance.StartGripAuthoring(); return; }
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            EditorSceneManager.OpenScene(GripAuthoringScene.ScenePath);
            EditorApplication.isPlaying = true;
        }

        private void Frame()
        {
            var scene = Scene;
            if (!scene) return;
            Pose pose;
            bool found = layer == EditLayer.HandPose ? scene.TryPalm(firstPerson, true, out pose) : scene.TryItem(firstPerson, out pose);
            if (found) SceneView.lastActiveSceneView?.Frame(new Bounds(pose.position, Vector3.one * 0.4f), false);
        }

        private void SceneGUI(SceneView view)
        {
            var scene = Scene;
            var item = scene ? scene.SelectedDefinition : null;
            if (item && layer == EditLayer.HandPose && scene.Phase != GripAuthoringPhase.Live)
            {
                PalmGizmo(scene, true);
                if (item.HoldMode != ItemHoldMode.OneHand) PalmGizmo(scene, false);
            }
            else if (item && layer is EditLayer.ItemOffset or EditLayer.AvatarOffset) OffsetGizmos(scene, item);
            if (Event.current.rawType == EventType.MouseUp && dragging) { dragging = false; BakeEdit(); }
        }

        private void PalmGizmo(GripAuthoringScene scene, bool right)
        {
            if (!scene.TryRequestedPalm(firstPerson, right, out var requested, out var reached)) return;
            bool active = dragging && draggingRight == right && scene.Dragging;
            var pose = active ? requested : reached;
            Handles.color = Color.white;
            Handles.Label(pose.position, right ? "Right palm" : "Left palm");
            if (Gizmo(ref pose))
            {
                if (!dragging) { dragging = scene.BeginPalmDrag(firstPerson, right); draggingRight = right; }
                if (dragging) scene.DragPalm(pose);
            }
            if (!active) return;
            Handles.color = Color.yellow; Handles.SphereHandleCap(0, requested.position, requested.rotation, 0.018f, EventType.Repaint);
            Handles.color = Color.cyan; Handles.SphereHandleCap(0, reached.position, reached.rotation, 0.012f, EventType.Repaint);
            Handles.DrawLine(requested.position, reached.position);
        }

        private void OffsetGizmos(GripAuthoringScene scene, ItemDefinition item)
        {
            bool avatarLayer = layer == EditLayer.AvatarOffset;
            if (!scene.TryGrip(firstPerson, out var frame) || !scene.TryItem(firstPerson, out var pose)) return;
            Handles.color = Color.white;
            Handles.Label(pose.position, avatarLayer ? "Avatar offset" : "Item offset");
            if (Gizmo(ref pose))
            {
                var local = HeldItemPoseCalculation.Compose(HeldItemPoseCalculation.Inverse(frame), pose);
                if (avatarLayer)
                {
                    var offset = (firstPerson ? item.FirstPersonGrip : item.ThirdPersonGrip).Pose;
                    SetAvatarGrip(item, Offset(HeldItemPoseCalculation.Compose(local, HeldItemPoseCalculation.Inverse(offset))), "Move avatar offset");
                }
                else
                {
                    var value = Offset(HeldItemPoseCalculation.Compose(HeldItemPoseCalculation.Inverse(AvatarGrip(item).Pose), local));
                    EditItem(item, "Move item offset", () => { if (firstPerson) item.FirstPersonGrip = value; else item.ThirdPersonGrip = value; });
                }
            }
            if (avatarLayer || item is not SlingshotDefinition slingshot || !scene.TryPouch(firstPerson, out var pouch) ||
                !scene.TryPalm(firstPerson, false, out var palm)) return;
            Handles.Label(pouch, "Pouch");
            EditorGUI.BeginChangeCheck();
            var moved = Handles.PositionHandle(pouch, Tools.pivotRotation == PivotRotation.Local ? palm.rotation : Quaternion.identity);
            if (EditorGUI.EndChangeCheck())
                EditItem(item, "Move pouch", () => slingshot.PouchOffset = Quaternion.Inverse(palm.rotation) * (moved - palm.position));
        }

        private static bool Gizmo(ref Pose pose)
        {
            bool local = Tools.pivotRotation == PivotRotation.Local;
            int hot = GUIUtility.hotControl;
            var before = pose;
            EditorGUI.BeginChangeCheck();
            switch (Tools.current)
            {
                case Tool.Rotate:
                {
                    var frame = Handles.RotationHandle(local ? pose.rotation : Quaternion.identity, pose.position);
                    pose.rotation = local ? frame : frame * gizmoStart.rotation;
                    break;
                }
                case Tool.Transform:
                {
                    var frame = local ? pose.rotation : Quaternion.identity;
                    Handles.TransformHandle(ref pose.position, ref frame);
                    pose.rotation = local ? frame : frame * gizmoStart.rotation;
                    break;
                }
                default:
                    pose.position = Handles.PositionHandle(pose.position, local ? pose.rotation : Quaternion.identity);
                    break;
            }
            if (hot == 0 && GUIUtility.hotControl != 0) gizmoStart = before;
            return EditorGUI.EndChangeCheck();
        }

        private static void BakeEdit()
        {
            var scene = Scene;
            if (scene && scene.EndPalmDrag(out var capture)) GripAuthoringAssets.Bake(capture);
        }

        private static GripOffset Offset(Pose pose) => new() { Position = pose.position, Euler = pose.rotation.eulerAngles };
    }
}
#endif
