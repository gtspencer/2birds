using FishNet.Object;
using FishNet.Object.Synchronizing;

namespace TwoBirds
{
    public struct PublicPlayerState
    {
        public byte SpawnSlot;
        public uint Revision;
        public float Health;
    }

    public sealed class PlayerNetworkState : NetworkBehaviour
    {
        private readonly SyncVar<PublicPlayerState> state = new();
        public PublicPlayerState Snapshot => state.Value;
        public float Health => state.Value.Health;
        internal void Initialize(byte slot) => state.Value = new PublicPlayerState { SpawnSlot = slot, Revision = 1, Health = 100f };
    }
}
