#if UNITY_INCLUDE_INSTRUMENTATION
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.UIElements;

namespace TwoBirds
{
    public sealed class GripAuthoringPanel : IDisposable
    {
        private readonly VisualElement root, fields, previews;
        private readonly GripAuthoringDrafts drafts;
        private readonly GripAuthoringScene scene;
        private readonly Label dirty, message, condition, firstReadout, observerReadout;
        private readonly Image firstImage, observerImage;
        private readonly VisualElement firstPane, observerPane;
        private bool editing, rebuilding;
        private int expanded;
        private Vector2 orbitStart;
        private bool orbiting;
        public Action<GripAuthoringDraft> Save;
        public Action OpenFolder, BeforeEdit;
        public Func<string, AnimationClip, Action<AnimationClip>, VisualElement> ClipField;
        public Action<UnityEngine.Object> Inspect;
        public Action<string> SelectHandle;

        public GripAuthoringPanel(VisualElement root, GripAuthoringDrafts drafts, GripAuthoringScene scene = null)
        {
            this.root = root; this.drafts = drafts; this.scene = scene;
            fields = root.Q("authoring-fields"); previews = root.Q("comparison");
            dirty = root.Q<Label>("draft-status"); message = root.Q<Label>("message"); condition = root.Q<Label>("conditions");
            firstPane = root.Q("first-pane"); observerPane = root.Q("observer-pane");
            firstReadout = root.Q<Label>("first-readout"); observerReadout = root.Q<Label>("observer-readout");
            firstImage = new Image { scaleMode = ScaleMode.ScaleToFit }; observerImage = new Image { scaleMode = ScaleMode.ScaleToFit };
            firstImage.AddToClassList("preview-image"); observerImage.AddToClassList("preview-image");
            root.Q("first-image").Add(firstImage); root.Q("observer-image").Add(observerImage);
            root.Q<Button>("expand-first").clicked += () => Expand(1);
            root.Q<Button>("expand-observer").clicked += () => Expand(2);
            root.Q<Button>("save").clicked += () => Run(() => Save?.Invoke(drafts.Current));
            root.Q<Button>("revert").clicked += () => { BeforeEdit?.Invoke(); drafts.Revert(drafts.Current); Rebuild(); };
            root.Q<Button>("open-folder").clicked += () => OpenFolder?.Invoke();
            root.Q<Button>("item-context").clicked += () => Context(GripAuthoringContext.Item);
            root.Q<Button>("avatar-context").clicked += () => Context(GripAuthoringContext.AvatarCalibration);
            root.Q<Button>("shared-context").clicked += () => Context(GripAuthoringContext.SharedDefaults);
            drafts.Changed += Changed;
            if (scene)
            {
                scene.Views.Changed += CameraChanged;
                BuildActions(); BuildCameras(); CameraChanged();
                observerImage.RegisterCallback<PointerDownEvent>(evt => { orbiting = true; orbitStart = evt.position; observerImage.CapturePointer(evt.pointerId); scene.SetEditing(true); });
                observerImage.RegisterCallback<PointerMoveEvent>(evt =>
                { if (orbiting) { Vector2 point = evt.position; scene.Views.Orbit(point - orbitStart); orbitStart = point; } });
                observerImage.RegisterCallback<PointerUpEvent>(evt => { orbiting = false; observerImage.ReleasePointer(evt.pointerId); });
                observerImage.RegisterCallback<WheelEvent>(evt => { scene.Views.Zoom(evt.delta.y); evt.StopPropagation(); });
                firstImage.RegisterCallback<PointerDownEvent>(_ => scene.SetEditing(false));
                root.Q("controls").RegisterCallback<PointerDownEvent>(_ => scene.SetEditing(true), TrickleDown.TrickleDown);
                root.RegisterCallback<FocusInEvent>(_ => scene.SetEditing(true));
            }
            else { previews.style.display = DisplayStyle.None; root.Q("actions").Add(new Label("Start Authoring for live gameplay, previews, and spatial handles.")); }
            Rebuild();
        }
        private void Run(Action action)
        { try { action(); } catch (Exception exception) { ShowMessage(exception.Message); } }
        public void ShowMessage(string text) => message.text = text;
        private void Context(GripAuthoringContext context) { drafts.Context = context; Rebuild(); }
        private void Changed()
        {
            UpdateReadouts();
            if (!editing && !rebuilding) Rebuild();
        }
        public void Rebuild()
        {
            rebuilding = true;
            try
            {
                fields.Clear();
                var selectors = root.Q("selectors"); selectors.Clear();
                var items = drafts.Items.Items.Where(item => item).ToList();
                var avatars = drafts.Avatars.Entries.Where(entry => entry.Settings).ToList();
                var itemChoice = new DropdownField("Item", items.Select(item => $"{item.ItemId}: {item.ItemName}").ToList(),
                    Mathf.Max(0, items.FindIndex(item => item.ItemId == drafts.SelectedItem)));
                itemChoice.RegisterValueChangedCallback(_ =>
                {
                    if (scene) scene.SelectItem(items[itemChoice.index].ItemId);
                    else { drafts.SelectedItem = items[itemChoice.index].ItemId; drafts.SelectionChanged(); }
                }); selectors.Add(itemChoice);
                var avatarChoice = new DropdownField("Avatar", avatars.Select(entry => entry.Settings.DisplayName).ToList(),
                    Mathf.Max(0, avatars.FindIndex(entry => entry.Id == drafts.SelectedAvatar)));
                avatarChoice.RegisterValueChangedCallback(_ =>
                {
                    if (scene) scene.SelectAvatar(avatars[avatarChoice.index].Id);
                    else { drafts.SelectedAvatar = avatars[avatarChoice.index].Id; drafts.SelectionChanged(); }
                }); selectors.Add(avatarChoice);
                if (drafts.Context == GripAuthoringContext.SharedDefaults)
                {
                    var shared = drafts.Records.Where(record => record.Context == GripAuthoringContext.SharedDefaults).ToList();
                    var source = new DropdownField("Source", shared.Select(record => record.DisplayName).ToList(),
                        Mathf.Max(0, shared.FindIndex(record => record.Key == drafts.SharedKey)));
                    source.RegisterValueChangedCallback(_ => { drafts.SharedKey = shared[source.index].Key; Rebuild(); }); fields.Add(source);
                }
                var record = drafts.Current;
                if (record == null) return;
                if (record.Runtime is ItemDefinition item) ItemFields(record, item);
                else if (record.Runtime is AvatarSettings)
                {
                    Header(fields, "Palm calibration ? generated palm local axes / metres before avatar scale / Euler degrees");
                    Field(fields, record, "LeftPalmCorrection", true); Field(fields, record, "RightPalmCorrection", true);
                    Header(fields, "Avatar scale and placement ? height/standing/placement in metres; reach/hold in arm lengths; yaw in degrees");
                    foreach (string field in new[] { "VisualHeight", "StandingOffset", "YawOffset", "FirstPersonPlacementOffset", "FirstPersonReachOffset", "FirstPersonHoldOffset" })
                        Field(fields, record, field, true);
                }
                else ObjectFields(fields, record, record.Values, "", true);
                UpdateReadouts();
            }
            finally { rebuilding = false; }
        }
        private void ItemFields(GripAuthoringDraft record, ItemDefinition item)
        {
            Header(fields, "Palm contacts · item-root metres before prefab scale / Euler degrees");
            Field(fields, record, "RightPalmContact", true);
            if (item.HoldMode == ItemHoldMode.Heavy) Field(fields, record, "LeftPalmContact", true);
            if (item is SlingshotDefinition) Field(fields, record, "PullingPalmContact", true);
            Field(fields, record, "GripFingers", true);
            Header(fields, "Hold placement and action timing");
            Group(record, "OverrideHoldSettings", "HandPose", drafts.Held.ResolveHold(item),
                item.HoldMode == ItemHoldMode.Heavy ? "HeavyHoldSettings · collider center / shoulder midpoint / average arm lengths" : "HoldSettings · shoulder frame / arm lengths");
            Group(record, "OverrideFirstPersonPose", "FirstPersonPose", drafts.Held.ResolveSpatial(item, true),
                item.HoldMode == ItemHoldMode.Heavy ? "resolved heavy hold spatial fields" : "FirstPersonPose · camera-oriented shoulder frame / arm lengths");
            Field(fields, record, "ThrowChargeTime", true);
            if (item is SlingshotDefinition sling)
            {
                Group(record, "OverrideRemoteChargePose", "RemoteChargePose", drafts.Held.ResolveCharge(sling, false), "SlingshotChargePose · aim axes");
                Group(record, "OverrideFirstPersonChargePose", "FirstPersonChargePose", drafts.Held.ResolveCharge(sling, true), "SlingshotChargePose · camera axes");
                Field(fields, record, "RecoverySeconds", true);
                if (Inspect != null) fields.Add(new Button(() => Inspect(item.WorldPrefab)) { text = "Inspect mechanical pouch / forks / bands" });
            }
        }
        private void Group(GripAuthoringDraft record, string flag, string name, object effective, string source)
        {
            bool enabled = (bool)GripAuthoringFields.Get(record.Values, flag);
            var toggle = new Toggle(flag) { value = enabled };
            toggle.RegisterValueChangedCallback(evt =>
            {
                BeforeEdit?.Invoke(); editing = true;
                try { drafts.SetOverride(record, flag, evt.newValue); } finally { editing = false; }
                Rebuild();
            }); fields.Add(toggle);
            fields.Add(new Label(enabled ? $"{record.DisplayName}.{name} · override" : $"{drafts.Held.name}.{source} · inherited"));
            if (enabled) Field(fields, record, name, true);
            else ObjectFields(fields, record, effective, name, false);
        }
        private static void Header(VisualElement parent, string text)
        { var label = new Label(text); label.AddToClassList("section-label"); parent.Add(label); }
        private void Field(VisualElement parent, GripAuthoringDraft record, string path, bool editable) =>
            ValueField(parent, record, path, GripAuthoringFields.Get(record.Values, path), editable);
        private void ObjectFields(VisualElement parent, GripAuthoringDraft record, object value, string path, bool editable)
        {
            foreach (var field in value.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public))
            {
                if (record.Runtime is SlingshotDefinition && path is "HandPose" or "FirstPersonPose" &&
                    field.Name is "ChargeControlPosition" or "ChargedPosition" or "ChargedWristEuler" or "ChargePoseDuration") continue;
                string next = string.IsNullOrEmpty(path) ? field.Name : path + "." + field.Name;
                ValueField(parent, record, next, field.GetValue(value), editable);
            }
        }
        private void ValueField(VisualElement parent, GripAuthoringDraft record, string path, object value, bool editable)
        {
            string name = path.Split('.').Last();
            void Set(object next)
            {
                BeforeEdit?.Invoke(); editing = true;
                try { drafts.Edit(record, () => GripAuthoringFields.Set(record.Values, path, next)); }
                finally { editing = false; }
            }
            if (value is Vector3 vector)
            {
                var row = new VisualElement(); row.AddToClassList("vector-row"); row.Add(new Label(name));
                for (int axis = 0; axis < 3; axis++)
                {
                    int index = axis;
                    var number = new FloatField("XYZ"[axis].ToString()) { value = vector[axis], isDelayed = false };
                    number.SetEnabled(editable); number.RegisterValueChangedCallback(evt =>
                    {
                        if (!float.IsFinite(evt.newValue)) return;
                        var current = (Vector3)GripAuthoringFields.Get(record.Values, path); current[index] = evt.newValue; Set(current);
                    }); row.Add(number);
                }
                if (editable && SelectHandle != null && GripAuthoringFields.IsSpatial(path))
                    row.Add(new Button(() => SelectHandle(path)) { text = "Handle" });
                parent.Add(row);
            }
            else if (value is float number)
            {
                var field = new FloatField(name) { value = number, isDelayed = false }; field.SetEnabled(editable);
                field.RegisterValueChangedCallback(evt => { if (float.IsFinite(evt.newValue)) Set(evt.newValue); }); parent.Add(field);
            }
            else if (value is bool boolean)
            {
                var field = new Toggle(name) { value = boolean }; field.SetEnabled(editable);
                field.RegisterValueChangedCallback(evt => Set(evt.newValue)); parent.Add(field);
            }
            else if (value is AnimationClip || value == null && name.Contains("Fingers"))
            {
                var clip = value as AnimationClip;
                if (editable && ClipField != null) parent.Add(ClipField(path, clip, next => Set(next)));
                else parent.Add(new Label($"{name}: {(clip ? clip.name : "Inherited")}"));
                if (!clip && record.Runtime is ItemDefinition)
                    parent.Add(new Label($"Effective: {drafts.Avatars.Animations.GripFingers.name} · {drafts.Avatars.Animations.name}.GripFingers"));
            }
            else if (value != null)
            {
                var group = new Foldout { text = name + " · " + GripAuthoringFields.Units(path), value = true }; parent.Add(group);
                if (editable && SelectHandle != null && GripAuthoringFields.IsSpatial(path))
                    group.Add(new Button(() => SelectHandle(path)) { text = "Edit spatial handle" });
                ObjectFields(group, record, value, path, editable);
            }
        }
        private void BuildActions()
        {
            var actions = root.Q("actions"); actions.Clear();
            void Button(string text, Action action) => actions.Add(new Button(action) { text = text });
            Button("Equip / replenish", scene.Equip); Button("Dequip", scene.Dequip);
            Button("Begin hold", scene.BeginUse); Button("Release", scene.EndUse); Button("Cancel", scene.Cancel);
            var direct = new Button(scene.DirectUse) { text = "Drink", name = "direct-use" }; actions.Add(direct);
            Button("Drop", scene.Drop); Button("Edit / unlock cursor (F2)", () => scene.SetEditing(true)); Button("Exit", scene.Exit);
        }
        private void BuildCameras()
        {
            var controls = root.Q("camera-controls");
            var presets = new DropdownField("Resolution", GripComparisonViews.Presets.Select(size => $"{size.x} × {size.y}").ToList(), 0);
            presets.RegisterValueChangedCallback(_ => { var size = GripComparisonViews.Presets[presets.index]; scene.Views.Resize(size.x, size.y); }); controls.Add(presets);
            var width = new IntegerField("Width") { value = scene.Views.Width, isDelayed = true };
            var height = new IntegerField("Height") { value = scene.Views.Height, isDelayed = true };
            width.RegisterValueChangedCallback(evt => scene.Views.Resize(evt.newValue, scene.Views.Height));
            height.RegisterValueChangedCallback(evt => scene.Views.Resize(scene.Views.Width, evt.newValue)); controls.Add(width); controls.Add(height);
            var fov = new FloatField("Vertical FOV °") { value = scene.Views.FirstPerson.fieldOfView, isDelayed = true };
            fov.RegisterValueChangedCallback(evt => scene.Views.SetFov(evt.newValue)); controls.Add(fov);
        }
        private void Expand(int pane)
        {
            expanded = expanded == pane ? 0 : pane;
            firstPane.style.display = expanded == 2 ? DisplayStyle.None : DisplayStyle.Flex;
            observerPane.style.display = expanded == 1 ? DisplayStyle.None : DisplayStyle.Flex;
        }
        private void CameraChanged()
        { firstImage.image = scene.Views.FirstTexture; observerImage.image = scene.Views.ObserverTexture; UpdateReadouts(); }
        public static Rect ImageRect(Image image)
        {
            Rect available = image.worldBound;
            if (!image.image || available.width <= 0f || available.height <= 0f) return available;
            float aspect = (float)image.image.width / image.image.height;
            Vector2 size = available.width / available.height > aspect ? new Vector2(available.height * aspect, available.height) : new Vector2(available.width, available.width / aspect);
            return new Rect(available.center - size * 0.5f, size);
        }
        public void UpdateReadouts()
        {
            dirty.text = string.Join(" · ", drafts.Records.Where(record => record.Dirty).Select(record => record.DisplayName + " *"));
            if (string.IsNullOrEmpty(dirty.text)) dirty.text = "No unsaved drafts";
            if (!scene || scene.Views == null) return;
            var item = drafts.Items.Get(drafts.SelectedItem);
            condition.text = $"{drafts.SelectedAvatar} · {item?.ItemName} · {scene.ActionPhase}";
            root.Q<Button>("direct-use").style.display = item is PotionDefinition ? DisplayStyle.Flex : DisplayStyle.None;
            firstReadout.text = Projection(scene.Views.FirstPerson, scene.Views.FirstTexture, firstImage) + "\n" + scene.ReachText(true);
            observerReadout.text = Projection(scene.Views.Observer, scene.Views.ObserverTexture, observerImage) + "\n" + scene.ReachText(false);
        }
        private static string Projection(Camera camera, RenderTexture texture, Image image)
        {
            Rect rect = ImageRect(image);
            return $"{texture.width} × {texture.height} px · viewport {rect.width:F0} × {rect.height:F0} at {rect.x:F0}, {rect.y:F0} · aspect {(rect.height > 0 ? rect.width / rect.height : 0):F3} · vFOV {camera.fieldOfView:F1}°";
        }
        public void Dispose()
        {
            drafts.Changed -= Changed;
            if (scene && scene.Views != null) scene.Views.Changed -= CameraChanged;
            root.Clear();
        }
    }

    public static class GripAuthoringFields
    {
        public static object Get(object root, string path)
        {
            foreach (string part in path.Split('.')) root = root.GetType().GetField(part).GetValue(root);
            return root;
        }
        public static void Set(object root, string path, object value)
        {
            int dot = path.IndexOf('.');
            var field = root.GetType().GetField(dot < 0 ? path : path[..dot]);
            if (dot < 0) { field.SetValue(root, value); return; }
            var child = field.GetValue(root); Set(child, path[(dot + 1)..], value); field.SetValue(root, child);
        }
        public static bool IsSpatial(string path) => path.EndsWith("Contact") || path.EndsWith("Correction") ||
            path is "HandPose" or "FirstPersonPose" or "RemoteChargePose" or "FirstPersonChargePose" or "HoldSettings" or "HeavyHoldSettings" or "SlingshotChargePose" ||
            path.EndsWith(".HoldPosition") || path.EndsWith(".ChargedPosition") || path.EndsWith(".ChargeControlPosition") ||
            path.EndsWith(".ChargedPositionOffset") || path.EndsWith(".PullingHandDrawOffset");
        public static string Units(string path) => path.Contains("Correction") ? "generated palm axes; metres before avatar scale / Euler degrees" :
            path.Contains("Contact") ? "item axes; metres before prefab scale / Euler degrees" :
            path.Contains("ChargePose") ? "camera/aim offset metres / Euler degrees / item draw metres" :
            path.Contains("Pose") || path.Contains("HoldSettings") || path is "Left" or "Right" ? "arm lengths / Euler degrees / seconds" : "metres / seconds";
    }
}
#endif
