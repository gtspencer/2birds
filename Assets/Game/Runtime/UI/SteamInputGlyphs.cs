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
        private readonly SteamLifetime steam = SteamLifetime.Instance;
        private readonly Callback<SteamInputDeviceConnected_t> connected;
        private readonly Callback<SteamInputDeviceDisconnected_t> disconnected;
        private readonly Callback<SteamInputConfigurationLoaded_t> configured;
        private readonly Callback<SteamInputGamepadSlotChange_t> slotChanged;
        public event Action Changed;

        public void Invalidate()
        {
            var failed = new List<EInputActionOrigin>();
            foreach (var pair in glyphs) if (!pair.Value.Texture) failed.Add(pair.Key);
            foreach (var origin in failed) glyphs.Remove(origin);
        }

        public SteamInputGlyphs()
        {
            if (!steam || !steam.InputReady) return;
            connected = Callback<SteamInputDeviceConnected_t>.Create(_ => AssociationChanged());
            disconnected = Callback<SteamInputDeviceDisconnected_t>.Create(_ => AssociationChanged());
            configured = Callback<SteamInputConfigurationLoaded_t>.Create(_ => AssociationChanged());
            slotChanged = Callback<SteamInputGamepadSlotChange_t>.Create(_ => AssociationChanged());
            SteamInput.EnableDeviceCallbacks();
        }

        private void AssociationChanged() { Invalidate(); Changed?.Invoke(); }

        public Texture2D Get(InputControl control, out string name, out ESteamInputType inputType)
        {
            inputType = ESteamInputType.k_ESteamInputType_Unknown;
            name = null;
            if (!steam || !steam.InputReady || control?.device is not XInputController) return null;
            inputType = ESteamInputType.k_ESteamInputType_XBoxOneController;
            return Get(XboxOrigin(control), out name);
        }

        public Texture2D Get(EInputActionOrigin origin, out string name)
        {
            name = null;
            if (!steam || !steam.InputReady || origin == EInputActionOrigin.k_EInputActionOrigin_None) return null;
            if (glyphs.TryGetValue(origin, out var cached))
            {
                name = cached.Name;
                return cached.Texture;
            }
            name = SteamInput.GetStringForActionOrigin(origin);
            Texture2D texture = null;
            var path = SteamInput.GetGlyphPNGForActionOrigin(origin, ESteamInputGlyphSize.k_ESteamInputGlyphSize_Medium,
                (uint)ESteamInputGlyphStyle.ESteamInputGlyphStyle_Knockout);
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

        private static EInputActionOrigin XboxOrigin(InputControl control)
        {
            string name = control.parent == ((Gamepad)control.device).dpad ? "dpad/" + control.name : control.name;
            return name switch
            {
                "buttonSouth" => EInputActionOrigin.k_EInputActionOrigin_XBoxOne_A,
                "buttonEast" => EInputActionOrigin.k_EInputActionOrigin_XBoxOne_B,
                "buttonWest" => EInputActionOrigin.k_EInputActionOrigin_XBoxOne_X,
                "buttonNorth" => EInputActionOrigin.k_EInputActionOrigin_XBoxOne_Y,
                "leftShoulder" => EInputActionOrigin.k_EInputActionOrigin_XBoxOne_LeftBumper,
                "rightShoulder" => EInputActionOrigin.k_EInputActionOrigin_XBoxOne_RightBumper,
                "leftTrigger" => EInputActionOrigin.k_EInputActionOrigin_XBoxOne_LeftTrigger_Pull,
                "rightTrigger" => EInputActionOrigin.k_EInputActionOrigin_XBoxOne_RightTrigger_Pull,
                "leftStick" => EInputActionOrigin.k_EInputActionOrigin_XBoxOne_LeftStick_Move,
                "rightStick" => EInputActionOrigin.k_EInputActionOrigin_XBoxOne_RightStick_Move,
                "leftStickPress" => EInputActionOrigin.k_EInputActionOrigin_XBoxOne_LeftStick_Click,
                "rightStickPress" => EInputActionOrigin.k_EInputActionOrigin_XBoxOne_RightStick_Click,
                "start" => EInputActionOrigin.k_EInputActionOrigin_XBoxOne_Menu,
                "select" => EInputActionOrigin.k_EInputActionOrigin_XBoxOne_View,
                "dpad/up" => EInputActionOrigin.k_EInputActionOrigin_XBoxOne_DPad_North,
                "dpad/down" => EInputActionOrigin.k_EInputActionOrigin_XBoxOne_DPad_South,
                "dpad/left" => EInputActionOrigin.k_EInputActionOrigin_XBoxOne_DPad_West,
                "dpad/right" => EInputActionOrigin.k_EInputActionOrigin_XBoxOne_DPad_East,
                _ => EInputActionOrigin.k_EInputActionOrigin_None
            };
        }

        public void Dispose()
        {
            connected?.Dispose();
            disconnected?.Dispose();
            configured?.Dispose();
            slotChanged?.Dispose();
            foreach (var glyph in glyphs.Values)
                if (glyph.Texture) UnityEngine.Object.Destroy(glyph.Texture);
            glyphs.Clear();
        }
    }
}
