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
        private readonly Dictionary<string, Texture2D> nativeGlyphs = new();
        private readonly SteamLifetime steam;
        private bool gameplay, focused = true, overlay, releasing;
        private int releaseFrame;
        private int ignorePointerThrough;
        private double lastController, mouseSince, lastMouse;
        public InputDevice ActiveDevice { get; private set; } = Keyboard.current;
        public string ActiveGroup => ActiveDevice is Gamepad ? InputBindings.Controller : InputBindings.KeyboardMouse;
        public bool SuppressInput => !focused || overlay || releasing || Time.frameCount <= releaseFrame;
        public event Action Changed;
        public event Action Interrupted;

        public InputPresentation(InputBindings bindings)
        {
            this.bindings = bindings;
            bindings.Changed += Notify;
            glyphs.Changed += GlyphsChanged;
            InputSystem.onEvent += TrackDevice;
            InputSystem.onDeviceChange += DeviceChanged;
            Application.focusChanged += FocusChanged;
            steam = SteamLifetime.Instance;
            if (steam) steam.OverlayChanged += OverlayChanged;
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
                if (!texture && control?.device is Gamepad pad)
                {
                    string family = pad is UnityEngine.InputSystem.DualShock.DualShockGamepad ? "PlayStation" :
                        pad is UnityEngine.InputSystem.Switch.SwitchProController ? "Nintendo" :
                        pad is UnityEngine.InputSystem.XInput.XInputController ? "Xbox" : "Generic";
                    string key = control.parent == pad.dpad ? "dpad-" + control.name : control.name;
                    string resource = $"ControllerGlyphs/{family}/{key}";
                    if (!nativeGlyphs.TryGetValue(resource, out texture))
                        nativeGlyphs[resource] = texture = Resources.Load<Texture2D>(resource);
                    name = control.shortDisplayName ?? control.displayName;
                    if (family == "Generic") name = key switch
                    {
                        "buttonSouth" => "South", "buttonEast" => "East",
                        "buttonWest" => "West", "buttonNorth" => "North", _ => name
                    };
                }
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
            if (!focused || overlay) return;
            foreach (var control in evt.EnumerateControls(InputControlExtensions.Enumerate.IgnoreControlsInCurrentState |
                InputControlExtensions.Enumerate.IncludeNonLeafControls, device, 0.25f))
            {
                if (control is ButtonControl button &&
                    (!button.ReadValueFromEvent(evt, out float value) || value < button.pressPointOrDefault)) continue;
                if (device is Gamepad && control is not ButtonControl)
                {
                    if (control is not StickControl stick || !stick.ReadValueFromEvent(evt, out var tilt) || tilt.sqrMagnitude < 0.16f) continue;
                }
                if (device is Mouse mouse && control is not ButtonControl)
                {
                    bool moved = mouse.delta.ReadValueFromEvent(evt, out var delta) && delta.sqrMagnitude >= 16f && delta.sqrMagnitude < 250000f;
                    bool scrolled = mouse.scroll.ReadValueFromEvent(evt, out var scroll) && scroll.sqrMagnitude >= 1f;
                    if (!scrolled && Time.frameCount <= ignorePointerThrough) continue;
                    if (!moved && !scrolled) continue;
                    if (!scrolled && gameplay && ActiveDevice is Gamepad)
                    {
                        if (evt.time - lastMouse > 0.2) mouseSince = evt.time;
                        lastMouse = evt.time;
                        var pad = (Gamepad)ActiveDevice;
                        if (pad.leftStick.ReadValue().sqrMagnitude >= 0.16f || pad.rightStick.ReadValue().sqrMagnitude >= 0.16f)
                            lastController = evt.time;
                        if (evt.time - lastController < 1.0 || evt.time - mouseSince < 0.4) continue;
                    }
                }
                if (device is Gamepad) lastController = evt.time;
                bool changed = device is Gamepad ? ActiveDevice != device : ActiveDevice is Gamepad;
                ActiveDevice = device;
                if (changed) Notify();
                break;
            }
        }

        private void DeviceChanged(InputDevice device, InputDeviceChange change)
        {
            glyphs.InvalidateAssociations();
            if (ActiveDevice == device && change is InputDeviceChange.Removed or InputDeviceChange.Disconnected or InputDeviceChange.Disabled)
            {
                ActiveDevice = Keyboard.current;
                Interrupted?.Invoke();
                Notify();
            }
            else if (ActiveDevice == device && change == InputDeviceChange.ConfigurationChanged) Notify();
        }

        public string Label(string action) => Resolve(InputSystem.actions.FindAction(action)).Text;
        public string ScrollLabel => Label(ActiveDevice is Gamepad ? "UI/Scroll" : "UI/ScrollWheel");

        public void SetGameplay(bool value)
        {
            if (gameplay == value) return;
            gameplay = value;
            mouseSince = lastMouse = 0;
            UpdateCursor();
        }

        private void UpdateCursor()
        {
            bool locked = gameplay && focused && !overlay;
            var mode = locked ? CursorLockMode.Locked : CursorLockMode.None;
            if (Cursor.lockState != mode)
            {
                Cursor.lockState = mode;
                ignorePointerThrough = Time.frameCount + 2;
            }
            bool visible = !locked && ActiveDevice is not Gamepad;
            if (Cursor.visible != visible) Cursor.visible = visible;
        }

        private void FocusChanged(bool value) { focused = value; Suspend(); }
        private void OverlayChanged(bool value) { overlay = value; Suspend(); }
        private void Suspend()
        {
            if (!releasing) InputSystem.onAfterUpdate += ReleaseInput;
            releasing = true;
            releaseFrame = Time.frameCount + 1;
            if (!focused || overlay) Interrupted?.Invoke();
            Notify();
        }

        internal static bool ButtonsHeld()
        {
            foreach (var device in InputSystem.devices)
                if (device is Keyboard or Mouse or Gamepad)
                    foreach (var control in device.allControls)
                        if (control is ButtonControl button && button.isPressed && control.parent is not StickControl) return true;
            return false;
        }

        private void ReleaseInput()
        {
            if (!releasing || !focused || overlay || Time.frameCount <= releaseFrame || ButtonsHeld()) return;
            releasing = false;
            InputSystem.onAfterUpdate -= ReleaseInput;
            releaseFrame = Time.frameCount;
            Notify();
        }

        private void Notify() { UpdateCursor(); Changed?.Invoke(); }
        private void GlyphsChanged() { if (ActiveDevice is Gamepad) Notify(); }

        public void Dispose()
        {
            bindings.Changed -= Notify;
            InputSystem.onEvent -= TrackDevice;
            InputSystem.onDeviceChange -= DeviceChanged;
            InputSystem.onAfterUpdate -= ReleaseInput;
            Application.focusChanged -= FocusChanged;
            if (steam) steam.OverlayChanged -= OverlayChanged;
            glyphs.Dispose();
            glyphs.Changed -= GlyphsChanged;
            foreach (var texture in nativeGlyphs.Values)
                if (texture) Resources.UnloadAsset(texture);
            nativeGlyphs.Clear();
        }
    }
}
