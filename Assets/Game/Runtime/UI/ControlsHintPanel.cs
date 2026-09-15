using System;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

namespace TwoBirds
{
    public struct ControlHint
    {
        public InputAction Action;
        public string Label;
    }

    internal sealed class ControlsHintPanel : IDisposable
    {
        private readonly VisualElement container;
        private readonly SteamInputGlyphs glyphs;
        private ControlHint[] hints;
        private (InputControl Control, Image Glyph, Label Keycap, string Text)[] rows =
            Array.Empty<(InputControl, Image, Label, string)>();
        private InputDevice lastDevice;
        private bool dirty = true;

        public ControlsHintPanel(VisualElement root, SteamInputGlyphs glyphs)
        {
            container = root.Q("controls-hint");
            this.glyphs = glyphs;
            InputSystem.onActionChange += OnActionChange;
            InputSystem.onDeviceChange += OnDeviceChange;
        }

        public void Show(ControlHint[] newHints, InputDevice device)
        {
            if (hints != newHints) { hints = newHints; dirty = true; }
            if (lastDevice != device) { lastDevice = device; dirty = true; }
            if (dirty) Rebuild();
            foreach (var row in rows)
            {
                var texture = glyphs.Get(row.Control, out var name);
                row.Glyph.image = texture;
                row.Glyph.style.display = texture ? DisplayStyle.Flex : DisplayStyle.None;
                row.Keycap.text = !string.IsNullOrEmpty(name) ? name : row.Text;
                row.Keycap.style.display = texture ? DisplayStyle.None : DisplayStyle.Flex;
            }
            container.style.display = DisplayStyle.Flex;
        }

        public void Hide() => container.style.display = DisplayStyle.None;

        private void Rebuild()
        {
            dirty = false;
            container.Clear();
            rows = new (InputControl, Image, Label, string)[hints?.Length ?? 0];
            if (hints == null) return;
            for (int i = 0; i < hints.Length; i++)
            {
                var hint = hints[i];
                var control = FindControl(hint.Action, lastDevice);
                var row = new VisualElement();
                row.AddToClassList("controls-row");
                row.pickingMode = PickingMode.Ignore;

                var glyph = new Image { pickingMode = PickingMode.Ignore };
                glyph.AddToClassList("controls-glyph");
                row.Add(glyph);

                var keycap = new Label();
                keycap.AddToClassList("controls-keycap");
                keycap.pickingMode = PickingMode.Ignore;
                row.Add(keycap);

                var label = new Label(hint.Label);
                label.AddToClassList("controls-label");
                label.pickingMode = PickingMode.Ignore;
                row.Add(label);

                container.Add(row);
                rows[i] = (control, glyph, keycap, ResolveKeyText(hint.Action, control));
            }
        }

        private static string ResolveKeyText(InputAction action, InputControl control)
        {
            if (control == null) return "?";
            int index = action.GetBindingIndexForControl(control);
            while (index > 0 && action.bindings[index].isPartOfComposite) index--;
            return action.GetBindingDisplayString(index);
        }

        private static InputControl FindControl(InputAction action, InputDevice device)
        {
            if (action == null || !action.enabled) return null;
            foreach (var control in action.controls)
                if (device is Gamepad ? control.device == device : control.device is Keyboard || control.device is Mouse)
                    return control;
            return null;
        }

        private void OnActionChange(object obj, InputActionChange change)
        {
            if (change == InputActionChange.BoundControlsChanged) dirty = true;
        }

        private void OnDeviceChange(InputDevice device, InputDeviceChange change)
        {
            if (change == InputDeviceChange.ConfigurationChanged) dirty = true;
        }

        public void Dispose()
        {
            InputSystem.onActionChange -= OnActionChange;
            InputSystem.onDeviceChange -= OnDeviceChange;
            Hide();
        }
    }
}
