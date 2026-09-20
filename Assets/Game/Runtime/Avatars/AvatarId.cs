using System;
using FishNet.Serializing;
using UnityEngine;

namespace TwoBirds
{
    [Serializable]
    public struct AvatarId : IEquatable<AvatarId>
    {
        [SerializeField] private uint high, low;
        public ulong Value => ((ulong)high << 32) | low;
        public bool IsValid => Value != 0;
        public AvatarId(ulong value) { high = (uint)(value >> 32); low = (uint)value; }
        public bool Equals(AvatarId other) => high == other.high && low == other.low;
        public override bool Equals(object obj) => obj is AvatarId other && Equals(other);
        public override int GetHashCode() => Value.GetHashCode();
        public override string ToString() => Value.ToString("x16");
        public static bool operator ==(AvatarId a, AvatarId b) => a.Equals(b);
        public static bool operator !=(AvatarId a, AvatarId b) => !a.Equals(b);
    }

    public static class AvatarIdSerializer
    {
        public static void WriteAvatarId(this Writer writer, AvatarId value) => writer.WriteUInt64Unpacked(value.Value);
        public static AvatarId ReadAvatarId(this Reader reader) => new(reader.ReadUInt64Unpacked());
    }
}
