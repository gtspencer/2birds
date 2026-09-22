using FishNet.Connection;
using FishNet.Transporting;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace TwoBirds
{
    public sealed partial class WorldItemRegistry
    {
        internal PebbleRegistry Pebbles { get; private set; }
        internal Scene WorldScene => worldScene;
        internal int LocalConnection => network.ClientManager.Connection.ClientId;
        internal uint AllocateRuntimeId() => runtimeIds.Allocate();
        internal bool Simulates(int simulator) => simulator < 0 ? IsHost :
            network.IsClientStarted && LocalConnection == simulator;

        private void RegisterPebbles()
        {
            Pebbles = new PebbleRegistry(this);
            network.ClientManager.RegisterBroadcast<PebbleSpawn>(ReceivePebbleSpawn);
            network.ClientManager.RegisterBroadcast<PebbleTransition>(ReceivePebbleTransition);
            network.ServerManager.RegisterBroadcast<PebbleTransition>(AcceptPebbleTransition);
            Physics.ContactModifyEvent += ItemContactPhysics.Modify;
            Physics.ContactModifyEventCCD += ItemContactPhysics.ModifyCcd;
        }

        private void UnregisterPebbles()
        {
            network.ClientManager.UnregisterBroadcast<PebbleSpawn>(ReceivePebbleSpawn);
            network.ClientManager.UnregisterBroadcast<PebbleTransition>(ReceivePebbleTransition);
            network.ServerManager.UnregisterBroadcast<PebbleTransition>(AcceptPebbleTransition);
            Physics.ContactModifyEvent -= ItemContactPhysics.Modify;
            Physics.ContactModifyEventCCD -= ItemContactPhysics.ModifyCcd;
        }
        private void ReceivePebbleSpawn(PebbleSpawn message, Channel channel)
        {
            if (!IsHost && worldReady && message.Epoch == epoch) Pebbles.Receive(message);
        }
        private void ReceivePebbleTransition(PebbleTransition message, Channel channel)
        {
            if (!IsHost && worldReady && message.Epoch == epoch) Pebbles.Receive(message);
        }
        private void AcceptPebbleTransition(NetworkConnection sender, PebbleTransition message, Channel channel)
        {
            if (worldReady && message.Epoch == epoch) Pebbles.AcceptTransition(message, sender.ClientId);
        }
        internal void PublishPebble(PebbleSpawn message, NetworkConnection target = null)
        {
            if (target != null) network.ServerManager.Broadcast(target, message);
            else network.ServerManager.Broadcast(observers, message);
        }
        internal void PublishPebble(PebbleTransition message) => network.ServerManager.Broadcast(observers, message);
        internal void SendPebble(PebbleTransition message) => network.ClientManager.Broadcast(message);
        internal void QueueMotion(ItemMotion motion)
        {
            motionBatch.Add(motion);
            if (motionBatch.Count == BatchSize) FlushMotion();
        }
    }
}
