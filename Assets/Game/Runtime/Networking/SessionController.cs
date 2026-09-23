using System;
using System.Collections;
using System.Collections.Generic;
using FishNet.Connection;
using FishNet.Managing;
using FishNet.Managing.Scened;
using FishNet.Transporting;
using FishNet.Transporting.Multipass;
using UnityEngine;
using UnityEngine.SceneManagement;
using SceneManager = UnityEngine.SceneManagement.SceneManager;

namespace TwoBirds
{
    public enum SessionMode { Solo, Host, Join }
    public enum SessionPhase { Idle, StartingServer, Connecting, InLobby, LoadingGame, InGame, Stopping }

    public sealed class SessionController : MonoBehaviour
    {
        public const int MultiplayerCapacity = 8;
        public const string GameId = "two-birds";
        public const string Protocol = "eight-player-birds-8";
        public const float DefaultMouseSensitivity = 0.12f;
        public const float DefaultControllerSensitivity = 150f;
        public static SessionController Instance { get; private set; }
        [SerializeField] private GameSettings settings;
        [SerializeField] private AvatarRegistry avatarRegistry;
        [SerializeField] private HatCatalog hatCatalog;
        [SerializeField] private TattooCatalog tattooCatalog;
        [SerializeField] private AvatarEditorController avatarEditorPrefab;
        public AvatarRegistry Avatars => avatarRegistry;
        public AvatarRegistry PresentationRegistryOverride { get; set; }
        public AvatarRegistry PresentationRegistry => PresentationRegistryOverride ? PresentationRegistryOverride : avatarRegistry;
        public string GameplayScene { get; private set; } = "Game";
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        private bool startingAuthoring;
        public void StartGripAuthoring()
        {
            if (Phase != SessionPhase.Idle || !Network || startingAuthoring) return;
            GripAuthoringSession.Begin(this);
            GameplayScene = GripAuthoringSession.SceneName;
            startingAuthoring = true;
            StartCoroutine(StartAuthoringSession());
        }
        private IEnumerator StartAuthoringSession()
        {
            var existing = SceneManager.GetSceneByName(GripAuthoringSession.SceneName);
            if (existing.IsValid() && existing.isLoaded)
            {
                var entry = SceneManager.CreateScene("GripAuthoringEntry");
                SceneManager.SetActiveScene(entry);
                yield return SceneManager.UnloadSceneAsync(existing);
            }
            try { StartSession(SessionMode.Solo); }
            finally { startingAuthoring = false; }
            if (Phase == SessionPhase.Idle) { GripAuthoringSession.End(); GameplayScene = "Game"; }
        }
#endif
        public HatCatalog Hats => hatCatalog;
        public TattooCatalog Tattoos => tattooCatalog;
        public AvatarAppearanceStore Appearance { get; private set; }
        public CosmeticUnlockService Unlocks { get; private set; }
        public AvatarEditorController AvatarEditor { get; private set; }
        public bool EditorOpen { get; private set; }
        [SerializeField] private Multipass multipass;
        [SerializeField] private GameTransport localTransport;
        [SerializeField] private GameSteamTransport steamTransport;
        public NetworkManager Network { get; private set; }
        public SteamLobby Lobby { get; private set; }
        public InputBindings Bindings { get; private set; }
        public InputPresentation InputPresentation { get; private set; }
        internal string MenuPage = "main", MenuSelection = "solo";
        internal string HostPort = "7770", JoinAddress = "127.0.0.1", JoinPort = "7770";
        internal string MenuFocus = "";
        internal ulong FriendSelection;
        public SessionMode Mode { get; private set; }
        public SessionPhase Phase { get; private set; }
        public string Status { get; private set; } = "";
        public string ShareEndpoint { get; private set; } = "";
        public uint SessionId { get; private set; }
        public bool PanelOpen { get; private set; }
        public bool ConsoleOpen { get; private set; }
        public PlayerMotor LocalPlayer { get; private set; }
        public LobbyMember[] Roster { get; private set; } = Array.Empty<LobbyMember>();
        public bool LocalNetworking => SessionBootstrap.LocalNetworking;
        public int Capacity => Mode == SessionMode.Solo ? 1 : MultiplayerCapacity;
        public int FrameCap { get; private set; }
        public FullScreenMode DisplayMode { get; private set; }
        public float MouseSensitivity { get; private set; }
        public float ControllerSensitivity { get; private set; }
        public bool CanStart => Mode == SessionMode.Host && Phase == SessionPhase.InLobby && admitted.Count > 0 && (UsingLocal || Lobby.HasLobby);
        public event Action Changed;
        internal int Attempt => attempt;
        internal int SelectedTransport => selectedIndex;
        internal string DisplayName => UsingLocal ? "" : steam.DisplayName;
        private bool UsingLocal => LocalNetworking || Mode == SessionMode.Solo;
        private readonly Dictionary<NetworkConnection, LobbyMember> admitted = new();
        private readonly Dictionary<NetworkConnection, uint> clientAttempts = new();
        private SteamLifetime steam;
        private SessionAuthenticator authenticator;
        private PlayerInputReader localInput;
        private Transport selectedTransport;
        private LocalConnectionState clientState = LocalConnectionState.Stopped;
        private float deadline;
        private bool sceneLoading, gameStarting, worldReady, birdsReady, quitting;
        private int attempt, selectedIndex = -1;
        private uint wireSession;
        private ulong pendingInvite, joiningLobby;
#if UNITY_INCLUDE_INSTRUMENTATION
        internal Dictionary<int, (long sent, long received)> PayloadTraffic =>
            UsingLocal ? localTransport.Traffic : steamTransport.Traffic;
        internal Transport DiagnosticTransport => selectedTransport;
        internal string DiagnosticEndpoint => UsingLocal
            ? $"{localTransport.GetClientAddress()}:{localTransport.GetPort()}"
            : $"Steam host {Lobby.HostId} (IP unavailable)";

