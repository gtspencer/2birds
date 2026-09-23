using FishNet.Serializing;
using UnityEngine;

namespace TwoBirds
{
    public static class AvatarAppearanceSerializer
    {
        public static void WriteAvatarAppearance(this Writer writer, AvatarAppearance value)
        {
            value ??= new AvatarAppearance();
            writer.WriteAvatarId(value.Avatar);
            writer.WriteUInt64Unpacked(value.Hat.Value);
            writer.WriteUInt8Unpacked((byte)value.Tattoos.Length);
            foreach (var tattoo in value.Tattoos)
            {
                writer.WriteUInt64Unpacked(tattoo.Design.Value); writer.WriteUInt32(tattoo.Region);
                writer.WriteVector3(tattoo.Position); writer.WriteVector3(tattoo.Normal);
                writer.WriteSingle(tattoo.Rotation); writer.WriteSingle(tattoo.Size);
                writer.WriteUInt8Unpacked(tattoo.R); writer.WriteUInt8Unpacked(tattoo.G); writer.WriteUInt8Unpacked(tattoo.B);
            }
        }
        public static AvatarAppearance ReadAvatarAppearance(this Reader reader)
        {
            var result = new AvatarAppearance { Avatar = reader.ReadAvatarId(), Hat = new HatId(reader.ReadUInt64Unpacked()) };
            result.Tattoos = new TattooAppearance[reader.ReadUInt8Unpacked()];
            for (int i = 0; i < result.Tattoos.Length; i++) result.Tattoos[i] = new TattooAppearance
            {
                Design = new TattooId(reader.ReadUInt64Unpacked()), Region = reader.ReadUInt32(),
                Position = reader.ReadVector3(), Normal = reader.ReadVector3(), Rotation = reader.ReadSingle(), Size = reader.ReadSingle(),
                R = reader.ReadUInt8Unpacked(), G = reader.ReadUInt8Unpacked(), B = reader.ReadUInt8Unpacked()
            };
            return result;
        }
    }
}
