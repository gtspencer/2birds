using System;
using FishNet.Connection;
using FishNet.Managing.Timing;
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

    public enum ItemActionState : byte { Idle, Charging, Recovering }

    public struct ItemActionSnapshot
    {
        public ItemActionState State;
        public byte DefinitionId, StartedFraction, ReleaseArcProgress;
        public uint WorldId, Operation, ControlRevision, StartedTick;
        public ushort TransitionSequence;

        internal bool NewerThan(in ItemActionSnapshot other) => ControlRevision > other.ControlRevision ||
            ControlRevision == other.ControlRevision && (short)(TransitionSequence - other.TransitionSequence) > 0;
        internal bool MatchesRelease(uint worldId, uint operation) =>
            State == ItemActionState.Recovering && WorldId == worldId && Operation == operation;
    }

    public sealed class PlayerNetworkState : NetworkBehaviour
    {
        private readonly SyncVar<PublicPlayerState> state = new();
        private ItemActionSnapshot localAction, replicatedAction, pendingAction;
        private bool hasLocal, hasReplicated, hasPending;
        private ushort sequence;
        private PlayerSeating seating;
        private PlayerMotor motor;
        private PlayerCarry carry;
        public event Action ActionChanged;

        private void Awake()
        {
            seating = GetComponent<PlayerSeating>();
            motor = GetComponent<PlayerMotor>();
            carry = GetComponent<PlayerCarry>();
        }
        internal bool CanCharge => (!seating || seating.CanEquip) && (!carry || carry.Role == CarryRole.Free);
        public PublicPlayerState Snapshot => state.Value;
        public float Health => state.Value.Health;
        public ItemActionSnapshot ItemAction => IsOwner && hasLocal ? localAction : replicatedAction;
        public bool HasActionSnapshot => IsOwner ? hasLocal : hasReplicated;
        public bool IsChargingUse => CanCharge && ItemAction.State == ItemActionState.Charging;

        internal void Initialize(byte slot)
        {
            state.Value = new PublicPlayerState { SpawnSlot = slot, Revision = 1, Health = 100f };
            ResetLifetime();
        }

        internal double ActionAge(in ItemActionSnapshot action)
        {
            var now = TimeManager.GetPreciseTick(TickType.Tick);
            return Math.Max(0d, ((int)(now.Tick - action.StartedTick) + now.PercentAsDouble -
                new PreciseTick(action.StartedTick, action.StartedFraction).PercentAsDouble) * TimeManager.TickDelta);
        }

        private ItemActionSnapshot Create(ItemActionState kind, byte definition, uint id, uint operation, byte progress)
        {
            var tick = TimeManager.GetPreciseTick(TickType.Tick);
            return new ItemActionSnapshot { State = kind, DefinitionId = definition, WorldId = id, Operation = operation,
                ControlRevision = motor.ControlRevision, TransitionSequence = ++sequence,
                StartedTick = tick.Tick, StartedFraction = tick.PercentAsByte, ReleaseArcProgress = progress };
        }

        internal void BeginItemCharge(byte definition, uint id)
        {
            if (!IsOwner || !CanCharge) return;
            PublishLocal(Create(ItemActionState.Charging, definition, id, 0, 0));
        }

        internal void CancelItemCharge()
        {
            if (!IsOwner || ItemAction.State != ItemActionState.Charging) return;
            PublishLocal(Create(ItemActionState.Idle, 0, 0, 0, 0));
        }

        internal ItemActionSnapshot PredictRecovery(byte definition, uint id, uint operation, byte progress)
        {
            var action = Create(ItemActionState.Recovering, definition, id, operation, progress);
            ApplyLocal(action);
            return action;
        }

        internal void CompleteRecovery(uint id, uint operation)
        {
            if (!IsOwner || !ItemAction.MatchesRelease(id, operation)) return;
            PublishLocal(Create(ItemActionState.Idle, 0, id, operation, 0));
        }

        internal void ResetItemAction()
        {
            if (!IsOwner || !HasActionSnapshot || ItemAction.State == ItemActionState.Idle) return;
            PublishLocal(Create(ItemActionState.Idle, 0, 0, 0, 0));
        }

        private void PublishLocal(ItemActionSnapshot action)
        {
            ApplyLocal(action);
            if (!IsClientInitialized) return;
            if (IsServerInitialized) Store(action);
            else CmdItemAction(action);
        }

        private void ApplyLocal(ItemActionSnapshot action)
        {
            if (hasLocal && !action.NewerThan(localAction)) return;
            localAction = action;
            hasLocal = true;
            sequence = action.TransitionSequence;
            ActionChanged?.Invoke();
        }

        [ServerRpc(RequireOwnership = true)]
        private void CmdItemAction(ItemActionSnapshot action)
        {
            if (action.State == ItemActionState.Recovering || action.ControlRevision < motor.ControlRevision) return;
            if (action.ControlRevision > motor.ControlRevision) { Pend(action); return; }
            if (action.State == ItemActionState.Charging && !CanCharge) return;
            Store(action);
        }

        internal void AcceptRecovery(ItemActionSnapshot action) => Store(action);

        private void Store(ItemActionSnapshot action)
        {
            if (hasReplicated && !action.NewerThan(replicatedAction)) return;
            replicatedAction = action;
            hasReplicated = true;
            if (!IsOwner) ActionChanged?.Invoke();
            ObserversItemAction(action);
        }

        [ObserversRpc(BufferLast = true)]
        private void ObserversItemAction(ItemActionSnapshot action)
        {
            if (!IsServerInitialized) Receive(action);
        }

        private void Pend(ItemActionSnapshot action)
        {
            if (hasPending && !action.NewerThan(pendingAction)) return;
            pendingAction = action;
            hasPending = true;
        }

        private void Receive(ItemActionSnapshot action)
        {
            if (action.ControlRevision < motor.ControlRevision) return;
            if (action.ControlRevision > motor.ControlRevision || seating.AwaitingReference) { Pend(action); return; }
            if (hasReplicated && !action.NewerThan(replicatedAction)) return;
            replicatedAction = action;
            hasReplicated = true;
            if (IsOwner) ApplyLocal(action);
            else ActionChanged?.Invoke();
        }

        internal void ApplyControlState(ItemActionSnapshot action)
        {
            action.ControlRevision = motor.ControlRevision;
            if (IsServerInitialized) Store(action);
            else Receive(action);
            if (IsOwner) ApplyLocal(action);
            if (!hasPending || pendingAction.ControlRevision > motor.ControlRevision) return;
            var pending = pendingAction;
            hasPending = false;
            if (pending.ControlRevision != motor.ControlRevision) return;
            if (IsServerInitialized)
            {
                if (pending.State != ItemActionState.Charging || CanCharge) Store(pending);
            }
            else Receive(pending);
        }

        private void ResetLifetime(bool publish = true)
        {
            hasLocal = hasReplicated = hasPending = false;
            localAction = replicatedAction = pendingAction = default;
            sequence = 0;
            ClearBuffedRpcs();
            if (publish && IsServerInitialized) Store(new ItemActionSnapshot { ControlRevision = motor.ControlRevision });
            if (publish && IsOwner) ApplyLocal(new ItemActionSnapshot { ControlRevision = motor.ControlRevision });
            ActionChanged?.Invoke();
        }

        public override void OnStartServer() => ResetLifetime();
        public override void OnStartClient()
        {
            if (IsOwner && !hasLocal) ApplyLocal(new ItemActionSnapshot { ControlRevision = motor.ControlRevision });
        }
        public override void OnOwnershipServer(NetworkConnection previousOwner) => ResetLifetime();
        public override void OnOwnershipClient(NetworkConnection previousOwner) => ResetLifetime();
        public override void OnStopNetwork() => ResetLifetime(false);
    }
}
