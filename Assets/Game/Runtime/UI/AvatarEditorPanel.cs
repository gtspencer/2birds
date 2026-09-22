using System;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

namespace TwoBirds
{
    [RequireComponent(typeof(UIDocument))]
    public sealed class AvatarEditorPanel : MonoBehaviour
    {
        private AvatarEditorController editor;
        private VisualElement root, controls, viewport, library, equipped, gizmo, dialog, hints;
        private Slider red, green, blue;
        private MenuNavigation navigation;
        private string tab = "avatars";
        private enum Gesture { None, Orbit, Move, Rotate, Resize }
        private Gesture gesture;
        private int pointerId = -1;
        private Vector2 lastPointer;
        private InputAction orbit, zoom, move, rotate, size;
        private bool refreshing;
        internal void Initialize(AvatarEditorController value)
        {
            editor = value;
            root = GetComponent<UIDocument>().rootVisualElement.Q("avatar-editor");
            controls = root.Q("editor-controls"); viewport = root.Q("preview");
            library = root.Q<ScrollView>("library").contentContainer; equipped = root.Q<ScrollView>("equipped").contentContainer;
            gizmo = root.Q("tattoo-gizmo"); dialog = root.Q("discard-dialog"); hints = root.Q("editor-hints");
            root.Q<Image>("preview-image").image = editor.Preview.Texture;
            root.Q<Image>("preview-image").scaleMode = ScaleMode.StretchToFill;
            red = root.Q<Slider>("ink-r"); green = root.Q<Slider>("ink-g"); blue = root.Q<Slider>("ink-b");
            red.RegisterValueChangedCallback(ColorChanged); green.RegisterValueChangedCallback(ColorChanged); blue.RegisterValueChangedCallback(ColorChanged);
            Click("avatars-tab", () => { tab = "avatars"; Refresh(); });
            Click("hats-tab", () => { tab = "hats"; Refresh(); });
            Click("tattoos-tab", () => { tab = "tattoos"; Refresh(); });
            Click("edit-placement", () => editor.SetPlacement(true));
            Click("remove-all", editor.RemoveAll); Click("apply", editor.Apply); Click("cancel-editor", editor.Cancel);
            Click("reset-view", () => { editor.Preview.ResetView(); UpdateGizmos(); });
            Click("keep-editing", editor.KeepEditing); Click("discard", editor.Cancel);
            foreach (var swatch in new[] { new Color32(24, 24, 24, 255), new Color32(160, 38, 38, 255),
                new Color32(34, 76, 145, 255), new Color32(39, 119, 65, 255), new Color32(225, 209, 171, 255) })
            {
                var color = swatch;
                var button = new Button(() => { editor.SetInk(color); RefreshColor(); }) { text = $"RGB {color.r}, {color.g}, {color.b}" };
                button.style.backgroundColor = (Color)color; root.Q("swatches").Add(button);
            }
            var map = InputSystem.actions.FindActionMap("AvatarEditor", true);
            orbit = map.FindAction("Orbit", true); zoom = map.FindAction("Zoom", true);
            move = map.FindAction("Move", true); rotate = map.FindAction("Rotate", true); size = map.FindAction("Resize", true);
            viewport.RegisterCallback<GeometryChangedEvent>(evt => editor.Preview.Resize((int)evt.newRect.width, (int)evt.newRect.height));
            viewport.RegisterCallback<PointerDownEvent>(PointerDown);
            viewport.RegisterCallback<PointerMoveEvent>(PointerMove);
            viewport.RegisterCallback<PointerUpEvent>(_ => ReleaseGesture());
            viewport.RegisterCallback<PointerCaptureOutEvent>(_ => { gesture = Gesture.None; pointerId = -1; });
            viewport.RegisterCallback<WheelEvent>(evt => {
                if (Blocked) return; editor.Preview.FocusTattoo(editor.Selection);
                editor.Preview.Zoom(-evt.delta.y * 0.04f); UpdateGizmos(); evt.StopPropagation();
            });
            controls.RegisterCallback<PointerDownEvent>(_ => { if (editor.Placement) editor.SetPlacement(false); }, TrickleDown.TrickleDown);
            root.RegisterCallback<NavigationCancelEvent>(evt => { editor.Back(); evt.StopImmediatePropagation(); });
            editor.Changed += Refresh;
            editor.Session.InputPresentation.Changed += Hints;
            navigation = new MenuNavigation(root, editor.Session.InputPresentation,
                () => !editor.IsOpen ? null : editor.Confirming ? dialog : editor.Placement ? null : root,
                () => editor.Confirming ? root.Q<Button>("keep-editing") : root.Q<Button>(tab + "-tab"));
        }
        private bool Blocked => !editor.IsOpen || editor.Confirming || editor.Session.InputPresentation.SuppressInput;
        private void Click(string name, Action action) => root.Q<Button>(name).clicked += action;
        internal void SetVisible(bool visible)
        { root.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None; if (visible) root.schedule.Execute(() => navigation?.Repair()); }
        private void Refresh()
        {
            if (!editor.IsOpen) return;
            string focus = (root.panel?.focusController.focusedElement as VisualElement)?.name;
            refreshing = true;
            library.Clear(); equipped.Clear();
            var session = editor.Session;
            if (tab == "avatars")
                foreach (var entry in session.Avatars.Entries)
                {
                    var id = entry.Id;
                    Entry(entry.Settings.Icon, entry.Settings.DisplayName, session.Unlocks.Available(id), "avatar-" + id,
                        () => editor.SelectAvatar(id));
                }
            else if (tab == "hats")
            {
                Entry(null, "No hat", true, "no-hat", () => editor.SelectHat(default));
                foreach (var entry in session.Hats.Entries)
                {
                    var id = entry.Id;
                    Entry(entry.Icon, entry.DisplayName, session.Unlocks.Available(id), "hat-" + id, () => editor.SelectHat(id));
                }
            }
            else
            {
                if (session.Tattoos.Entries.Count == 0) library.Add(new Label("No tattoo designs available."));
                foreach (var entry in session.Tattoos.Entries)
                {
                    var id = entry.Id;
                    var button = Entry(entry.Icon, entry.DisplayName, true, "design-" + id, () => editor.AddTattoo(id));
                    button.SetEnabled(!editor.Switching && editor.Draft.Tattoos.Length < AvatarAppearance.MaximumTattoos);
                }
            }
            for (int i = 0; i < editor.Draft.Tattoos.Length; i++)
            {
                int index = i; var tattoo = editor.Draft.Tattoos[i];
                string name = session.Tattoos.TryResolve(tattoo.Design, out var definition) ? definition.DisplayName : "Tattoo";
                var row = new VisualElement(); row.AddToClassList("equipped-row");
                row.Add(new Button(() => editor.SelectTattoo(index)) { name = "tattoo-" + i, text = $"{i + 1}. {name}" });
                row.Add(new Button(() => editor.RemoveTattoo(index)) { name = "remove-" + i, text = "X", tooltip = $"Remove tattoo {i + 1}" });
                equipped.Add(row);
            }
            root.Q("tattoo-controls").SetEnabled(editor.Selection >= 0 && !editor.Switching);
            equipped.SetEnabled(!editor.Switching);
            root.Q("apply").SetEnabled(!editor.Switching); root.Q("remove-all").SetEnabled(!editor.Switching);
            dialog.style.display = editor.Confirming ? DisplayStyle.Flex : DisplayStyle.None;
            RefreshColor(); refreshing = false; UpdateGizmos(); Hints();
            VisualElement preferred = editor.Confirming ? root.Q("keep-editing") : string.IsNullOrEmpty(focus) ? null : root.Q(focus);
            if (preferred == null && editor.Selection >= 0) preferred = root.Q("tattoo-" + editor.Selection);
            navigation?.Repair(preferred);
        }
        private Button Entry(Sprite icon, string title, bool available, string name, Action action)
        {
            var button = new Button(() => { if (available && !editor.Switching) action(); }) { name = name, tooltip = available ? title : "Locked" };
            button.AddToClassList("library-entry");
            if (icon)
            {
                var image = new Image { sprite = icon, scaleMode = ScaleMode.ScaleToFit };
                if (!available) image.tintColor = Color.black;
                button.Add(image);
            }
            button.Add(new Label(available ? title : "Locked"));
            if (!available) { button.Add(new Label("🔒")); button.AddToClassList("locked"); }
            button.SetEnabled(!editor.Switching); library.Add(button); return button;
        }
        private void ColorChanged(ChangeEvent<float> evt)
        { if (!refreshing) editor.SetInk(new Color32((byte)red.value, (byte)green.value, (byte)blue.value, 255)); }
        private void RefreshColor()
        {
            if (editor.Selection < 0) return;
            var value = editor.Draft.Tattoos[editor.Selection];
            red.SetValueWithoutNotify(value.R); green.SetValueWithoutNotify(value.G); blue.SetValueWithoutNotify(value.B);
        }
        private Vector2 UV(Vector2 local) => new(local.x / viewport.contentRect.width, 1f - local.y / viewport.contentRect.height);
        private void PointerDown(PointerDownEvent evt)
        {
            if (Blocked || editor.Switching) return;
            var name = (evt.target as VisualElement)?.name;
            gesture = evt.button == 1 ? Gesture.Orbit : evt.button != 0 ? Gesture.None : name switch
            {
                "move-handle" => Gesture.Move, "rotate-handle" => Gesture.Rotate, "size-handle" => Gesture.Resize, _ => Gesture.Orbit
            };
            if (gesture == Gesture.None) return;
            lastPointer = viewport.WorldToLocal(evt.position); pointerId = evt.pointerId;
            viewport.CapturePointer(pointerId); evt.StopPropagation();
        }
        private void PointerMove(PointerMoveEvent evt)
        {
            if (Blocked) { ReleaseGesture(); return; }
            Vector2 point = viewport.WorldToLocal(evt.position);
            editor.Preview.Look(UV(point));
            if (gesture == Gesture.None) return;
            Vector2 delta = point - lastPointer;
            if (gesture == Gesture.Orbit) editor.Preview.Orbit(new Vector2(-delta.x, delta.y) * 0.3f);
            else if (editor.Selection >= 0)
            {
                var value = editor.Draft.Tattoos[editor.Selection];
                if (gesture == Gesture.Move)
                { if (editor.Preview.Place(UV(point), value, out var moved)) value = moved; }
                else if (gesture == Gesture.Rotate)
                {
                    Vector2 center = gizmo.layout.center;
                    value.Rotation += Vector2.SignedAngle(lastPointer - center, point - center);
                }
                else value.Size = Mathf.Clamp(value.Size * Mathf.Exp((delta.x + delta.y) * 0.01f), 0.02f, 2f);
                editor.UpdateTattoo(value);
            }
            lastPointer = point; UpdateGizmos(); evt.StopPropagation();
        }
        internal void ReleaseGesture()
        {
            if (pointerId >= 0 && viewport.HasPointerCapture(pointerId)) viewport.ReleasePointer(pointerId);
            pointerId = -1; gesture = Gesture.None;
        }
        internal void TickInput()
        {
            if (editor.Switching) return;
            float dt = Time.unscaledDeltaTime;
            Vector2 turn = orbit.ReadValue<Vector2>(); float zoomValue = zoom.ReadValue<float>();
            bool changed = turn.sqrMagnitude > 0 || zoomValue != 0;
            if (turn.sqrMagnitude > 0) editor.Preview.Orbit(new Vector2(-turn.x, -turn.y) * (100 * dt));
            if (zoomValue != 0) { editor.Preview.FocusTattoo(editor.Selection); editor.Preview.Zoom(zoomValue * dt); }
            if (editor.Placement && editor.Selection >= 0)
            {
                var value = editor.Draft.Tattoos[editor.Selection];
                var movement = move.ReadValue<Vector2>(); float rotation = rotate.ReadValue<float>(), resizing = size.ReadValue<float>();
                if (movement.sqrMagnitude > 0 && editor.Preview.TattooPoint(editor.Selection, out var point))
                {
                    Vector2 uv = editor.Preview.Camera.WorldToViewportPoint(point);
                    if (editor.Preview.Place(uv + movement * (0.3f * dt), value, out var moved)) value = moved;
                }
                if (rotation != 0) value.Rotation += rotation * 100 * dt;
                if (resizing != 0) value.Size = Mathf.Clamp(value.Size * Mathf.Exp(resizing * dt), 0.02f, 2);
                if (movement.sqrMagnitude > 0 || rotation != 0 || resizing != 0) { editor.UpdateTattoo(value); changed = true; }
            }
            if (changed) UpdateGizmos();
        }
        internal void UpdateGizmos()
        {
            bool visible = editor.Placement && editor.Preview.TattooPoint(editor.Selection, out _);
            gizmo.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
            if (!visible) return;
            editor.Preview.TattooPoint(editor.Selection, out var point);
            var uv = editor.Preview.Camera.WorldToViewportPoint(point);
            gizmo.style.left = uv.x * viewport.contentRect.width - 60;
            gizmo.style.top = (1f - uv.y) * viewport.contentRect.height - 60;
        }
        private void Hints()
        {
            InputPrompt.Begin(hints); var presentation = editor.Session.InputPresentation;
            if (!presentation.IsController) hints.Add(new Label("Right drag / background drag: Rotate · Wheel: Zoom"));
            else { InputPrompt.AddAction(hints, presentation, "AvatarEditor/Orbit", "Rotate view"); InputPrompt.AddAction(hints, presentation, "AvatarEditor/Zoom", "Zoom"); }
            if (editor.Placement)
            {
                if (presentation.IsController)
                {
                    InputPrompt.AddAction(hints, presentation, "AvatarEditor/Move", "Move tattoo");
                    InputPrompt.AddAction(hints, presentation, "AvatarEditor/Rotate", "Rotate tattoo");
                    InputPrompt.AddAction(hints, presentation, "AvatarEditor/Resize", "Resize tattoo");
                }
                else hints.Add(new Label("Blue: Move · Gold ring: Rotate · Green: Resize"));
            }
            InputPrompt.AddAction(hints, presentation, "UI/Cancel", editor.Placement ? "Return to panel" : "Back");
        }
        private void OnDestroy()
        {
            navigation?.Dispose();
            if (!editor) return;
            editor.Changed -= Refresh; editor.Session.InputPresentation.Changed -= Hints;
        }
    }
}
