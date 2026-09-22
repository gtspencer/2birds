using System;
using UnityEngine;

namespace TwoBirds
{
    [Serializable]
    public struct TattooAppearance : IEquatable<TattooAppearance>
    {
        public TattooId Design;
        public uint Region;
        public Vector3 Position, Normal;
        public float Rotation, Size;
        public byte R, G, B;
        public Color Ink => new Color32(R, G, B, 255);
        public bool Equals(TattooAppearance other) => Design == other.Design && Region == other.Region &&
            Position.Equals(other.Position) && Normal.Equals(other.Normal) && Rotation.Equals(other.Rotation) &&
            Size.Equals(other.Size) && R == other.R && G == other.G && B == other.B;
    }

    [Serializable]
    public sealed class AvatarAppearance : IEquatable<AvatarAppearance>
    {
        public const int MaximumTattoos = 8;
        public AvatarId Avatar;
        public HatId Hat;
        public TattooAppearance[] Tattoos = Array.Empty<TattooAppearance>();
        public AvatarAppearance Clone() => new() { Avatar = Avatar, Hat = Hat,
            Tattoos = Tattoos == null ? Array.Empty<TattooAppearance>() : (TattooAppearance[])Tattoos.Clone() };
        public bool Equals(AvatarAppearance other)
        {
            if (other == null || Avatar != other.Avatar || Hat != other.Hat || Tattoos.Length != other.Tattoos.Length) return false;
            for (int i = 0; i < Tattoos.Length; i++) if (!Tattoos[i].Equals(other.Tattoos[i])) return false;
            return true;
        }
        public override bool Equals(object obj) => obj is AvatarAppearance other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(Avatar, Hat, Tattoos.Length);
    }
}
