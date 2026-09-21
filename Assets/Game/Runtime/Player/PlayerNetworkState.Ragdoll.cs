using FishNet.Managing.Timing;
using FishNet.Object;
using FishNet.Transporting;
using UnityEngine;

namespace TwoBirds
{
    public struct PlayerRagdollRoot
    {
        public uint Lifetime, Revision, Sequence, Tick;
        public Vector3 Position;
        public bool Settled;
    }

    public sealed partial class PlayerNetworkState
    {
        private PlayerRagdollRoot retainedRoot, pendingRoot;
        private bool hasPendingRoot, rootSettled;
        private uint rootSequence;
        private Vector3 sentRoot;
        private float nextRootSend;

        private void ResetRoot(PlayerLifeSnapshot snapshot)
        {
            retainedRoot = new PlayerRagdollRoot { Lifetime = snapshot.Lifetime, Revision = snapshot.Revision,
                Position = snapshot.State == PlayerLifeState.Downed ? snapshot.Seed.Position : snapshot.Placement.Position };
            rootSequence = 0;
            sentRoot = retainedRoot.Position;
            rootSettled = false;
            nextRootSend = 0f;
        }
        internal void PublishRoot(Vector3 position, bool settled)
        {
            if (!IsOwner || !DownConfirmed || PredictionManager.IsReconciling || Time.unscaledTime < nextRootSend) return;
            bool terminal = settled && !rootSettled;
            if (settled && rootSettled) return;
            if (!settled) rootSettled = false;
            if (!terminal && (position - sentRoot).sqrMagnitude < 0.0025f) return;
            var root = new PlayerRagdollRoot { Lifetime = Lifetime, Revision = LifeRevision, Sequence = ++rootSequence,
                Tick = TimeManager.GetPreciseTick(TickType.Tick).Tick, Position = position, Settled = settled };
            sentRoot = position;
            rootSettled = settled;
            nextRootSend = Time.unscaledTime + 0.1f;
            var channel = terminal ? Channel.Reliable : Channel.Unreliable;
            if (IsServerInitialized) AcceptRoot(root, channel);
            else ServerRoot(root, channel);
        }
        [ServerRpc]
        private void ServerRoot(PlayerRagdollRoot root, Channel channel = Channel.Unreliable) => AcceptRoot(root, channel);
        private void AcceptRoot(PlayerRagdollRoot root, Channel channel)
        {
            if (root.Lifetime != Lifetime || root.Revision != LifeRevision || life.State != PlayerLifeState.Downed ||
                root.Sequence <= retainedRoot.Sequence) return;
            retainedRoot = root;
            if (!IsOwner) ragdoll.ReceiveRoot(root.Position, root.Settled);
            ObserversRoot(root, channel);
        }
        [ObserversRpc(ExcludeOwner = true)]
        private void ObserversRoot(PlayerRagdollRoot root, Channel channel = Channel.Unreliable)
        {
            if (!IsServerInitialized) ReceiveRoot(root);
        }
        private void ReceiveRoot(PlayerRagdollRoot root)
        {
            if (!baselineReady || root.Lifetime == Lifetime && root.Revision > LifeRevision)
            {
                if (!hasPendingRoot || root.Revision > pendingRoot.Revision || root.Revision == pendingRoot.Revision && root.Sequence > pendingRoot.Sequence)
                { pendingRoot = root; hasPendingRoot = true; }
                return;
            }
            if (root.Lifetime != Lifetime || root.Revision != LifeRevision || life.State != PlayerLifeState.Downed ||
                root.Sequence < retainedRoot.Sequence) return;
            retainedRoot = root;
            ragdoll.ReceiveRoot(root.Position, root.Settled);
        }
        private void ApplyPendingRoot()
        {
            if (!hasPendingRoot || pendingRoot.Revision > LifeRevision) return;
            var pending = pendingRoot;
            hasPendingRoot = false;
            ReceiveRoot(pending);
        }
    }
}
