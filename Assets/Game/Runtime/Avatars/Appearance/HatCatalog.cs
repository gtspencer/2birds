using System.Collections.Generic;
using UnityEngine;

namespace TwoBirds
{
    [CreateAssetMenu(menuName = "Two Birds/Cosmetics/Hat Catalog")]
    public sealed class HatCatalog : ScriptableObject
    {
        public List<HatDefinition> Entries = new();

        private Dictionary<ulong, HatDefinition> lookup;
        public bool TryResolve(HatId id, out HatDefinition definition)
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
                    Debug.LogError($"Invalid or duplicate hat ID {entry.Id}.", this);
        }
    }
}
