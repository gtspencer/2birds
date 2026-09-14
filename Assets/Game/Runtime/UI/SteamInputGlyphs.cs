using System;
using System.Collections.Generic;
using System.IO;
using Steamworks;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.DualShock;
using UnityEngine.InputSystem.Switch;
using UnityEngine.InputSystem.XInput;

namespace TwoBirds
{
    internal sealed class SteamInputGlyphs : IDisposable
    {
        private readonly Dictionary<EInputActionOrigin, Texture2D> textures = new();
        private readonly InputHandle_t[] handles = new InputHandle_t[Constants.STEAM_INPUT_MAX_COUNT];
        private bool attempted, steamReady, inputReady;

        public void Update()
        {
            if (steamReady) SteamAPI.RunCallbacks();
        }

        public Texture2D Get(InputControl control)
        {
            if (control?.device is not Gamepad gamepad) return null;
            var button = XboxOrigin(control);
            if (button == EXboxOrigin.k_EXboxOrigin_Count) return null;
            if (!attempted)
            {
                attempted = true;
                try
                {
                    steamReady = SteamAPI.Init();
                    inputReady = steamReady && SteamInput.Init(false);
                }
                catch (Exception e) when (e is DllNotFoundException || e is EntryPointNotFoundException || e is BadImageFormatException)
                {
                    inputReady = false;
                }
            }
            if (!inputReady) return null;

            // The Xbox origin enums share the same button ordering.
            var origin = EInputActionOrigin.k_EInputActionOrigin_XBoxOne_A + (int)button;
            var type = gamepad switch
            {
                DualSenseGamepadHID _ => ESteamInputType.k_ESteamInputType_PS5Controller,
                DualShockGamepad _ => ESteamInputType.k_ESteamInputType_PS4Controller,
                SwitchProController _ => ESteamInputType.k_ESteamInputType_SwitchProController,
                _ => ESteamInputType.k_ESteamInputType_XBoxOneController
            };
            if (gamepad is XInputController && Gamepad.all.Count == 1 && SteamInput.GetConnectedControllers(handles) == 1 &&
                SteamInput.GetGamepadIndexForController(handles[0]) >= 0)
                origin = SteamInput.GetActionOriginFromXboxOrigin(handles[0], button);
            else if (type != ESteamInputType.k_ESteamInputType_XBoxOneController)
                origin = SteamInput.TranslateActionOrigin(type, origin);

            if (origin == EInputActionOrigin.k_EInputActionOrigin_None) return null;
            if (textures.TryGetValue(origin, out var texture)) return texture;
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
                if (texture != null) UnityEngine.Object.Destroy(texture);
                texture = null;
            }
            textures[origin] = texture;
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
            foreach (var texture in textures.Values)
                if (texture != null) UnityEngine.Object.Destroy(texture);
            textures.Clear();
            if (inputReady) SteamInput.Shutdown();
            if (steamReady) SteamAPI.Shutdown();
            steamReady = inputReady = false;
        }
    }
}
