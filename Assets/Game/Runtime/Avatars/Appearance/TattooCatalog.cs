using System.Collections.Generic;
using UnityEngine;

namespace TwoBirds
{
    [CreateAssetMenu(menuName = "Two Birds/Cosmetics/Tattoo Catalog")]
    public sealed class TattooCatalog : ScriptableObject
    {
        public List<TattooDefinition> Entries = new();
        public Material DecalMaterial;
        private Dictionary<ulong, TattooDefinition> lookup;
        public bool TryResolve(TattooId id, out TattooDefinition definition)
        {
            if (lookup == null)
            {
                lookup = new();
                foreach (var entry in Entries)
                    if (entry && entry.Id.IsValid) lookup.TryAdd(entry.Id.Value, entry);
            }
            return lookup.TryGetValue(id.Value, out definition) && definition;
        }
        private void OnValidate()
        {
            lookup = null;
            var seen = new HashSet<ulong>();
            foreach (var entry in Entries)
                if (entry && (!entry.Id.IsValid || !seen.Add(entry.Id.Value)))
                    Debug.LogError($"Invalid or duplicate tattoo ID {entry.Id}.", this);
        }
    }
}
