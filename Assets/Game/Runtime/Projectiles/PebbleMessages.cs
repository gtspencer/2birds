using FishNet.Broadcast;
using FishNet.Serializing;
using UnityEngine;

namespace TwoBirds
{
    public enum PebbleEnd : byte { None, Impact, Lifetime, KillVolume, Bounds, Rejected }

    public struct PebbleRecord
    {
        public ItemMotion Motion;
        public byte Definition, Impact, LaunchFraction;
        public int Shooter, Simulator;
        public uint Lifetime, Weapon, Shot, BirdPlayer, LaunchTick;
        public double Expiry;
        public bool ShooterCleared, Touching;
        public Vector3 ContactPoint, ContactNormal;
    }

    public struct PebbleFire
    {
        public uint Epoch, Lifetime, Shot, Weapon;
        public ItemMotion Motion;
        public ItemActionSnapshot Action;
    }

    public struct PebbleSpawn : IBroadcast
    {
        public uint Epoch;
        public bool Accepted, Baseline;
        public PebbleRecord Record;
    }

    public struct PebbleTransition : IBroadcast
    {
        public uint Epoch;
        public PebbleRecord Record;
        public PebbleEnd End;
        public bool HasBird;
        public BirdHitReport Bird;
    }

    public static class PebbleSerializers
    {
        public static void WritePebbleTransition(this Writer writer, PebbleTransition value)
        {
            writer.WriteUInt32(value.Epoch);
            writer.WritePebbleRecord(value.Record);
            writer.WriteUInt8Unpacked((byte)value.End);
            writer.WriteBoolean(value.HasBird);
            if (value.HasBird) writer.WriteBirdHitReport(value.Bird);
        }
        public static PebbleTransition ReadPebbleTransition(this Reader reader)
        {
            var value = new PebbleTransition { Epoch = reader.ReadUInt32(), Record = reader.ReadPebbleRecord(),
                End = (PebbleEnd)reader.ReadUInt8Unpacked(), HasBird = reader.ReadBoolean() };
            if (value.HasBird) value.Bird = reader.ReadBirdHitReport();
            return value;
        }
        public static void WritePebbleRecord(this Writer writer, PebbleRecord value)
        {
            writer.WritePackedMotion(value.Motion);
            writer.WriteUInt8Unpacked(value.Definition); writer.WriteUInt8Unpacked(value.Impact);
            writer.WriteUInt8Unpacked(value.LaunchFraction);
            writer.WriteInt32(value.Shooter); writer.WriteInt32(value.Simulator);
            writer.WriteUInt32(value.Lifetime); writer.WriteUInt32(value.Weapon); writer.WriteUInt32(value.Shot);
            writer.WriteUInt32(value.BirdPlayer); writer.WriteUInt32(value.LaunchTick);
            writer.WriteDouble(value.Expiry);
            writer.WriteBoolean(value.ShooterCleared); writer.WriteBoolean(value.Touching);
            if (value.Impact == 0) return;
            writer.WriteVector3(value.ContactPoint); writer.WriteVector3(value.ContactNormal);
        }
        public static PebbleRecord ReadPebbleRecord(this Reader reader)
        {
            var value = new PebbleRecord { Motion = reader.ReadPackedMotion(), Definition = reader.ReadUInt8Unpacked(),
                Impact = reader.ReadUInt8Unpacked(), LaunchFraction = reader.ReadUInt8Unpacked(),
                Shooter = reader.ReadInt32(), Simulator = reader.ReadInt32(), Lifetime = reader.ReadUInt32(),
                Weapon = reader.ReadUInt32(), Shot = reader.ReadUInt32(), BirdPlayer = reader.ReadUInt32(), LaunchTick = reader.ReadUInt32(),
                Expiry = reader.ReadDouble(), ShooterCleared = reader.ReadBoolean(), Touching = reader.ReadBoolean() };
            if (value.Impact > 0) { value.ContactPoint = reader.ReadVector3(); value.ContactNormal = reader.ReadVector3(); }
            return value;
        }
    }
}
