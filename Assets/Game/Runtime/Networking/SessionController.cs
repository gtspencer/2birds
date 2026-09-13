using System;
using System.Collections;
using FishNet.Managing;
using FishNet.Managing.Scened;
using FishNet.Transporting;
using FishNet.Transporting.Tugboat;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using SceneManager = UnityEngine.SceneManagement.SceneManager;

namespace TwoBirds
{
    public enum SessionMode { Solo, Host, Join }
    public enum SessionPhase { Idle, StartingServer, Connecting, LoadingGame, InGame, Stopping }

    public sealed class SessionController : MonoBehaviour
    {
        public static SessionController Instance { get; private set; }
        [SerializeField] private GameSettings settings;
        public NetworkManager Network { get; private set; }
        public SessionMode Mode { get; private set; }
        public SessionPhase Phase { get; private set; }
        public string Status { get; private set; } = "";
        public string ShareEndpoint { get; private set; } = "";
        public uint SessionId { get; private set; }
        public bool PanelOpen { get; private set; }
        public PlayerMotor LocalPlayer { get; private set; }
        public bool InventoryOpen { get; private set; }
        public bool ApplicationFocused { get; private set; } = true;
        public Func<bool> CancelInventoryDrag;
        public bool GameplayAllowed => Phase == SessionPhase.InGame && LocalPlayer != null &&
            LocalPlayer.IsOwner && ApplicationFocused && !PanelOpen && !InventoryOpen;
        public event Action Changed;
        private Tugboat transport;
        private float deadline;
        private bool sceneLoading;
        private int attempt;
        private InputAction uiNavigate, uiSubmit;

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);
            Application.runInBackground = true;
        }

        private void Start()
        {
            uiNavigate = InputSystem.actions.FindAction("UI/Navigate");
            uiSubmit = InputSystem.actions.FindAction("UI/Submit");
            Network = GetComponent<NetworkManager>();
            transport = GetComponent<Tugboat>();
            Network.ServerManager.OnServerConnectionState += ServerState;
            Network.ClientManager.OnClientConnectionState += ClientState;
            Network.SceneManager.OnQueueStart += QueueStarted;
            Network.SceneManager.OnQueueEnd += QueueEnded;
            Network.SceneManager.OnLoadEnd += LoadEnded;
        }

        public void StartSession(SessionMode mode, string address, string portText, string shareAddress = "")
        {
            if (Phase != SessionPhase.Idle || Network == null) return;
            if (!EndpointUtility.TryPort(portText, out ushort port)) { SetPhase(Phase, "Enter a port from 1 to 65535."); return; }
            if (mode == SessionMode.Join && !EndpointUtility.TryAddress(address, out address))
            { SetPhase(Phase, "Enter a valid IPv4 host address."); return; }
            ++attempt;
            ++SessionId;
            Mode = mode;
            LocalPlayer = null;
            PanelOpen = false;
            InventoryOpen = false;
            ShareEndpoint = mode == SessionMode.Host && !string.IsNullOrEmpty(shareAddress) ? $"{shareAddress}:{port}" : "";
            transport.SetServerBindAddress(mode == SessionMode.Solo ? "127.0.0.1" : "0.0.0.0", IPAddressType.IPv4);
            transport.SetServerBindAddress("", IPAddressType.IPv6);
            // IPv6 and socket reuse are disabled in the session prefab (Tugboat has no public IPv6 setter).
            transport.SetClientAddress(mode == SessionMode.Join ? address : "127.0.0.1");
            transport.SetPort(mode == SessionMode.Solo ? (ushort)0 : port);
            transport.SetMaximumClients(mode == SessionMode.Solo ? 1 : 2);
            transport.SetTimeout(settings.ConnectTimeout, true);
            transport.SetTimeout(settings.ConnectTimeout, false);
            deadline = Time.unscaledTime + settings.ConnectTimeout;
            if (mode == SessionMode.Join)
            {
                SetPhase(SessionPhase.Connecting, "Connecting…");
                if (!Network.ClientManager.StartConnection()) Leave("Unable to start the client.");
            }
            else
            {
                GetComponent<SessionAuthenticator>().BeginServer();
                SetPhase(SessionPhase.StartingServer, "Starting server…");
                if (!Network.ServerManager.StartConnection()) Leave("Unable to bind the server; check the port.");
            }
        }

        private void ServerState(ServerConnectionStateArgs args)
        {
            if (Phase == SessionPhase.Stopping || Phase == SessionPhase.Idle) return;
            if (args.ConnectionState == LocalConnectionState.Started && Phase == SessionPhase.StartingServer)
            {
                // While started, Tugboat.GetPort reads NetManager.LocalPort (including OS-assigned ports).
                int boundPort = transport.GetPort();
                transport.SetPort((ushort)boundPort);
                SetPhase(SessionPhase.Connecting, "Connecting local player…");
                Network.SceneManager.LoadGlobalScenes(new SceneLoadData("Game") { ReplaceScenes = ReplaceOption.All });
                if (!Network.ClientManager.StartConnection()) Leave("Unable to connect the local player.");
            }
            else if (args.ConnectionState == LocalConnectionState.Stopped)
                Leave("Server stopped or could not bind the selected port.");
        }

        private void ClientState(ClientConnectionStateArgs args)
        {
            if (Phase == SessionPhase.Stopping || Phase == SessionPhase.Idle) return;
            if (args.ConnectionState == LocalConnectionState.Started)
            {
                deadline = Time.unscaledTime + settings.LoadTimeout;
                SetPhase(SessionPhase.LoadingGame, "Loading game…");
            }
            else if (args.ConnectionState == LocalConnectionState.Stopped)
                Leave(Phase == SessionPhase.InGame ? "Connection lost. The session has ended." :
                    "Unable to connect; check the address, port, and available player slot.");
        }

        private void QueueStarted() => sceneLoading = true;
        private void QueueEnded() => sceneLoading = false;
        private void LoadEnded(SceneLoadEndEventArgs args) => sceneLoading = false;

        public void PlayerReady(PlayerMotor player)
        {
            if (Phase != SessionPhase.LoadingGame && Phase != SessionPhase.Connecting) return;
            LocalPlayer = player;
            SetPhase(SessionPhase.InGame, "");
        }

        public void SetPanel(bool open)
        {
            PanelOpen = open;
            if (open) { CancelInventoryDrag?.Invoke(); InventoryOpen = false; }
            RefreshInput();
            Changed?.Invoke();
        }

        public void SetInventory(bool open)
        {
            if (open && (Phase != SessionPhase.InGame || LocalPlayer == null || PanelOpen || !ApplicationFocused)) return;
            CancelInventoryDrag?.Invoke();
            InventoryOpen = open;
            RefreshInput();
            Changed?.Invoke();
        }
        public void CancelModal()
        {
            if (CancelInventoryDrag?.Invoke() == true) return;
            if (InventoryOpen) SetInventory(false);
            else if (Phase == SessionPhase.InGame) SetPanel(!PanelOpen);
            else Leave();
        }
        public void RefreshInput()
        {
            if (GameplayAllowed) { uiNavigate?.Disable(); uiSubmit?.Disable(); }
            else { uiNavigate?.Enable(); uiSubmit?.Enable(); }
            if (LocalPlayer != null) LocalPlayer.GetComponent<PlayerInputReader>().SetGameplay(GameplayAllowed);
        }
        public void PlayerGone(PlayerMotor player)
        {
            if (LocalPlayer != player) return;
            SetPanel(true);
            LocalPlayer = null;
            Changed?.Invoke();
        }
        private void OnApplicationFocus(bool focused)
        {
            ApplicationFocused = focused;
            if (!focused && Phase == SessionPhase.InGame) SetPanel(true);
            else RefreshInput();
        }

        private void Update()
        {
            if (Phase is SessionPhase.StartingServer or SessionPhase.Connecting or SessionPhase.LoadingGame)
                if (Time.unscaledTime > deadline) Leave(Phase == SessionPhase.LoadingGame ? "Game loading timed out." : "Connection timed out; check the address, port, and available player slot.");
        }

        public void Leave(string message = "")
        {
            if (Phase == SessionPhase.Idle || Phase == SessionPhase.Stopping) return;
            int stoppingAttempt = ++attempt;
            SetPanel(true);
            SetPhase(SessionPhase.Stopping, message);
            StartCoroutine(StopSession(stoppingAttempt));
        }

        private IEnumerator StopSession(int stoppingAttempt)
        {
            // Unity scene loads cannot be canceled. Let the in-flight operation settle before FishNet
            // clears its scene queue on server shutdown, then return to the menu exactly once.
            while (sceneLoading) yield return null;
            Network.ClientManager.StopConnection();
            if (Mode != SessionMode.Join) Network.ServerManager.StopConnection(true);
            // Do not permit another attempt until old socket callbacks AND queued scene work have drained.
            while (transport.GetConnectionState(false) != LocalConnectionState.Stopped ||
                   transport.GetConnectionState(true) != LocalConnectionState.Stopped)
                yield return null;
            yield return null;
            LocalPlayer = null;
            var load = SceneManager.LoadSceneAsync("MainMenu", LoadSceneMode.Single);
            yield return load;
            if (attempt != stoppingAttempt) yield break;
            PanelOpen = false;
            SetPhase(SessionPhase.Idle, Status);
        }

        private void SetPhase(SessionPhase phase, string message)
        {
            Phase = phase;
            Status = message;
            RefreshInput();
            Debug.Log($"Session {SessionId}: {Mode} {phase}. {message}");
            Changed?.Invoke();
        }

        private void OnDestroy()
        {
            if (Instance != this) return;
            Instance = null;
            if (Network == null) return;
            Network.ServerManager.OnServerConnectionState -= ServerState;
            Network.ClientManager.OnClientConnectionState -= ClientState;
            Network.SceneManager.OnQueueStart -= QueueStarted;
            Network.SceneManager.OnQueueEnd -= QueueEnded;
            Network.SceneManager.OnLoadEnd -= LoadEnded;
        }
    }
}
