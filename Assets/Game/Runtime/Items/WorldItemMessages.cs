using System.Collections.Generic;
using FishNet.Broadcast;
using UnityEngine;

namespace TwoBirds
{
    public enum WorldItemState : byte { World, Held, Removed }

    public struct ItemMotion
    {
        public uint Id;
        public uint Revision;
        public uint Tick;
        public Vector3 Position;
        public Quaternion Rotation;
        public Vector3 Velocity;
        public Vector3 AngularVelocity;
    }

    public struct ItemRecord
    {
        public ItemMotion Motion;
        public byte DefinitionId;
        public WorldItemState State;
        public int Holder;
        public bool Equipped;
        public bool Sleeping;
        public int Releaser;
        public uint Operation;
        public uint LaunchTick;
    }

    public struct ItemBaselineRequest : IBroadcast { }
    public struct ItemBaselineStart : IBroadcast { public uint Epoch; }
    public struct ItemLifecycleBatch : IBroadcast
    {
        public uint Epoch;
        public List<ItemRecord> Items;
    }
    public struct ItemMotionBatch : IBroadcast
    {
        public uint Epoch;
        public List<ItemMotion> Items;
    }

    public struct ItemHit
    {
        public uint Id;
        public uint Source;
        public uint ServerTick;
        public uint PlayerTick;
        public Vector3 Impulse;
    }
}
