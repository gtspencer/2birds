using System;
using UnityEngine;

namespace TwoBirds
{
    [CreateAssetMenu(menuName = "Two Birds/Cosmetics/Tattoo")]
    public sealed class TattooDefinition : ScriptableObject
    {
        public TattooId Id;
        public string DisplayName;
        public Sprite Icon;
        public Texture2D Artwork;
        [Min(0.01f)] public float AspectRatio = 1f;
        private void OnValidate()
        {
            if (!Id.IsValid) Id = new TattooId(BitConverter.ToUInt64(Guid.NewGuid().ToByteArray(), 0));
            if (Artwork) AspectRatio = (float)Artwork.width / Artwork.height;
        }
    }
}
