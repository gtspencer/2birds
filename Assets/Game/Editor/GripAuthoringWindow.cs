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
        private const string ItemRegistryPath = "Assets/Game/ScriptableObjects/ItemRegistry.asset";
        private const string AvatarRegistryPath = "Assets/Game/Settings/Avatars/AvatarRegistry.asset";
        private static readonly string[] PhaseNames = { "Live", "Hold", "Charged" };
        private static readonly (bool right, GripTarget target)[] ContactRows =
        {
            (false, GripTarget.ContactPalm), (false, GripTarget.ContactElbow), (true, GripTarget.ContactPalm), (true, GripTarget.ContactElbow)
        };
        [SerializeField] private int itemId;
        [SerializeField] private AvatarId avatarId;
        [SerializeField] private bool firstPerson, lookThrough, ownerHidden;
        [SerializeField] private int savedLayers;
        [SerializeField] private GripAuthoringPhase phase;
        [SerializeField] private GripAuthoringMode mode;
        [SerializeField] private bool mirror, copyOpen;
        [SerializeField] private List<int> copyItems = new();
        private ItemRegistry items;
        private AvatarRegistry avatars;
        private readonly List<Action> refreshers = new();
        private readonly List<Transform> listed = new();
        private Label readouts, unsaved;
        private ToolbarButton save;
        private VisualElement actions;
        private Toggle walk;
        private SceneView lookView;
        private float lookFieldOfView, lookNearClip;
        private bool lookDynamicClip, lookOrthographic;

        [MenuItem("Two Birds/Grip Authoring")]
        public static void Open() => GetWindow<GripAuthoringWindow>("Grip Authoring");

        private static GripAuthoringScene Scene =>
            EditorApplication.isPlaying && GripAuthoringScene.Instance && GripAuthoringScene.Instance.Attached ? GripAuthoringScene.Instance : null;
        private ItemDefinition Definition => items ? items.Get((byte)itemId) : null;
        private bool Held => mode == GripAuthoringMode.HeldItem;

        private void OnEnable()
        {
            items = AssetDatabase.LoadAssetAtPath<ItemRegistry>(ItemRegistryPath);
            avatars = AssetDatabase.LoadAssetAtPath<AvatarRegistry>(AvatarRegistryPath);
            if (!Definition && items) itemId = items.Items.FirstOrDefault(item => item)?.ItemId ?? 0;
            if (!avatarId.IsValid && avatars) avatarId = avatars.DefaultId;
            GripAuthoringScene.SceneChanged += SceneChanged;
            GripAuthoringAssets.Changed += AssetsChanged;
            Undo.undoRedoPerformed += UndoRedo;
            Undo.postprocessModifications += Modified;
            EditorApplication.playModeStateChanged += PlayModeChanged;
            AssetsChanged();
        }

        private void OnDisable()
        {
            GripAuthoringScene.SceneChanged -= SceneChanged;
            GripAuthoringAssets.Changed -= AssetsChanged;
            Undo.undoRedoPerformed -= UndoRedo;
            Undo.postprocessModifications -= Modified;
            EditorApplication.playModeStateChanged -= PlayModeChanged;
            HideOwner(false); EndLookThrough();
        }

        public void CreateGUI() => Rebuild();

        private void SceneChanged()
        {
            var scene = Scene;
            if (scene)
            {
                scene.SelectAvatar(avatarId); scene.SelectItem((byte)itemId); scene.SetView(firstPerson);
                scene.SetPhase(phase); scene.SetMode(mode); scene.SetMirror(mirror);
            }
            HideOwner(scene);
            Rebuild();
        }

        private void PlayModeChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingPlayMode)
            {
                if (GripAuthoringAssets.Unsaved.Count > 0)
                {
                    if (EditorUtility.DisplayDialog("Grip Authoring", saveChangesMessage, "Save", "Revert")) GripAuthoringAssets.SaveAll();
                    else GripAuthoringAssets.RevertAll();
                }
                HideOwner(false); EndLookThrough();
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

        private void AssetsChanged()
        {
            var names = GripAuthoringAssets.Unsaved.Where(asset => asset).Select(asset => asset.name).ToArray();
            hasUnsavedChanges = names.Length > 0;
            saveChangesMessage = "Save grip authoring changes?\n" + string.Join("\n", names);
            if (save != null) save.text = $"Save ({names.Length})";
            if (unsaved != null) unsaved.text = names.Length > 0 ? "Unsaved: " + string.Join(", ", names) : "No unsaved changes";
        }

        // Undo can restore any item, slot or contact, including ones Save stopped tracking.
        private void UndoRedo()
        {
            if (!items) return;
            var definitions = items.Items.Where(item => item).ToList();
            foreach (var item in definitions) item.NotifyContentChanged();
            foreach (var slot in definitions.Select(item => item.HoldSlot).Where(slot => slot).Distinct()) slot.NotifyContentChanged();
            GripAuthoringAssets.SyncPairs();
            Scene?.Reapply();
        }

        // Runs before Inspector edits apply, so the snapshot holds the prior state.
        private static UndoPropertyModification[] Modified(UndoPropertyModification[] modifications)
        {
            if (Scene)
                foreach (var modification in modifications)
                {
                    var target = modification.currentValue?.target;
                    if (target is HoldSlot or ItemDefinition or AvatarSettings ||
                        target is AvatarHandContact && EditorUtility.IsPersistent(target))
                        GripAuthoringAssets.Touch(target);
                }
            return modifications;
        }

        public override void SaveChanges() { GripAuthoringAssets.SaveAll(); base.SaveChanges(); }
        public override void DiscardChanges() { GripAuthoringAssets.RevertAll(); base.DiscardChanges(); }

        private void Rebuild()
        {
            var root = rootVisualElement;
            root.Clear(); refreshers.Clear(); listed.Clear();
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
            toolbar.Add(new ToolbarButton(() => { GripAuthoringAssets.RevertAll(); Scene?.Reapply(); Rebuild(); }) { text = "Revert" });
            root.Add(toolbar);
            var body = new ScrollView { style = { flexGrow = 1, paddingLeft = 4, paddingRight = 4 } };
            root.Add(body);

            var modeGroup = new RadioButtonGroup("Mode", new List<string> { "Held item", "World contact" }) { value = (int)mode };
            modeGroup.RegisterValueChangedCallback(evt =>
            {
                if (!ConfirmDiscard()) { modeGroup.SetValueWithoutNotify(evt.previousValue); return; }
                mode = (GripAuthoringMode)evt.newValue;
                Scene?.SetMode(mode);
                Rebuild();
            });
            body.Add(modeGroup);
            var itemList = items.Items.Where(item => item).ToList();
            if (itemList.Count > 0)
                body.Add(Stepper("Item", itemList.Count, Mathf.Max(0, itemList.FindIndex(item => item.ItemId == itemId)),
                    index => itemList[index].ItemName, index => { itemId = itemList[index].ItemId; Scene?.SelectItem((byte)itemId); }));
            var avatarList = avatars.Entries.Where(entry => entry != null && entry.Settings).ToList();
            if (avatarList.Count > 0)
                body.Add(Stepper("Avatar", avatarList.Count, Mathf.Max(0, avatarList.FindIndex(entry => entry.Id == avatarId)),
                    index => avatarList[index].Settings.DisplayName, index => { avatarId = avatarList[index].Id; Scene?.SelectAvatar(avatarId); }));
            var view = new RadioButtonGroup("View", new List<string> { "Third person", "First person" }) { value = firstPerson ? 1 : 0 };
            view.RegisterValueChangedCallback(evt =>
            {
                if (!ConfirmDiscard()) { view.SetValueWithoutNotify(evt.previousValue); return; }
                firstPerson = evt.newValue == 1; Scene?.SetView(firstPerson); Rebuild();
            });
            body.Add(view);
            if (Held)
            {
                var phaseGroup = new RadioButtonGroup("Phase", PhaseNames.ToList()) { value = (int)phase };
                phaseGroup.RegisterValueChangedCallback(evt => SetPhase((GripAuthoringPhase)evt.newValue));
                body.Add(phaseGroup);
            }

            var panel = new Box { style = { marginTop = 6, marginBottom = 6, paddingLeft = 4, paddingRight = 4, paddingTop = 4, paddingBottom = 4 } };
            body.Add(panel);
            if (!scene) panel.Add(new Label("Start Authoring to edit targets."));
            else if (Held) HeldPanel(panel, scene);
            else ContactPanel(panel, scene);

            actions = new VisualElement { style = { flexDirection = FlexDirection.Row, flexWrap = Wrap.Wrap } };
            Button Action(string text, string tooltip, Action<GripAuthoringScene> action)
            {
                var button = new Button(() => { if (Scene) action(Scene); }) { text = text, tooltip = tooltip };
                actions.Add(button);
                return button;
            }
            if (Held)
            {
                Action("Equip", "Supply and select the item.", s => s.EquipAction());
                Action("Dequip", "Select no hotbar slot.", s => s.Dequip());
                Action("Hold", "Begin a charge and keep holding.", s => s.Hold());
                Action("Release", "End the charge (throw or fire).", s => s.Release());
                Action("Throw", "Charge for the full charge time, then release.", s => s.Throw());
                Action("Cancel", "Cancel the current charge.", s => s.CancelAction());
                Action("Drop", "Drop the selected item.", s => s.Drop());
                Action("Drink", "Drink the selected potion. Only potions have a direct use.", s => s.Use())
                    .SetEnabled(Definition is PotionDefinition);
                var reequip = new Toggle("Auto re-equip")
                    { value = !scene || scene.AutoReequip, tooltip = "Re-supply the item 1 s after it leaves the inventory." };
                reequip.RegisterValueChangedCallback(evt => { if (Scene) Scene.AutoReequip = evt.newValue; });
                actions.Add(reequip);
            }
            else
            {
                Action("Enter seat", "Sit in the golf cart's driver seat.", s => s.EnterSeat());
                Action("Exit seat", "Leave the golf cart.", s => s.ExitSeat());
            }
            walk = new Toggle("Walk (F2)")
                { value = scene && scene.Walking, tooltip = "Walk with WASD in the Game view. F2 toggles while the Game view has focus." };
            walk.RegisterValueChangedCallback(evt =>
            {
                Scene?.SetWalking(evt.newValue);
                if (evt.newValue) EditorApplication.ExecuteMenuItem("Window/General/Game");
            });
            actions.Add(walk);
            actions.SetEnabled(scene);
            body.Add(new Label("Actions") { tooltip = "Runs real gameplay on your player to preview grips in motion. Switches the phase to Live." });
            body.Add(actions);

            readouts = new Label { style = { marginTop = 6, whiteSpace = WhiteSpace.Normal } };
            body.Add(readouts);
            unsaved = new Label { style = { marginTop = 6, whiteSpace = WhiteSpace.Normal } };
            body.Add(unsaved);
            AssetsChanged();
            Refresh();
        }

        private VisualElement Stepper(string label, int count, int index, Func<int, string> name, Action<int> select)
        {
            var row = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center } };
            row.Add(new Label(label) { style = { width = 60 } });
            void Step(int delta) { if (!ConfirmDiscard()) return; select((index + delta + count) % count); Rebuild(); }
            row.Add(new Button(() => Step(-1)) { text = "◀" });
            row.Add(new Label(name(index)) { style = { flexGrow = 1, unityTextAlign = TextAnchor.MiddleCenter } });
            row.Add(new Button(() => Step(1)) { text = "▶" });
            return row;
        }

        // Item, avatar, view and mode changes re-apply the edited rig, which drops unsaved drags.
        private bool ConfirmDiscard()
        {
            var scene = Scene;
            if (!scene) return true;
            bool dirty = Held
                ? Enum.GetValues(typeof(GripTarget)).Cast<GripTarget>().Any(target => scene.TryTarget(target, out _, out _, out _, out var changed) && changed)
                : ContactRows.Any(row => Contact(row.right) && Contact(row.right).TryAuthored(row.target, out _, out _, out _, out var changed) && changed);
            return !dirty || EditorUtility.DisplayDialog("Grip Authoring", "Discard unsaved target edits?", "Discard", "Cancel");
        }

        private void SetPhase(GripAuthoringPhase value)
        {
            phase = value;
            Scene?.SetPhase(value);
            Rebuild();
            SceneView.RepaintAll();
        }

        private void HeldPanel(VisualElement panel, GripAuthoringScene scene)
        {
            var item = Definition;
            if (!item) { panel.Add(new Label("Select an item.")); return; }
            if (!item.HoldSlot) { panel.Add(new Label("The item has no Hold Slot.")); return; }
            panel.Add(new Label($"{item.HoldSlot.name} · {item.HoldMode} · {(firstPerson ? "first" : "third")} person"));
            foreach (var target in scene.PhaseTargets().ToArray()) HeldRow(panel, item, target);
            if (phase == GripAuthoringPhase.Live)
            {
                panel.Add(new Label("Preview only — switch to Hold or Charged to edit.") { style = { marginTop = 4 } });
                return;
            }
            if (item.HoldMode == HoldSlotMode.Heavy)
            {
                var mirrorToggle = new Toggle("Mirror left/right")
                {
                    value = mirror, style = { marginTop = 4 },
                    tooltip = "Dragging a hand or elbow moves its partner as a reflection: hands across the item's local X, elbows across the body midline."
                };
                mirrorToggle.RegisterValueChangedCallback(evt => { mirror = evt.newValue; Scene?.SetMirror(mirror); });
                panel.Add(mirrorToggle);
            }
            const string saves = " Saves changed (●) targets of this phase for the current view.";
            var buttons = new VisualElement { style = { flexDirection = FlexDirection.Row, flexWrap = Wrap.Wrap, marginTop = 4 } };
            buttons.Add(new Button(() => SaveHeld(GripLayer.Avatar))
                { text = "Save for avatar", tooltip = "Override for this avatar and this item only. Highest priority." + saves });
            buttons.Add(new Button(() => SaveHeld(GripLayer.Item))
                { text = "Save as item default", tooltip = "This item, all avatars, unless an avatar has its own override." + saves });
            buttons.Add(new Button(() => SaveHeld(GripLayer.Slot))
            {
                text = "Save as slot default",
                tooltip = $"Fallback for every item using {item.HoldSlot.name} when neither the item nor the avatar defines it." + saves
            });
            panel.Add(buttons);
            CopyPanel(panel, item);
        }

        private void CopyPanel(VisualElement panel, ItemDefinition item)
        {
            var candidates = items.Items.Where(other => other && other != item && other.HoldSlot == item.HoldSlot).ToList();
            if (candidates.Count == 0) return;
            var foldout = new Foldout { value = copyOpen, style = { marginTop = 4 } };
            foldout.RegisterValueChangedCallback(evt => { if (evt.target == foldout) copyOpen = evt.newValue; });
            var toggles = new List<Toggle>();
            var copy = new VisualElement { style = { flexDirection = FlexDirection.Row, flexWrap = Wrap.Wrap, marginTop = 4 } };
            void Changed()
            {
                int count = candidates.Count(other => copyItems.Contains(other.ItemId));
                foldout.text = $"Copy to items ({count} selected)";
                copy.SetEnabled(count > 0);
            }
            var bulk = new VisualElement { style = { flexDirection = FlexDirection.Row } };
            bulk.Add(new Button(() => { foreach (var toggle in toggles) toggle.value = true; }) { text = "All" });
            bulk.Add(new Button(() => { foreach (var toggle in toggles) toggle.value = false; }) { text = "None" });
            foldout.Add(bulk);
            foreach (var other in candidates)
            {
                int id = other.ItemId;
                var toggle = new Toggle { text = other.ItemName, value = copyItems.Contains(id) };
                toggle.RegisterValueChangedCallback(evt =>
                {
                    copyItems.Remove(id);
                    if (evt.newValue) copyItems.Add(id);
                    Changed();
                });
                toggles.Add(toggle);
                foldout.Add(toggle);
            }
            const string copies = " Copies every saved or changed target of this item for the current view, as shown. This item is not saved.";
            copy.Add(new Button(() => CopyToItems(GripLayer.Avatar, candidates))
                { text = "Copy for avatar", tooltip = "Write the selected items' overrides for this avatar." + copies });
            copy.Add(new Button(() => CopyToItems(GripLayer.Item, candidates))
                { text = "Copy as item default", tooltip = "Write the selected items' defaults for all avatars." + copies });
            foldout.Add(copy);
            Changed();
            panel.Add(foldout);
        }

        private void CopyToItems(GripLayer layer, List<ItemDefinition> candidates)
        {
            var scene = Scene;
            var item = Definition;
            var selected = candidates.Where(other => other && copyItems.Contains(other.ItemId)).ToList();
            if (!scene || !item || selected.Count == 0) return;
            var poses = new List<(GripTarget target, Pose pose)>();
            foreach (GripTarget target in Enum.GetValues(typeof(GripTarget)))
                if (GripPoses.Uses(item.HoldMode, target) && scene.TryTarget(target, out _, out var stored, out var source, out var changed) &&
                    (changed || source != GripLayer.None))
                    poses.Add((target, stored));
            if (poses.Count == 0) return;
            if (layer == GripLayer.Item)
            {
                var shadowed = selected.Where(other => GripPoses.Find(other.AvatarGripPoses, avatarId) is { } table &&
                    poses.Any(entry => table.TryGet(entry.target, firstPerson, out _))).Select(other => other.ItemName).ToArray();
                if (shadowed.Length > 0 && !EditorUtility.DisplayDialog("Grip Authoring",
                    $"{string.Join(", ", shadowed)} have overrides for this avatar that will keep winning over the item default. Copy anyway?",
                    "Copy", "Cancel")) return;
            }
            foreach (var other in selected)
                GripAuthoringAssets.Edit(other, "Copy grip to items", () =>
                {
                    var table = layer == GripLayer.Avatar ? GripPoses.Ensure(other.AvatarGripPoses, avatarId) : other.GripPoses;
                    foreach (var (target, pose) in poses) table.Set(target, firstPerson, pose);
                });
        }

        private void HeldRow(VisualElement panel, ItemDefinition item, GripTarget target)
        {
            var row = Row(panel, target.ToString(), out var source, out var dirty);
            var select = new Button(() => { if (Scene && Scene.TryTarget(target, out var transform, out _, out _, out _)) Selection.activeTransform = transform; }) { text = "Select" };
            var reset = new Button(() => Scene?.ResetTarget(target)) { text = "Reset", tooltip = "Discard the unsaved drag on this target." };
            var clear = new Button(() => ClearHeld(item, target)) { text = "Clear" };
            row.Add(select); row.Add(reset); row.Add(clear);
            Button copy = null;
            if (target is GripTarget.RightHand or GripTarget.LeftHand or GripTarget.PouchDraw)
                row.Add(copy = new Button(() => CopyHeld(item, target)) { text = "Copy to other view" });
            refreshers.Add(() =>
            {
                var scene = Scene;
                Transform transform = null;
                var layer = GripLayer.None;
                bool changed = false;
                bool found = scene && scene.TryTarget(target, out transform, out _, out layer, out changed);
                if (found && !listed.Contains(transform)) listed.Add(transform);
                source.text = layer.ToString(); dirty.text = changed ? "●" : "";
                bool slotLocked = layer == GripLayer.Slot && !GripPoses.IsHint(target);
                clear.SetEnabled(found && layer != GripLayer.None && !slotLocked);
                clear.tooltip = ClearTooltip(layer, slotLocked);
                if (copy != null)
                {
                    copy.SetEnabled(found && layer != GripLayer.None && !changed);
                    copy.tooltip = layer == GripLayer.None ? "Save before copying." : changed ? "Save or reset before copying." :
                        $"Copy the {layer} entry to the {(firstPerson ? "third" : "first")} person view.";
                }
                reset.SetEnabled(found && changed);
                select.SetEnabled(found);
            });
        }

        private static string ClearTooltip(GripLayer layer, bool slotLocked) =>
            layer == GripLayer.None ? "Nothing saved to clear." :
            slotLocked ? "Slot defaults for required targets can't be cleared." : $"Remove the {layer} entry for this view.";

        private static VisualElement Row(VisualElement panel, string name, out Label source, out Label dirty)
        {
            var row = new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center } };
            row.Add(new Label(name) { style = { width = 130 } });
            row.Add(source = new Label { style = { width = 60 } });
            row.Add(dirty = new Label { style = { width = 16 } });
            panel.Add(row);
            return row;
        }

        private (UnityEngine.Object owner, GripPoseTable table) Layer(ItemDefinition item, GripLayer layer) => layer switch
        {
            GripLayer.Avatar => (item, GripPoses.Find(item.AvatarGripPoses, avatarId)),
            GripLayer.Item => (item, item.GripPoses),
            GripLayer.Slot => (item.HoldSlot, item.HoldSlot ? item.HoldSlot.Defaults : null),
            _ => ((UnityEngine.Object)null, (GripPoseTable)null)
        };

        private void SaveHeld(GripLayer layer)
        {
            var scene = Scene;
            var item = Definition;
            if (!scene || !item || layer == GripLayer.Slot && !item.HoldSlot) return;
            var shadowed = scene.PhaseTargets().Where(target =>
                scene.TryTarget(target, out _, out _, out var source, out var changed) && changed && source > layer).ToArray();
            if (shadowed.Length > 0)
            {
                EditorUtility.DisplayDialog("Grip Authoring", $"{string.Join(", ", shadowed)} resolve from a higher layer, which would " +
                    $"keep overriding the {layer} layer. Save to that layer, or clear it first.", "OK");
                return;
            }
            foreach (var target in scene.PhaseTargets().ToArray())
            {
                if (!scene.TryTarget(target, out _, out var stored, out _, out var changed) || !changed) continue;
                UnityEngine.Object owner = layer == GripLayer.Slot ? item.HoldSlot : (UnityEngine.Object)item;
                GripAuthoringAssets.Edit(owner, "Save grip", () =>
                {
                    var table = layer switch
                    {
                        GripLayer.Avatar => GripPoses.Ensure(item.AvatarGripPoses, avatarId),
                        GripLayer.Item => item.GripPoses,
                        _ => item.HoldSlot.Defaults
                    };
                    table.Set(target, firstPerson, stored);
                });
            }
            scene.Reapply();
        }

        private void ClearHeld(ItemDefinition item, GripTarget target)
        {
            var scene = Scene;
            if (!scene || !scene.TryTarget(target, out _, out _, out var layer, out _)) return;
            var (owner, table) = Layer(item, layer);
            if (!owner || table == null) return;
            GripAuthoringAssets.Edit(owner, "Clear grip", () => table.Remove(target, firstPerson));
            scene.Reapply();
        }

        private void CopyHeld(ItemDefinition item, GripTarget target)
        {
            var scene = Scene;
            if (!scene || !scene.TryTarget(target, out _, out _, out var layer, out _)) return;
            var (owner, table) = Layer(item, layer);
            if (!owner || table == null || !table.TryGet(target, firstPerson, out var pose)) return;
            GripAuthoringAssets.Edit(owner, "Copy grip to other view", () => table.Set(target, !firstPerson, pose));
            scene.Reapply();
        }

        private void ContactPanel(VisualElement panel, GripAuthoringScene scene)
        {
            if (!scene.HasCart) { panel.Add(new Label("Add a GolfCart instance to the authoring scene.")); return; }
            panel.Add(new Label($"Steering wheel · {(firstPerson ? "first" : "third")} person"));
            foreach (var (right, target) in ContactRows) ContactRow(panel, right, target);
            var buttons = new VisualElement { style = { flexDirection = FlexDirection.Row, flexWrap = Wrap.Wrap, marginTop = 4 } };
            const string saves = " Saves changed (●) targets for the current view.";
            buttons.Add(new Button(() => SaveContacts(true)) { text = "Save for avatar", tooltip = "Contact prefab override for this avatar." + saves });
            buttons.Add(new Button(() => SaveContacts(false)) { text = "Save as contact default", tooltip = "Contact prefab default for all avatars." + saves });
            panel.Add(buttons);
        }

        private static AvatarHandContact Contact(bool right) =>
            Scene ? right ? Scene.RightContact : Scene.LeftContact : null;

        private void ContactRow(VisualElement panel, bool right, GripTarget target)
        {
            string name = $"{(right ? "Right" : "Left")} {(target == GripTarget.ContactPalm ? "palm" : "elbow")}";
            var row = Row(panel, name, out var source, out var dirty);
            var select = new Button(() => { if (Contact(right) && Contact(right).TryAuthored(target, out var transform, out _, out _, out _)) Selection.activeTransform = transform; }) { text = "Select" };
            var reset = new Button(() => { var contact = Contact(right); if (contact) contact.ResetAuthored(target); })
                { text = "Reset", tooltip = "Discard the unsaved drag on this target." };
            var clear = new Button(() => ClearContact(right, target)) { text = "Clear" };
            row.Add(select); row.Add(reset); row.Add(clear);
            refreshers.Add(() =>
            {
                var contact = Contact(right);
                Transform transform = null;
                var layer = GripLayer.None;
                bool changed = false;
                bool found = contact && contact.TryAuthored(target, out transform, out _, out layer, out changed);
                if (found && !listed.Contains(transform)) listed.Add(transform);
                source.text = layer.ToString(); dirty.text = changed ? "●" : "";
                clear.SetEnabled(found && layer != GripLayer.None);
                clear.tooltip = ClearTooltip(layer, false);
                reset.SetEnabled(found && changed);
                select.SetEnabled(found);
            });
        }

        private bool EditContact(AvatarHandContact live, string undo, Action<AvatarHandContact> change)
        {
            var asset = PrefabUtility.GetCorrespondingObjectFromOriginalSource(live);
            if (!asset) { Debug.LogError("Contact is not a prefab instance", live); return false; }
            GripAuthoringAssets.Edit(asset, undo, () => change(asset));
            GripAuthoringAssets.Pair(asset, live);
            live.CopyPoses(asset);
            return true;
        }

        private void SaveContacts(bool avatarLayer)
        {
            var scene = Scene;
            if (!scene) return;
            foreach (var (right, target) in ContactRows)
            {
                var live = Contact(right);
                if (!live || !live.TryAuthored(target, out _, out var stored, out _, out var changed) || !changed) continue;
                if (!EditContact(live, "Save contact", asset =>
                    (avatarLayer ? GripPoses.Ensure(asset.AvatarPoses, avatarId) : asset.Poses).Set(target, firstPerson, stored))) return;
            }
            scene.Reapply();
        }

        private void ClearContact(bool right, GripTarget target)
        {
            var live = Contact(right);
            if (!live || !live.TryAuthored(target, out _, out _, out var layer, out _) || layer == GripLayer.None) return;
            EditContact(live, "Clear contact", asset =>
                (layer == GripLayer.Avatar ? GripPoses.Find(asset.AvatarPoses, avatarId) : asset.Poses)?.Remove(target, firstPerson));
            Scene?.Reapply();
        }

        private void Update()
        {
            var scene = Scene;
            if (scene && lookThrough && firstPerson) LookThrough(scene);
            else EndLookThrough();
            if (rootVisualElement.panel != null) Refresh();
        }

        private void Refresh()
        {
            if (readouts == null) return;
            var scene = Scene;
            actions?.SetEnabled(scene);
            if (scene && Held && scene.Phase != phase) { phase = scene.Phase; Rebuild(); return; }
            foreach (var refresh in refreshers) refresh();
            if (!scene) { readouts.text = "Start Authoring to enter Play Mode in the authoring scene."; return; }
            walk?.SetValueWithoutNotify(scene.Walking);
            string message = string.IsNullOrEmpty(scene.Message) ? "" : " · " + scene.Message;
            if (!Held) { readouts.text = $"{(scene.Seated ? "Seated as driver" : "Not seated")}{message}"; return; }
            string strip = scene.StripReadouts();
            readouts.text = $"Action: {scene.ActionPhase}{message}\n\n" +
                $"First person\n{scene.Readout(true)}\n\nThird person\n{scene.Readout(false)}" + (strip.Length > 0 ? "\n\nStrip\n" + strip : "");
        }

        private void LookThrough(GripAuthoringScene scene)
        {
            var camera = scene.ViewCamera;
            var view = SceneView.lastActiveSceneView;
            if (!camera || !view) return;
            if (view != lookView)
            {
                EndLookThrough();
                lookView = view;
                var saved = view.cameraSettings;
                lookFieldOfView = saved.fieldOfView; lookDynamicClip = saved.dynamicClip; lookNearClip = saved.nearClip;
                lookOrthographic = view.orthographic;
            }
            var pose = camera.transform;
            var settings = view.cameraSettings;
            settings.fieldOfView = camera.fieldOfView; settings.dynamicClip = false; settings.nearClip = camera.nearClipPlane;
            view.cameraSettings = settings;
            view.orthographic = false;
            view.LookAtDirect(pose.position, pose.rotation, 1f);
            view.LookAtDirect(pose.position + pose.forward * view.cameraDistance, pose.rotation, 1f);
            view.Repaint();
        }

        private void EndLookThrough()
        {
            if (!lookView) return;
            var settings = lookView.cameraSettings;
            settings.fieldOfView = lookFieldOfView; settings.dynamicClip = lookDynamicClip; settings.nearClip = lookNearClip;
            lookView.cameraSettings = settings;
            lookView.orthographic = lookOrthographic;
            lookView.Repaint();
            lookView = null;
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
            Transform target = Selection.activeTransform && listed.Contains(Selection.activeTransform) ? Selection.activeTransform : null;
            if (!target)
            {
                if (Held) scene.TryTarget(GripTarget.RightHand, out target, out _, out _, out _);
                else target = scene.RightContact ? scene.RightContact.transform : null;
            }
            if (target) SceneView.lastActiveSceneView?.Frame(new Bounds(target.position, Vector3.one * 0.4f), false);
        }
    }
}
#endif
