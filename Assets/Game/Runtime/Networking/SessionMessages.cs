using FishNet.Broadcast;

namespace TwoBirds
{
    public struct LobbyMember
    {
        public int Connection;
        public string Name;
        public bool Host, Ready;
    }

    public struct SessionAdmission : IBroadcast
    {
        public uint Attempt, Session;
        public bool Starting;
    }

    public struct LobbyRoster : IBroadcast
    {
        public uint Session;
        public LobbyMember[] Members;
    }

    public struct SessionStarting : IBroadcast { public uint Session; }
    public struct SessionReady : IBroadcast { public uint Session; }
    public struct SessionRejected : IBroadcast { public uint Attempt; public string Reason; }
}
