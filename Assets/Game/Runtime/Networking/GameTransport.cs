using System;
using System.Collections.Generic;
using FishNet.Transporting;
using FishNet.Transporting.Tugboat;

namespace TwoBirds
{
    // Keeps development payload accounting outside imported transport code. -1 denotes the client socket.
    public sealed class GameTransport : Tugboat
    {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        public readonly Dictionary<int, (long sent, long received)> Traffic = new();
        private void Count(int peer, int sent, int received)
        {
            Traffic.TryGetValue(peer, out var value);
            Traffic[peer] = (value.sent + sent, value.received + received);
        }
        public override void SendToServer(byte channelId, ArraySegment<byte> segment)
        { Count(-1, segment.Count, 0); base.SendToServer(channelId, segment); }
        public override void SendToClient(byte channelId, ArraySegment<byte> segment, int connectionId)
        { Count(connectionId, segment.Count, 0); base.SendToClient(channelId, segment, connectionId); }
        public override void HandleServerReceivedDataArgs(ServerReceivedDataArgs args)
        { Count(args.ConnectionId, 0, args.Data.Count); base.HandleServerReceivedDataArgs(args); }
        public override void HandleClientReceivedDataArgs(ClientReceivedDataArgs args)
        { Count(-1, 0, args.Data.Count); base.HandleClientReceivedDataArgs(args); }
#endif
    }
}
