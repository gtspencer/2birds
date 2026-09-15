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
        private PlayerSeating seating;
        private void Awake() => seating = GetComponent<PlayerSeating>();
        public PublicPlayerState Snapshot => state.Value;
        public float Health => state.Value.Health;
        public bool IsChargingUse => (seating == null || seating.CanEquip) && (IsOwner ? localChargingUse : replicatedChargingUse);

        internal void Initialize(byte slot)
        {
            state.Value = new PublicPlayerState { SpawnSlot = slot, Revision = 1, Health = 100f };
            ResetChargingUse();
        }

        internal void SetChargingUse(bool value)
        {
            if (value && seating != null && !seating.CanEquip) return;
            if (localChargingUse == value) return;
            localChargingUse = value;
            if (!IsOwner || !IsClientInitialized) return;
            if (IsServerInitialized) StoreChargingUse(value);
            else CmdChargingUse(value, seating != null ? seating.Revision : 0);
        }

        [ServerRpc(RequireOwnership = true)]
        private void CmdChargingUse(bool value, uint revision)
        {
            if (seating != null && (revision != seating.Revision || value && !seating.CanEquip)) return;
            StoreChargingUse(value);
        }

        private void StoreChargingUse(bool value)
        {
            if (replicatedChargingUse == value) return;
            replicatedChargingUse = value;
            ObserversChargingUse(value, seating != null ? seating.Revision : 0);
        }

        [ObserversRpc(BufferLast = true)]
        private void ObserversChargingUse(bool value, uint revision)
        {
            if (seating != null && (revision != seating.Revision || value && !seating.CanEquip)) return;
            if (!IsOwner && !IsServerInitialized) replicatedChargingUse = value;
        }

        private void ResetChargingUse()
        {
            localChargingUse = false;
            replicatedChargingUse = false;
            ClearBuffedRpcs();
            if (IsServerInitialized) ObserversChargingUse(false, seating != null ? seating.Revision : 0);
        }

        internal void ClearChargingForSeat() => ResetChargingUse();

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
