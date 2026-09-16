using System;
using FishNet.Authenticating;
using FishNet.Broadcast;
using FishNet.Connection;
using FishNet.Managing;
using FishNet.Transporting;

namespace TwoBirds
{
    // This handshake reserves player identity for the in-process host; it is not an account login.
    public sealed class SessionAuthenticator : Authenticator
    {
        public struct Hello : IBroadcast { public string HostToken, Game, Protocol, Name; public uint Attempt; }
        public override event Action<NetworkConnection, bool> OnAuthenticationResult;
        private string hostToken;

        public void BeginServer()
        {
            hostToken = Guid.NewGuid().ToString("N");
        }

        public override void InitializeOnce(NetworkManager manager)
        {
            base.InitializeOnce(manager);
            manager.ServerManager.RegisterBroadcast<Hello>(Receive, false);
            manager.ClientManager.OnClientConnectionState += ClientState;
        }

        private void ClientState(ClientConnectionStateArgs args)
        {
            var session = SessionController.Instance;
            if (args.ConnectionState == LocalConnectionState.Started && args.TransportIndex == session.SelectedTransport)
                NetworkManager.ClientManager.Broadcast(new Hello { HostToken = NetworkManager.IsServerStarted ? hostToken : "",
                    Game = SessionController.GameId, Protocol = SessionController.Protocol, Name = session.DisplayName, Attempt = session.SessionId });
        }

        private void Receive(NetworkConnection connection, Hello hello, Channel channel)
        {
            if (connection.IsAuthenticated) return;
            bool isHost = !string.IsNullOrEmpty(hostToken) && hello.HostToken == hostToken;
            string rejection = SessionController.Instance.Admit(connection, hello, isHost);
            if (rejection != null)
                NetworkManager.ServerManager.Broadcast(connection, new SessionRejected { Attempt = hello.Attempt, Reason = rejection }, requireAuthenticated: false);
            OnAuthenticationResult?.Invoke(connection, rejection == null);
        }

        private void OnDestroy()
        {
            if (NetworkManager == null) return;
            NetworkManager.ServerManager.UnregisterBroadcast<Hello>(Receive);
            NetworkManager.ClientManager.OnClientConnectionState -= ClientState;
        }
    }
}
