using FishNet.Connection;
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
        private bool localChargingUse;
        private bool replicatedChargingUse;
        public PublicPlayerState Snapshot => state.Value;
        public float Health => state.Value.Health;
        public bool IsChargingUse => IsOwner ? localChargingUse : replicatedChargingUse;

        internal void Initialize(byte slot)
        {
            state.Value = new PublicPlayerState { SpawnSlot = slot, Revision = 1, Health = 100f };
            ResetChargingUse();
        }

        internal void SetChargingUse(bool value)
        {
            if (localChargingUse == value) return;
            localChargingUse = value;
            if (!IsOwner || !IsClientInitialized) return;
            if (IsServerInitialized) StoreChargingUse(value);
            else CmdChargingUse(value);
        }

        [ServerRpc(RequireOwnership = true)]
        private void CmdChargingUse(bool value) => StoreChargingUse(value);

        private void StoreChargingUse(bool value)
        {
            if (replicatedChargingUse == value) return;
            replicatedChargingUse = value;
            ObserversChargingUse(value);
        }

        [ObserversRpc(BufferLast = true)]
        private void ObserversChargingUse(bool value)
        {
            if (!IsOwner && !IsServerInitialized) replicatedChargingUse = value;
        }

        private void ResetChargingUse()
        {
            localChargingUse = false;
            replicatedChargingUse = false;
            ClearBuffedRpcs();
            if (IsServerInitialized) ObserversChargingUse(false);
        }

        public override void OnStartServer() => ResetChargingUse();
        public override void OnOwnershipServer(NetworkConnection previousOwner) => ResetChargingUse();
        public override void OnOwnershipClient(NetworkConnection previousOwner) => localChargingUse = false;
        public override void OnStopClient() => localChargingUse = false;
        public override void OnStopServer()
        {
            localChargingUse = false;
            replicatedChargingUse = false;
            ClearBuffedRpcs();
        }
        public override void OnStopNetwork()
        {
            localChargingUse = false;
            replicatedChargingUse = false;
        }
    }
}
