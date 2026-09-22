using System;
using UnityEngine;

namespace TwoBirds
{
    [Serializable]
    public struct TattooId : IEquatable<TattooId>
    {
        [SerializeField] private uint high, low;
        public ulong Value => ((ulong)high << 32) | low;
        public bool IsValid => Value != 0;
        public TattooId(ulong value) { high = (uint)(value >> 32); low = (uint)value; }
        public bool Equals(TattooId other) => Value == other.Value;
        public override bool Equals(object obj) => obj is TattooId other && Equals(other);
        public override int GetHashCode() => Value.GetHashCode();
        public override string ToString() => Value.ToString("x16");
        public static bool operator ==(TattooId a, TattooId b) => a.Equals(b);
        public static bool operator !=(TattooId a, TattooId b) => !a.Equals(b);
    }
}
