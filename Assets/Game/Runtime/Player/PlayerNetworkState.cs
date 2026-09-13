using FishNet.Object;
using FishNet.Object.Synchronizing;

namespace TwoBirds
{
    public struct PublicPlayerState
    {
        public byte SpawnSlot;
        public uint Revision;
    }

    // Only persistent fields consumed by this MVP belong here. Movement state is ticked in PlayerMotor.
    public sealed class PlayerNetworkState : NetworkBehaviour
    {
        private readonly SyncVar<PublicPlayerState> state = new();
        public PublicPlayerState Snapshot => state.Value;
        internal void Initialize(byte slot) => state.Value = new PublicPlayerState { SpawnSlot = slot, Revision = 1 };
    }
}