        internal void SetConsoleOpen(bool open)
        {
            ConsoleOpen = open;
            RefreshGameplay();
        }
#endif

        private void Awake()
        {
            if (Instance && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            Bindings = new InputBindings(UnityEngine.InputSystem.InputSystem.actions);
            InputPresentation = new InputPresentation(Bindings);
            InputPresentation.Interrupted += InputInterrupted;
            Appearance = new AvatarAppearanceStore(avatarRegistry, hatCatalog, tattooCatalog);
            Unlocks = new CosmeticUnlockService(avatarRegistry, hatCatalog);
            AvatarEditor = Instantiate(avatarEditorPrefab, transform);
            AvatarEditor.Initialize(this);
            DontDestroyOnLoad(gameObject);
            Application.runInBackground = true;
            MouseSensitivity = PlayerPrefs.GetFloat("MouseSensitivity", DefaultMouseSensitivity);
            ControllerSensitivity = PlayerPrefs.GetFloat("ControllerSensitivity", DefaultControllerSensitivity);
        }

        private void Start()
        {
            Network = GetComponent<NetworkManager>();
            steam = GetComponent<SteamLifetime>();
            Lobby = GetComponent<SteamLobby>();
            authenticator = GetComponent<SessionAuthenticator>();
            Network.ServerManager.OnServerConnectionState += ServerState;
            Network.ClientManager.OnClientConnectionState += ClientState;
            Network.ServerManager.OnAuthenticationResult += Authenticated;
            Network.ServerManager.OnRemoteConnectionState += RemoteState;
            Network.ClientManager.RegisterBroadcast<SessionAdmission>(ReceiveAdmission);
            Network.ClientManager.RegisterBroadcast<LobbyRoster>(ReceiveRoster);
            Network.ClientManager.RegisterBroadcast<SessionStarting>(ReceiveStarting);
            Network.ClientManager.RegisterBroadcast<SessionRejected>(ReceiveRejection);
            Network.ServerManager.RegisterBroadcast<SessionReady>(ReceiveReady);
            Network.SceneManager.OnQueueStart += QueueStarted;
            Network.SceneManager.OnQueueEnd += QueueEnded;
            SetFrameCap(PlayerPrefs.GetInt("RenderingFrameCap", 60));
            SetDisplayMode(PlayerPrefs.GetInt("DisplayMode", (int)FullScreenMode.ExclusiveFullScreen), false);
#if UNITY_INCLUDE_INSTRUMENTATION
            if (!Application.isBatchMode) {
                gameObject.AddComponent<DevConsole>();
            }
#endif
        }

        public void SetFrameCap(int value)
        {
            FrameCap = value is 90 or 120 ? value : 60;
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = FrameCap;
            if (Network)
            {
                Network.ServerManager.SetFrameRate((ushort)FrameCap);
                Network.ClientManager.SetFrameRate((ushort)FrameCap);
            }
            PlayerPrefs.SetInt("RenderingFrameCap", FrameCap);
            PlayerPrefs.Save();
        }

        public void SetMouseSensitivity(float value)
        {
            MouseSensitivity = Mathf.Clamp(value, 0.01f, 0.5f);
            PlayerPrefs.SetFloat("MouseSensitivity", MouseSensitivity);
            PlayerPrefs.Save();
        }

        public void SetControllerSensitivity(float value)
        {
            ControllerSensitivity = Mathf.Clamp(value, 50f, 300f);
            PlayerPrefs.SetFloat("ControllerSensitivity", ControllerSensitivity);
            PlayerPrefs.Save();
        }

        public void ResetSensitivity()
        {
            SetMouseSensitivity(DefaultMouseSensitivity);
            SetControllerSensitivity(DefaultControllerSensitivity);
        }

        public void SetDisplayMode(int value) => SetDisplayMode(value switch
        {
            1 => FullScreenMode.Windowed,
            2 => FullScreenMode.FullScreenWindow,
            _ => FullScreenMode.ExclusiveFullScreen
        }, true);

        private void SetDisplayMode(int value, bool save)
        {
            SetDisplayMode(value switch
            {
                (int)FullScreenMode.Windowed => FullScreenMode.Windowed,
                (int)FullScreenMode.FullScreenWindow => FullScreenMode.FullScreenWindow,
                _ => FullScreenMode.ExclusiveFullScreen
            }, save);
        }

        private void SetDisplayMode(FullScreenMode mode, bool save)
        {
            DisplayMode = mode;
            Screen.fullScreenMode = DisplayMode;
            if (save)
            {
                PlayerPrefs.SetInt("DisplayMode", (int)DisplayMode);
                PlayerPrefs.Save();
            }
        }

        public static string DisplayModeLabel(FullScreenMode mode) => mode switch
        {
            FullScreenMode.Windowed => "Windowed",
            FullScreenMode.FullScreenWindow => "Windowed Fullscreen",
            _ => "Fullscreen"
        };

        private bool BeginAttempt(SessionMode mode)
        {
            if (Phase != SessionPhase.Idle || !Network) return false;
            Mode = mode;
            if (!multipass || !localTransport || !steamTransport || Network.TransportManager.Transport != multipass || !Lobby || !steam)
            { SetPhase(Phase, "Configure SessionRoot's Steam lifetime, lobby, Multipass, and transport references."); return false; }
            if (!UsingLocal && !steam.Ready) { SetPhase(Phase, steam.Failure); return false; }
            selectedTransport = UsingLocal ? localTransport : steamTransport;
            selectedIndex = -1;
            for (int i = 0; i < multipass.Transports.Count; i++)
                if (multipass.Transports[i] == selectedTransport) selectedIndex = i;
            if (selectedIndex < 0) { SetPhase(Phase, "Add both transports to Multipass on SessionRoot."); return false; }
            ++attempt;
            ++SessionId;
            wireSession = 0;
            admitted.Clear();
            clientAttempts.Clear();
            Roster = Array.Empty<LobbyMember>();
            LocalPlayer = null;
            localInput = null;
            worldReady = birdsReady = gameStarting = PanelOpen = false;
            ShareEndpoint = "";
            clientState = LocalConnectionState.Stopped;
            multipass.GlobalServerActions = false;
            multipass.SetClientTransport(selectedIndex);
            selectedTransport.SetMaximumClients(UsingLocal ? Capacity : Capacity - 1);
            deadline = Time.unscaledTime + settings.ConnectTimeout;
            return true;
        }

        public void StartSession(SessionMode mode, string address = "127.0.0.1", string portText = "7770")
        {
            if (Phase != SessionPhase.Idle) return;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (!startingAuthoring) GameplayScene = "Game";
#endif
            bool local = LocalNetworking || mode == SessionMode.Solo;
            ushort port = 0;
            if (local && !EndpointUtility.TryPort(portText, out port))
            { SetPhase(Phase, "Enter a port from 1 to 65535."); return; }
            if (local && mode == SessionMode.Join && !EndpointUtility.TryAddress(address, out address))
            { SetPhase(Phase, "Enter a valid IPv4 host address."); return; }
            if (!local && mode == SessionMode.Join) { SetPhase(Phase, "Choose a friend's lobby or accept a Steam invitation."); return; }
            if (!BeginAttempt(mode)) return;
            if (local)
            {
                localTransport.SetServerBindAddress(mode == SessionMode.Solo ? "127.0.0.1" : "0.0.0.0", IPAddressType.IPv4);
                localTransport.SetServerBindAddress("", IPAddressType.IPv6);
                localTransport.SetClientAddress(mode == SessionMode.Join ? address : "127.0.0.1");
                localTransport.SetPort(mode == SessionMode.Solo ? (ushort)0 : port);
                localTransport.SetTimeout(settings.ConnectTimeout, true);
                localTransport.SetTimeout(settings.ConnectTimeout, false);
                var lan = EndpointUtility.Discover();
                ShareEndpoint = mode == SessionMode.Host ? $"{(lan.Count > 0 ? lan[0].Address : "127.0.0.1")}:{port}" : "";
            }
            if (mode == SessionMode.Join) ConnectClient();
            else
            {
                wireSession = (uint)UnityEngine.Random.Range(1, int.MaxValue);
                authenticator.BeginServer();
                SetPhase(SessionPhase.StartingServer, "Starting server…");
                if (!multipass.StartConnection(true, selectedIndex)) Leave("Unable to start the selected server transport.");
            }
        }

        public void JoinSteamLobby(ulong lobby)
        {
            if (LocalNetworking || lobby == 0) return;
            if (pendingInvite == lobby || Phase != SessionPhase.Stopping && Phase != SessionPhase.Idle &&
                (joiningLobby == lobby || Lobby && Lobby.CurrentLobby == lobby)) return;
            pendingInvite = lobby;
            if (Phase != SessionPhase.Idle) Leave();
        }

        internal bool IsAttempt(int value) => attempt == value && Phase != SessionPhase.Idle && Phase != SessionPhase.Stopping;
        internal void LobbyCreated(int value)
        {
            if (IsAttempt(value)) SetPhase(SessionPhase.InLobby, "Waiting for the host to start.");
        }
        internal void SteamJoined(int value, ulong host)
        {
            if (!IsAttempt(value)) return;
            steamTransport.SetClientAddress(host.ToString());
            ConnectClient();
        }

        private void ConnectClient()
        {
            deadline = Time.unscaledTime + settings.ConnectTimeout;
            SetPhase(SessionPhase.Connecting, "Connecting to game host…");
            if (!Network.ClientManager.StartConnection()) Leave("Unable to start the client transport.");
        }

        private void ServerState(ServerConnectionStateArgs args)
        {
            if (args.TransportIndex != selectedIndex || Phase is SessionPhase.Stopping or SessionPhase.Idle) return;
            if (args.ConnectionState == LocalConnectionState.Started && Phase == SessionPhase.StartingServer)
            {
                if (UsingLocal) localTransport.SetPort(localTransport.GetPort());
                ConnectClient();
            }
            else if (args.ConnectionState == LocalConnectionState.Stopped)
                Leave("Server stopped or could not start the selected transport.");
        }

        private void ClientState(ClientConnectionStateArgs args)
        {
            if (args.TransportIndex != selectedIndex) return;
            clientState = args.ConnectionState;
            if (Phase is SessionPhase.Stopping or SessionPhase.Idle) return;
            if (args.ConnectionState == LocalConnectionState.Stopped)
                Leave(Phase is SessionPhase.InLobby or SessionPhase.InGame or SessionPhase.LoadingGame ?
                    "Connection to the host was lost. The session has ended." : "Game connection failed; the host may be unavailable or full.");
        }

        internal string Admit(NetworkConnection connection, SessionAuthenticator.Hello hello, bool isHost)
        {
            if (Phase is SessionPhase.Idle or SessionPhase.Stopping || connection.TransportIndex != selectedIndex) return "Session is closing.";
            if (hello.Game != GameId || hello.Protocol != Protocol) return "Incompatible game version.";
            if (admitted.Count >= Capacity) return "Session is full.";
            if (!isHost && (admitted.Count == 0 || Mode != SessionMode.Host)) return "Host is not ready to accept guests.";
            string name = UsingLocal ? $"Player {connection.ClientId + 1}" : hello.Name;
            if (string.IsNullOrWhiteSpace(name)) name = "Player";
            if (name.Length > 32) name = name.Substring(0, 32);
            admitted.Add(connection, new LobbyMember { Connection = connection.ClientId, Name = name, Host = isHost });
            clientAttempts.Add(connection, hello.Attempt);
            return null;
        }

        private void Authenticated(NetworkConnection connection, bool accepted)
        {
            if (!accepted || !clientAttempts.TryGetValue(connection, out uint clientAttempt)) return;
            Network.ServerManager.Broadcast(connection, new SessionAdmission { Attempt = clientAttempt, Session = wireSession, Starting = gameStarting || Mode == SessionMode.Solo });
            PublishRoster();
            if (!admitted[connection].Host) return;
            if (Mode == SessionMode.Solo) StartGame();
            else if (!UsingLocal)
            {
                deadline = Time.unscaledTime + settings.ConnectTimeout;
                Lobby.Create(attempt);
            }
        }

        private void ReceiveAdmission(SessionAdmission message, Channel channel)
        {
            if (Phase != SessionPhase.Connecting || message.Attempt != SessionId) return;
            wireSession = message.Session;
            if (message.Starting) BeginLoading();
            else if (Mode == SessionMode.Host && !UsingLocal && !Lobby.HasLobby)
                SetPhase(SessionPhase.Connecting, "Creating Steam lobby…");
            else SetPhase(SessionPhase.InLobby, "Waiting for the host to start.");
        }

        private void ReceiveRoster(LobbyRoster message, Channel channel)
        {
            if (message.Session != wireSession || Phase is SessionPhase.Idle or SessionPhase.Stopping) return;
            Roster = message.Members;
            Changed?.Invoke();
        }

        private void ReceiveRejection(SessionRejected message, Channel channel)
        {
            if (message.Attempt == SessionId && Phase == SessionPhase.Connecting) Leave(message.Reason);
        }

        public void StartGame()
        {
            if (Mode == SessionMode.Join || gameStarting || admitted.Count == 0 || Phase is SessionPhase.Stopping or SessionPhase.Idle) return;
            if (!UsingLocal && !Lobby.HasLobby) return;
            gameStarting = true;
            Network.ServerManager.Broadcast(new SessionStarting { Session = wireSession });
            BeginLoading();
            UpdateLobby();
            Network.SceneManager.LoadGlobalScenes(new SceneLoadData(GameplayScene) { ReplaceScenes = ReplaceOption.All });
        }

        private void ReceiveStarting(SessionStarting message, Channel channel)
        {
            if (message.Session == wireSession && Phase is SessionPhase.InLobby or SessionPhase.Connecting) BeginLoading();
        }

        private void BeginLoading()
        {
            AvatarEditor.ForceCommit();
            if (Phase == SessionPhase.LoadingGame) return;
            deadline = Time.unscaledTime + settings.LoadTimeout;
            SetPhase(SessionPhase.LoadingGame, "Loading game…");
            TryEnterGame();
        }

        private void QueueStarted() { AvatarEditor.ForceCommit(); sceneLoading = true; }
        private void QueueEnded() => sceneLoading = false;

        public void PlayerReady(PlayerMotor player)
        {
            if (Phase != SessionPhase.LoadingGame) return;
            LocalPlayer = player;
            localInput = player.GetComponent<PlayerInputReader>();
            TryEnterGame();
        }

        internal void WorldReady(uint session, uint epoch)
        {
            if (session != SessionId || epoch == 0 || Phase != SessionPhase.LoadingGame) return;
            worldReady = true;
            TryEnterGame();
        }

        internal void BirdsReady(uint session, uint epoch)
        {
            if (session != SessionId || epoch == 0 || Phase != SessionPhase.LoadingGame) return;
            birdsReady = true;
            TryEnterGame();
        }

        private void TryEnterGame()
        {
            if (Phase != SessionPhase.LoadingGame || !LocalPlayer || !worldReady || !birdsReady) return;
            SetPhase(SessionPhase.InGame, "");
            RefreshGameplay();
            Network.ClientManager.Broadcast(new SessionReady { Session = wireSession });
            UpdateLobby();
        }

        private void ReceiveReady(NetworkConnection connection, SessionReady message, Channel channel)
        {
            if (message.Session != wireSession || !admitted.TryGetValue(connection, out var member) || member.Ready) return;
            member.Ready = true;
            admitted[connection] = member;
            PublishRoster();
        }

        private void RemoteState(NetworkConnection connection, RemoteConnectionStateArgs args)
        {
            if (args.ConnectionState != RemoteConnectionState.Stopped || !admitted.Remove(connection, out var member)) return;
            clientAttempts.Remove(connection);
            if (Phase == SessionPhase.Stopping) return;
            if (member.Host) Leave("The host left. The session has ended.");
            else PublishRoster();
        }

        private void PublishRoster()
        {
            var members = new LobbyMember[admitted.Count];
            admitted.Values.CopyTo(members, 0);
            Network.ServerManager.Broadcast(new LobbyRoster { Session = wireSession, Members = members });
            UpdateLobby();
        }

        internal void UpdateLobby()
        {
            if (!UsingLocal && Mode == SessionMode.Host)
                Lobby.Publish(Phase == SessionPhase.Stopping ? "closing" : Phase == SessionPhase.LoadingGame ? "loading" : gameStarting ? "playing" : "lobby", admitted.Count < Capacity);
            Changed?.Invoke();
        }

        public void SetPanel(bool open)
        {
            if (PanelOpen == open) return;
            PanelOpen = open;
            RefreshGameplay();
            Changed?.Invoke();
        }

        private void InputInterrupted()
        {
            if (localInput) localInput.ClearContext();
            if (EditorOpen) return;
            if (Phase == SessionPhase.InGame) SetPanel(true);
        }

        internal void SetEditorOpen(bool open)
        {
            if (EditorOpen == open) return;
            EditorOpen = open;
            InputPresentation.RequireNeutral();
            RefreshGameplay();
            Changed?.Invoke();
        }

        private void RefreshGameplay()
        {
            bool available = Phase == SessionPhase.InGame && !PanelOpen && !ConsoleOpen && !EditorOpen;
            if (localInput) localInput.SetGameplay(available);
            else InputPresentation.SetGameplay(false);
        }

        private void Update()
        {
            if (pendingInvite != 0 && Phase == SessionPhase.Idle && Network)
            {
                ulong invite = pendingInvite;
                pendingInvite = 0;
                if (BeginAttempt(SessionMode.Join))
                {
                    joiningLobby = invite;
                    SetPhase(SessionPhase.Connecting, "Joining Steam lobby…");
                    Lobby.Join(invite, attempt);
                }
            }
            if (Phase is SessionPhase.StartingServer or SessionPhase.Connecting or SessionPhase.LoadingGame)
                if (Time.unscaledTime > deadline) Leave($"{Status.TrimEnd('…', '.')} timed out.");
        }

        public void Leave(string message = "")
        {
            AvatarEditor.ForceCommit();
            if (Phase is SessionPhase.Idle or SessionPhase.Stopping) return;
            int stoppingAttempt = ++attempt;
            joiningLobby = 0;
            SetPanel(true);
            worldReady = birdsReady = false;
            SetPhase(SessionPhase.Stopping, message);
            UpdateLobby();
            Lobby.Leave();
            StartCoroutine(StopSession(stoppingAttempt));
        }

        private void StopNetwork()
        {
            if (!Network || !multipass || selectedIndex < 0) return;
            if (Mode != SessionMode.Join && Network.IsServerStarted)
                Network.ServerManager.SendDisconnectMessages(new List<NetworkConnection>(Network.ServerManager.Clients.Values), iterate: true);
            Network.ClientManager.StopConnection();
            if (Mode != SessionMode.Join) multipass.StopConnection(true, selectedIndex);
        }

        private IEnumerator StopSession(int stoppingAttempt)
        {
            yield return null;
            while (sceneLoading) yield return null;
            StopNetwork();
            while (clientState != LocalConnectionState.Stopped || multipass.GetConnectionState(true, selectedIndex) != LocalConnectionState.Stopped)
                yield return null;
            yield return null;
            LocalPlayer = null;
            localInput = null;
            admitted.Clear();
            clientAttempts.Clear();
            Roster = Array.Empty<LobbyMember>();
            wireSession = 0;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            GripAuthoringSession.End();
#endif
            GameplayScene = "Game";
            PresentationRegistryOverride = null;
            yield return SceneManager.LoadSceneAsync("MainMenu", LoadSceneMode.Single);
            if (attempt != stoppingAttempt) yield break;
            PanelOpen = false;
            SetPhase(SessionPhase.Idle, Status);
        }

        private void SetPhase(SessionPhase phase, string message)
        {
            Phase = phase;
            Status = message;
            if (phase != SessionPhase.InGame) InputPresentation.SetGameplay(false);
            Changed?.Invoke();
        }

        internal void ShutdownPlatform(bool saveDraft = true)
        {
            if (quitting) return;
            if (saveDraft && AvatarEditor) AvatarEditor.ForceCommit();
            quitting = true;
            ++attempt;
            Phase = SessionPhase.Stopping;
            if (Lobby) Lobby.Leave();
            StopNetwork();
            if (Lobby) Lobby.Shutdown();
            if (steamTransport) steamTransport.Shutdown();
            if (steam) steam.Shutdown();
        }

        private void OnApplicationQuit() => ShutdownPlatform();
        private void OnDestroy()
        {
            if (Instance != this) return;
            InputPresentation?.Dispose();
            ShutdownPlatform(false);
            Instance = null;
            if (!Network) return;
            Network.ServerManager.OnServerConnectionState -= ServerState;
            Network.ClientManager.OnClientConnectionState -= ClientState;
            Network.ServerManager.OnAuthenticationResult -= Authenticated;
            Network.ServerManager.OnRemoteConnectionState -= RemoteState;
            Network.ClientManager.UnregisterBroadcast<SessionAdmission>(ReceiveAdmission);
            Network.ClientManager.UnregisterBroadcast<LobbyRoster>(ReceiveRoster);
            Network.ClientManager.UnregisterBroadcast<SessionStarting>(ReceiveStarting);
            Network.ClientManager.UnregisterBroadcast<SessionRejected>(ReceiveRejection);
            Network.ServerManager.UnregisterBroadcast<SessionReady>(ReceiveReady);
            Network.SceneManager.OnQueueStart -= QueueStarted;
            Network.SceneManager.OnQueueEnd -= QueueEnded;
        }
    }
}
