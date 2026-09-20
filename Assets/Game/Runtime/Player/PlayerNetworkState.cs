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
        private PlayerMotor motor;
        private PlayerCarry carry;
        private void Awake()
        {
            seating = GetComponent<PlayerSeating>();
            motor = GetComponent<PlayerMotor>();
            carry = GetComponent<PlayerCarry>();
        }
        private bool CanCharge => (!seating || seating.CanEquip) && (!carry || carry.Role == CarryRole.Free);
        public PublicPlayerState Snapshot => state.Value;
        public float Health => state.Value.Health;
        public bool IsChargingUse => CanCharge && (IsOwner ? localChargingUse : replicatedChargingUse);

        internal void Initialize(byte slot)
        {
            state.Value = new PublicPlayerState { SpawnSlot = slot, Revision = 1, Health = 100f };
            ResetChargingUse();
        }

        internal void SetChargingUse(bool value)
        {
            if (value && !CanCharge) return;
            if (localChargingUse == value) return;
            localChargingUse = value;
            if (!IsOwner || !IsClientInitialized) return;
            if (IsServerInitialized) StoreChargingUse(value);
            else CmdChargingUse(value, motor.ControlRevision);
        }

        [ServerRpc(RequireOwnership = true)]
        private void CmdChargingUse(bool value, uint revision)
        {
            if (revision != motor.ControlRevision || value && !CanCharge) return;
            StoreChargingUse(value);
        }

        private void StoreChargingUse(bool value)
        {
            if (replicatedChargingUse == value) return;
            replicatedChargingUse = value;
            ObserversChargingUse(value, motor.ControlRevision);
        }

        [ObserversRpc(BufferLast = true)]
        private void ObserversChargingUse(bool value, uint revision)
        {
            if (revision != motor.ControlRevision || value && !CanCharge) return;
            if (!IsOwner && !IsServerInitialized) replicatedChargingUse = value;
        }

        private void ResetChargingUse()
        {
            localChargingUse = false;
            replicatedChargingUse = false;
            ClearBuffedRpcs();
            if (IsServerInitialized) ObserversChargingUse(false, motor.ControlRevision);
        }

        internal void ApplyControlState(bool chargingUse)
        {
            ResetChargingUse();
            replicatedChargingUse = chargingUse && CanCharge;
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
