using System.Collections.Generic;
using UnityEngine;

namespace TwoBirds
{
    [CreateAssetMenu(menuName = "Two Birds/Emote Catalog")]
    public sealed class EmoteCatalog : ScriptableObject
    {
        public const int Capacity = 8;
        public List<EmoteDefinition> Entries = new();

        public EmoteDefinition Get(int index)
        {
            if (index < 0 || index >= Capacity || index >= Entries.Count) return null;
            var entry = Entries[index];
            return entry && entry.Clip && entry.Clip.length > 0f ? entry : null;
        }
        private void OnValidate()
        {
            if (Entries.Count > Capacity) Debug.LogError($"Emote catalog holds at most {Capacity} entries.", this);
        }
    }
}
