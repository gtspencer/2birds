using System;
using UnityEngine;

namespace TwoBirds
{
    [Serializable]
    public struct HatId : IEquatable<HatId>
    {
        [SerializeField] private uint high, low;
        public ulong Value => ((ulong)high << 32) | low;
        public bool IsValid => Value != 0;
        public HatId(ulong value) { high = (uint)(value >> 32); low = (uint)value; }
        public bool Equals(HatId other) => Value == other.Value;
        public override bool Equals(object obj) => obj is HatId other && Equals(other);
        public override int GetHashCode() => Value.GetHashCode();
        public override string ToString() => Value.ToString("x16");
        public static bool operator ==(HatId a, HatId b) => a.Equals(b);
        public static bool operator !=(HatId a, HatId b) => !a.Equals(b);
    }
}
