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
        public struct Hello : IBroadcast { public string HostToken; }
        public override event Action<NetworkConnection, bool> OnAuthenticationResult;
        private string hostToken;
        private bool hostAdmitted;

        public void BeginServer()
        {
            hostToken = Guid.NewGuid().ToString("N");
            hostAdmitted = false;
        }

        public override void InitializeOnce(NetworkManager manager)
        {
            base.InitializeOnce(manager);
            manager.ServerManager.RegisterBroadcast<Hello>(Receive, false);
            manager.ClientManager.OnClientConnectionState += ClientState;
        }

        private void ClientState(ClientConnectionStateArgs args)
        {
            if (args.ConnectionState == LocalConnectionState.Started)
                NetworkManager.ClientManager.Broadcast(new Hello { HostToken = NetworkManager.IsServerStarted ? hostToken : "" });
        }

        private void Receive(NetworkConnection connection, Hello hello, Channel channel)
        {
            if (connection.IsAuthenticated) return;
            bool isHost = !string.IsNullOrEmpty(hostToken) && hello.HostToken == hostToken;
            bool accepted = isHost || (hostAdmitted && SessionController.Instance.Mode == SessionMode.Host);
            if (isHost) hostAdmitted = true;
            OnAuthenticationResult?.Invoke(connection, accepted);
        }

        private void OnDestroy()
        {
            if (NetworkManager == null) return;
            NetworkManager.ServerManager.UnregisterBroadcast<Hello>(Receive);
            NetworkManager.ClientManager.OnClientConnectionState -= ClientState;
        }
    }
}
