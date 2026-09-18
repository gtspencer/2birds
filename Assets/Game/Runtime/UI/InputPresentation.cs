using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.InputSystem.LowLevel;

namespace TwoBirds
{
    public sealed class InputPresentation : IDisposable
    {
        private readonly InputBindings bindings;
        private readonly SteamInputGlyphs glyphs = new();
        public InputDevice ActiveDevice { get; private set; } = Keyboard.current;
        public string ActiveGroup => ActiveDevice is Gamepad ? InputBindings.Controller : InputBindings.KeyboardMouse;
        public event Action Changed;

        public InputPresentation(InputBindings bindings)
        {
            this.bindings = bindings;
            bindings.Changed += Notify;
            InputSystem.onEvent += TrackDevice;
            InputSystem.onDeviceChange += DeviceChanged;
        }

        public (string Text, Texture2D Glyph) Resolve(InputAction action, string group = null, Guid? bindingId = null)
        {
            if (action == null) return ("Unassigned", null);
            group ??= ActiveGroup;
            var texts = new List<string>();
            Texture2D glyph = null;
            foreach (var binding in action.bindings)
            {
                if (binding.isComposite || !InputBindings.InGroup(binding, group) ||
                    bindingId.HasValue && binding.id != bindingId.Value) continue;
                string path = binding.effectivePath;
                if (string.IsNullOrEmpty(path)) continue;
                InputControl control = null;
                if (group == InputBindings.Controller)
                {
                    var device = ActiveDevice is Gamepad active && active.added ? active : Gamepad.current;
                    if (device != null) control = InputControlPath.TryFindControl(device, path);
                }
                string text = InputControlPath.ToHumanReadableString(path,
                    InputControlPath.HumanReadableStringOptions.OmitDevice, control);
                var texture = glyphs.Get(control, out var name);
                if (!string.IsNullOrEmpty(name)) text = name;
                if (binding.isPartOfComposite && !bindingId.HasValue) text = $"{binding.name}: {text}";
                texts.Add(text);
                glyph = texture;
            }
            return texts.Count == 0 ? ("Unassigned", null) :
                (string.Join(" / ", texts), texts.Count == 1 ? glyph : null);
        }

        private void TrackDevice(InputEventPtr evt, InputDevice device)
        {
            if (evt.type != StateEvent.Type && evt.type != DeltaStateEvent.Type) return;
            if (device is not Keyboard && device is not Mouse && device is not Gamepad) return;
            foreach (var control in evt.EnumerateChangedControls(device, 0.25f))
            {
                if (device is Mouse mouse && control is not ButtonControl)
                {
                    bool moved = mouse.delta.ReadValueFromEvent(evt, out var delta) && delta.sqrMagnitude >= 4f;
                    bool scrolled = mouse.scroll.ReadValueFromEvent(evt, out var scroll) && scroll.sqrMagnitude >= 1f;
                    if (!moved && !scrolled) continue;
                }
                bool changed = device is Gamepad ? ActiveDevice != device : ActiveDevice is Gamepad;
                ActiveDevice = device;
                if (changed) Notify();
                break;
            }
        }

        private void DeviceChanged(InputDevice device, InputDeviceChange change)
        {
            if (ActiveDevice == device && change is InputDeviceChange.Removed or InputDeviceChange.Disconnected or InputDeviceChange.Disabled)
                ActiveDevice = Keyboard.current;
            Notify();
        }

        private void Notify() => Changed?.Invoke();

        public void Dispose()
        {
            bindings.Changed -= Notify;
            InputSystem.onEvent -= TrackDevice;
            InputSystem.onDeviceChange -= DeviceChanged;
            glyphs.Dispose();
        }
    }
}
