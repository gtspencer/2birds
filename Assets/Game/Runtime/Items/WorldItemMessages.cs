using System.Collections.Generic;
using FishNet.Broadcast;
using FishNet.Serializing;
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
        [FishNet.CodeGenerating.ExcludeSerialization]
        public bool RotationOmitted;
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
        public uint BirdPlayer;
    }

    public struct ItemBaselineRequest : IBroadcast { public uint Session; }
    public struct ItemBaselineStart : IBroadcast { public uint Session, Epoch; }
    public struct ItemBaselineComplete : IBroadcast { public uint Session, Epoch; }
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

    public static class ItemMotionBatchSerializer
    {
        private const byte OmitRotation = 1, FullVelocity = 2, FullAngularVelocity = 4;

        public static void WriteItemMotionBatch(this Writer writer, ItemMotionBatch batch)
        {
            writer.WriteUInt32(batch.Epoch);
            writer.WriteInt32(batch.Items.Count);
            foreach (var motion in batch.Items)
            {
                byte flags = motion.RotationOmitted ? OmitRotation : (byte)0;
                if (!Fits(motion.Velocity)) flags |= FullVelocity;
                if (!motion.RotationOmitted && !Fits(motion.AngularVelocity)) flags |= FullAngularVelocity;
                writer.WriteUInt32(motion.Id);
                writer.WriteUInt32(motion.Revision);
                writer.WriteUInt32(motion.Tick);
                writer.WriteByte(flags);
                writer.WriteVector3(motion.Position);
                WriteVelocity(writer, motion.Velocity, (flags & FullVelocity) != 0);
                if (motion.RotationOmitted) continue;
                writer.WriteQuaternion32(motion.Rotation);
                WriteVelocity(writer, motion.AngularVelocity, (flags & FullAngularVelocity) != 0);
            }
        }

        public static ItemMotionBatch ReadItemMotionBatch(this Reader reader)
        {
            uint epoch = reader.ReadUInt32();
            int count = reader.ReadInt32();
            var items = new List<ItemMotion>(count);
            for (int i = 0; i < count; i++)
            {
                var motion = new ItemMotion { Id = reader.ReadUInt32(), Revision = reader.ReadUInt32(), Tick = reader.ReadUInt32() };
                byte flags = reader.ReadByte();
                motion.RotationOmitted = (flags & OmitRotation) != 0;
                motion.Position = reader.ReadVector3();
                motion.Velocity = ReadVelocity(reader, (flags & FullVelocity) != 0);
                if (!motion.RotationOmitted)
                {
                    motion.Rotation = reader.ReadQuaternion32();
                    motion.AngularVelocity = ReadVelocity(reader, (flags & FullAngularVelocity) != 0);
                }
                items.Add(motion);
            }
            return new ItemMotionBatch { Epoch = epoch, Items = items };
        }

        private static bool Fits(Vector3 value) =>
            value.x >= -327.68f && value.x <= 327.67f &&
            value.y >= -327.68f && value.y <= 327.67f &&
            value.z >= -327.68f && value.z <= 327.67f;

        private static void WriteVelocity(Writer writer, Vector3 value, bool full)
        {
            if (full) { writer.WriteVector3(value); return; }
            writer.WriteInt16((short)Mathf.RoundToInt(value.x * 100f));
            writer.WriteInt16((short)Mathf.RoundToInt(value.y * 100f));
            writer.WriteInt16((short)Mathf.RoundToInt(value.z * 100f));
        }

        private static Vector3 ReadVelocity(Reader reader, bool full) => full ? reader.ReadVector3() :
            new Vector3(reader.ReadInt16() * 0.01f, reader.ReadInt16() * 0.01f, reader.ReadInt16() * 0.01f);
    }
}
