using System.Collections.Generic;
using FishNet.Serializing;
using UnityEngine;

namespace TwoBirds
{
    public enum SplatTargetKind : byte { Missing, Scene, Player, Cart, Item, Bird, Network }

    public struct SplatTarget
    {
        public SplatTargetKind Kind;
        public uint Id, Lifetime, Reset;
        public HumanBodyBones Bone;
        public string Path;
    }

    public struct SplatEvent
    {
        public uint Epoch, Item, Operation;
        public int Releaser;
        public byte Definition;
        public SplatTarget Target;
        public Vector3 Point;
        public Quaternion Rotation;
    }

    public static class SplatMessages
    {
        public static void WriteItemContactResult(this Writer writer, ItemContactResult value)
        {
            writer.WriteUInt32(value.Epoch); writer.WriteUInt32(value.Item);
            writer.WriteInt32(value.Releaser); writer.WriteUInt32(value.Operation);
            writer.WriteUInt8Unpacked((byte)((value.HasSplat ? 1 : 0) | (value.SplatAccepted ? 2 : 0)));
        }

        public static ItemContactResult ReadItemContactResult(this Reader reader)
        {
            var value = new ItemContactResult { Epoch = reader.ReadUInt32(), Item = reader.ReadUInt32(),
                Releaser = reader.ReadInt32(), Operation = reader.ReadUInt32() };
            byte flags = reader.ReadUInt8Unpacked();
            value.HasSplat = (flags & 1) != 0; value.SplatAccepted = (flags & 2) != 0;
            return value;
        }

        public static void WriteSplatTarget(this Writer writer, SplatTarget target)
        {
            writer.WriteUInt8Unpacked((byte)target.Kind);
            if (target.Kind == SplatTargetKind.Missing) return;
            if (target.Kind != SplatTargetKind.Scene) writer.WriteUInt32(target.Id);
            if (target.Kind is SplatTargetKind.Player or SplatTargetKind.Cart) writer.WriteUInt32(target.Lifetime);
            if (target.Kind == SplatTargetKind.Player)
            {
                writer.WriteUInt32(target.Reset);
                writer.WriteUInt8Unpacked((byte)target.Bone);
            }
            if (target.Kind is SplatTargetKind.Scene or SplatTargetKind.Network or SplatTargetKind.Cart) writer.WriteString(target.Path);
        }

        public static SplatTarget ReadSplatTarget(this Reader reader)
        {
            var target = new SplatTarget { Kind = (SplatTargetKind)reader.ReadUInt8Unpacked() };
            if (target.Kind == SplatTargetKind.Missing) return target;
            if (target.Kind != SplatTargetKind.Scene) target.Id = reader.ReadUInt32();
            if (target.Kind is SplatTargetKind.Player or SplatTargetKind.Cart) target.Lifetime = reader.ReadUInt32();
            if (target.Kind == SplatTargetKind.Player)
            {
                target.Reset = reader.ReadUInt32();
                target.Bone = (HumanBodyBones)reader.ReadUInt8Unpacked();
            }
            if (target.Kind is SplatTargetKind.Scene or SplatTargetKind.Network or SplatTargetKind.Cart) target.Path = reader.ReadString();
            return target;
        }

        public static void WriteSplatEvent(this Writer writer, SplatEvent value)
        {
            writer.WriteUInt32(value.Epoch); writer.WriteUInt32(value.Item); writer.WriteInt32(value.Releaser);
            writer.WriteUInt32(value.Operation); writer.WriteUInt8Unpacked(value.Definition);
            writer.WriteSplatTarget(value.Target); writer.WriteVector3(value.Point); writer.WriteQuaternion32(value.Rotation);
        }

        public static SplatEvent ReadSplatEvent(this Reader reader) => new()
        {
            Epoch = reader.ReadUInt32(), Item = reader.ReadUInt32(), Releaser = reader.ReadInt32(),
            Operation = reader.ReadUInt32(), Definition = reader.ReadUInt8Unpacked(),
            Target = reader.ReadSplatTarget(), Point = reader.ReadVector3(), Rotation = reader.ReadQuaternion32()
        };

        public static void WriteItemContact(this Writer writer, ItemContact value)
        {
            writer.WriteUInt32(value.Epoch); writer.WriteUInt32(value.Item); writer.WriteUInt32(value.Revision);
            writer.WriteInt32(value.Releaser); writer.WriteUInt32(value.Operation); writer.WriteInt32(value.Cauldron);
            writer.WriteUInt8Unpacked((byte)((value.Impact ? 1 : 0) | (value.HasSplat ? 2 : 0)));
            if (value.Impact)
            {
                writer.WriteInt32(value.Cart); writer.WriteUInt32(value.CartLifetime); writer.WriteVector3(value.Position);
            }
            if (value.HasSplat)
            {
                writer.WriteSplatTarget(value.Target); writer.WriteVector3(value.SplatPoint); writer.WriteQuaternion32(value.SplatRotation);
            }
        }

        public static ItemContact ReadItemContact(this Reader reader)
        {
            var value = new ItemContact { Epoch = reader.ReadUInt32(), Item = reader.ReadUInt32(), Revision = reader.ReadUInt32(),
                Releaser = reader.ReadInt32(), Operation = reader.ReadUInt32(), Cauldron = reader.ReadInt32(), Cart = -1 };
            byte flags = reader.ReadUInt8Unpacked();
            value.Impact = (flags & 1) != 0; value.HasSplat = (flags & 2) != 0;
            if (value.Impact)
            {
                value.Cart = reader.ReadInt32(); value.CartLifetime = reader.ReadUInt32(); value.Position = reader.ReadVector3();
            }
            if (value.HasSplat)
            {
                value.Target = reader.ReadSplatTarget(); value.SplatPoint = reader.ReadVector3(); value.SplatRotation = reader.ReadQuaternion32();
            }
            return value;
        }

        public static void WriteCraftingTransition(this Writer writer, CraftingTransition value)
        {
            writer.WriteUInt32(value.Epoch);
            writer.WriteUInt8Unpacked((byte)((value.Snapshot ? 1 : 0) | (value.HasCauldron ? 2 : 0) |
                (value.HasActivation ? 4 : 0) | (value.Blast ? 8 : 0) | (value.HasSplat ? 16 : 0)));
            writer.Write(value.Items);
            if (value.HasCauldron) writer.Write(value.Cauldron);
            if (value.HasActivation) writer.Write(value.Activation);
            if (value.HasSplat) writer.WriteSplatEvent(value.Splat);
        }

        public static CraftingTransition ReadCraftingTransition(this Reader reader)
        {
            uint epoch = reader.ReadUInt32();
            byte flags = reader.ReadUInt8Unpacked();
            var value = new CraftingTransition { Epoch = epoch, Snapshot = (flags & 1) != 0, HasCauldron = (flags & 2) != 0,
                HasActivation = (flags & 4) != 0, Blast = (flags & 8) != 0, HasSplat = (flags & 16) != 0,
                Items = reader.Read<List<ItemRecord>>() };
            if (value.HasCauldron) value.Cauldron = reader.Read<CauldronRecord>();
            if (value.HasActivation) value.Activation = reader.Read<PotionActivation>();
            if (value.HasSplat) value.Splat = reader.ReadSplatEvent();
            return value;
        }
    }
}
