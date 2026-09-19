using System;
using System.Collections.Generic;
using System.IO;
using Steamworks;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.XInput;

namespace TwoBirds
{
    internal sealed class SteamInputGlyphs : IDisposable
    {
        private readonly Dictionary<EInputActionOrigin, (Texture2D Texture, string Name)> glyphs = new();
        private readonly Dictionary<int, int> slots = new();
        private readonly SteamLifetime steam = SteamLifetime.Instance;
        private readonly Callback<SteamInputDeviceConnected_t> connected;
        private readonly Callback<SteamInputDeviceDisconnected_t> disconnected;
        private readonly Callback<SteamInputConfigurationLoaded_t> configured;
        private readonly Callback<SteamInputGamepadSlotChange_t> slotChanged;
        public event Action Changed;
        [Serializable] private sealed class XInputCapabilities { public int userIndex = -1; }

        public void InvalidateAssociations() => slots.Clear();

        public SteamInputGlyphs()
        {
            if (!steam || !steam.InputReady) return;
            connected = Callback<SteamInputDeviceConnected_t>.Create(_ => AssociationChanged());
            disconnected = Callback<SteamInputDeviceDisconnected_t>.Create(_ => AssociationChanged());
            configured = Callback<SteamInputConfigurationLoaded_t>.Create(_ => AssociationChanged());
            slotChanged = Callback<SteamInputGamepadSlotChange_t>.Create(_ => AssociationChanged());
            SteamInput.EnableDeviceCallbacks();
        }

        private void AssociationChanged() { InvalidateAssociations(); Changed?.Invoke(); }

        private EInputActionOrigin ResolveOrigin(InputControl control)
        {
            if (control?.device is not Gamepad gamepad) return EInputActionOrigin.k_EInputActionOrigin_None;
            var button = XboxOrigin(control);
            if (button == EXboxOrigin.k_EXboxOrigin_Count) return EInputActionOrigin.k_EInputActionOrigin_None;
            if (!steam || !steam.InputReady) return EInputActionOrigin.k_EInputActionOrigin_None;

            if (gamepad is not XInputController || gamepad.description.interfaceName != "XInput")
                return EInputActionOrigin.k_EInputActionOrigin_None;
            if (!slots.TryGetValue(gamepad.deviceId, out int slot))
            {
                var capabilities = new XInputCapabilities();
                if (!string.IsNullOrEmpty(gamepad.description.capabilities))
                    JsonUtility.FromJsonOverwrite(gamepad.description.capabilities, capabilities);
                slots[gamepad.deviceId] = slot = capabilities.userIndex;
            }
            if (slot < 0 || slot > 3) return EInputActionOrigin.k_EInputActionOrigin_None;
            var handle = SteamInput.GetControllerForGamepadIndex(slot);
            return handle.m_InputHandle == 0 ? EInputActionOrigin.k_EInputActionOrigin_None :
                SteamInput.GetActionOriginFromXboxOrigin(handle, button);
        }

        public Texture2D Get(InputControl control, out string name)
        {
            name = null;
            var origin = ResolveOrigin(control);
            if (origin == EInputActionOrigin.k_EInputActionOrigin_None) return null;
            if (glyphs.TryGetValue(origin, out var cached))
            {
                name = cached.Name;
                return cached.Texture;
            }
            name = SteamInput.GetStringForActionOrigin(origin);
            Texture2D texture = null;
            var path = SteamInput.GetGlyphPNGForActionOrigin(origin, ESteamInputGlyphSize.k_ESteamInputGlyphSize_Medium, 0);
            try
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                    if (!texture.LoadImage(File.ReadAllBytes(path)))
                    {
                        UnityEngine.Object.Destroy(texture);
                        texture = null;
                    }
                }
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                if (texture) UnityEngine.Object.Destroy(texture);
                texture = null;
            }
            glyphs[origin] = (texture, name);
            return texture;
        }

        private static EXboxOrigin XboxOrigin(InputControl control)
        {
            string name = control.parent == ((Gamepad)control.device).dpad ? "dpad/" + control.name : control.name;
            return name switch
            {
                "buttonSouth" => EXboxOrigin.k_EXboxOrigin_A,
                "buttonEast" => EXboxOrigin.k_EXboxOrigin_B,
                "buttonWest" => EXboxOrigin.k_EXboxOrigin_X,
                "buttonNorth" => EXboxOrigin.k_EXboxOrigin_Y,
                "leftShoulder" => EXboxOrigin.k_EXboxOrigin_LeftBumper,
                "rightShoulder" => EXboxOrigin.k_EXboxOrigin_RightBumper,
                "leftTrigger" => EXboxOrigin.k_EXboxOrigin_LeftTrigger_Pull,
                "rightTrigger" => EXboxOrigin.k_EXboxOrigin_RightTrigger_Pull,
                "leftStick" => EXboxOrigin.k_EXboxOrigin_LeftStick_Move,
                "rightStick" => EXboxOrigin.k_EXboxOrigin_RightStick_Move,
                "leftStickPress" => EXboxOrigin.k_EXboxOrigin_LeftStick_Click,
                "rightStickPress" => EXboxOrigin.k_EXboxOrigin_RightStick_Click,
                "start" => EXboxOrigin.k_EXboxOrigin_Menu,
                "select" => EXboxOrigin.k_EXboxOrigin_View,
                "dpad/up" => EXboxOrigin.k_EXboxOrigin_DPad_North,
                "dpad/down" => EXboxOrigin.k_EXboxOrigin_DPad_South,
                "dpad/left" => EXboxOrigin.k_EXboxOrigin_DPad_West,
                "dpad/right" => EXboxOrigin.k_EXboxOrigin_DPad_East,
                _ => EXboxOrigin.k_EXboxOrigin_Count
            };
        }

        public void Dispose()
        {
            connected?.Dispose();
            disconnected?.Dispose();
            configured?.Dispose();
            slotChanged?.Dispose();
            slots.Clear();
            foreach (var glyph in glyphs.Values)
                if (glyph.Texture) UnityEngine.Object.Destroy(glyph.Texture);
            glyphs.Clear();
        }
    }
}
