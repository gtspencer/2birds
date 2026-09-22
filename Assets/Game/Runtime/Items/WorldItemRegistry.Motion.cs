using FishNet.Connection;
using FishNet.Transporting;
using UnityEngine;

namespace TwoBirds
{
    public sealed partial class WorldItemRegistry
    {
        internal bool Simulates(ItemRecord record) => record.Simulator < 0 ? IsHost :
            network.IsClientStarted && network.ClientManager.Connection.ClientId == record.Simulator;

        internal bool ReleasePending(uint id) => pendingReleases.ContainsKey(id);

        private void ReceiveSimulatorMotion(NetworkConnection connection, ItemMotionBatch message, Channel channel)
        {
            if (!worldReady || message.Epoch != epoch) return;
            foreach (var motion in message.Items)
            {
                if (Pebbles.AcceptsMotion(motion, connection.ClientId))
                {
                    Pebbles.ReceiveMotion(motion, connection.ClientId);
                    motionBatch.Add(motion);
                    if (motionBatch.Count == BatchSize) FlushMotion(channel, connection);
                    continue;
                }
                if (!records.TryGetValue(motion.Id, out var record) || record.State != WorldItemState.World ||
                    record.Simulator != connection.ClientId || motion.Revision != record.Motion.Revision ||
                    !Newer(motion, record.Motion)) continue;
                if (motion.Removed || OutsideWorld(motion)) { Remove(motion.Id); continue; }
                ApplyMotion(motion);
                motionBatch.Add(motion);
                if (motionBatch.Count == BatchSize) FlushMotion(channel, connection);
            }
            FlushMotion(channel, connection);
        }

        internal bool OutsideWorld(ItemMotion motion) => !Finite(motion.Position) || !Finite(motion.Velocity) ||
            !Finite(motion.AngularVelocity) || motion.Position.y < settings.FallBoundary || !worldBounds.Contains(motion.Position);

        private void TakeOverMotion(int simulator)
        {
            Pebbles.TakeOver(simulator);
            cleanup.Clear();
            foreach (var record in records.Values)
                if (record.State == WorldItemState.World && record.Simulator == simulator) cleanup.Add(record.Motion.Id);
            foreach (uint id in cleanup)
            {
                var record = records[id];
                record.Simulator = -1;
                record.Motion.Revision++;
                record.Motion.Sequence = 0;
                record.Motion.Tick = ServerTick;
                records[id] = record;
                items[id].ApplyRecord(record);
                Publish(record);
            }
        }

        private void AfterTick()
        {
            if (!worldReady || Replaying) return;
            if (IsHost && departureDrops.Count > 0 && Time.unscaledTime >= nextDepartureDrop) PlaceDepartureDrops();
            bool snapshot = LocalTick % (uint)Mathf.Max(1, Mathf.RoundToInt((float)(1d / TickDelta) / snapshotRate)) == 0;
            cleanup.Clear();
            foreach (var item in items.Values)
            {
                if (!item || !item.Definition || !item.gameObject.activeSelf) continue;
                item.Tick();
                var record = item.Record;
                if (record.State != WorldItemState.World || !Simulates(record) || ReleasePending(record.Motion.Id) ||
                    !item.MotionAvailable) continue;
                var motion = item.Capture(ServerTick);
                motion.Removed = item.RemovalPending || OutsideWorld(motion);
                bool boundary = item.MotionBoundary || motion.Sleeping != record.Sleeping || motion.Removed;
                if (!boundary && (!snapshot || motion.Sleeping)) continue;
                motion.Sequence = record.Motion.Sequence + 1;
                motion.Path = record.Motion.Path + (boundary ? 1u : 0u);
                if (motion.Tick <= record.Motion.Tick) motion.Tick = record.Motion.Tick + 1;
                motion.Boundary = boundary;
                motion.RotationOmitted = !boundary && !item.Definition.SyncRotation;
                record.Motion = motion;
                record.Sleeping = motion.Sleeping;
                records[motion.Id] = record;
                item.SetRecord(record);
                item.MotionBoundary = false;
                if (motion.Removed && IsHost) { cleanup.Add(motion.Id); continue; }
                if (boundary) FlushMotion();
                motionBatch.Add(motion);
                if (boundary || motionBatch.Count == BatchSize) FlushMotion(boundary ? Channel.Reliable : Channel.Unreliable);
            }
            Pebbles.Tick(snapshot);
            FlushMotion();
            foreach (uint id in cleanup) Remove(id);
        }
    }
}
