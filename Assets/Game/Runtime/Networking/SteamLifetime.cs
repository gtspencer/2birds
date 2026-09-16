using System;
using Steamworks;
using UnityEngine;

namespace TwoBirds
{
    [DefaultExecutionOrder(-200)]
    public sealed class SteamLifetime : MonoBehaviour
    {
        public static SteamLifetime Instance { get; private set; }
        public bool Ready { get; private set; }
        public bool InputReady { get; private set; }
        public string Failure { get; private set; } = "Steam is unavailable. Start Steam and restart Two Birds.";
        public string DisplayName => Ready ? SteamFriends.GetPersonaName() : "";
        public ulong UserId => Ready ? SteamUser.GetSteamID().m_SteamID : 0;

        private void Awake()
        {
            if (Instance && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            if (SessionBootstrap.LocalNetworking) return;
            try
            {
                Ready = SteamAPI.Init();
                InputReady = Ready && SteamInput.Init(false);
            }
            catch (Exception e) when (e is DllNotFoundException || e is EntryPointNotFoundException || e is BadImageFormatException)
            {
                Failure = $"Steam initialization failed: {e.Message}";
            }
        }

        private void Update()
        {
            if (Ready) SteamAPI.RunCallbacks();
        }

        internal void Shutdown()
        {
            if (InputReady) SteamInput.Shutdown();
            if (Ready) SteamAPI.Shutdown();
            InputReady = Ready = false;
        }

        private void OnDestroy()
        {
            if (Instance != this) return;
            if (SessionController.Instance) SessionController.Instance.ShutdownPlatform();
            Shutdown();
            Instance = null;
        }
    }
}
