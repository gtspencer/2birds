using System;
using System.Collections.Generic;
using UnityEngine;

namespace TwoBirds
{
    [CreateAssetMenu(menuName = "Two Birds/Item Catalog")]
    public sealed class ItemCatalog : ScriptableObject
    {
        public ItemDefinition[] Definitions = Array.Empty<ItemDefinition>();
        public void Validate()
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in Definitions)
                if (item == null || string.IsNullOrWhiteSpace(item.DefinitionId) || item.MaximumStack < 1 || !ids.Add(item.DefinitionId))
                    throw new InvalidOperationException("Invalid or duplicate item definition in " + name);
        }
        public ItemDefinition Resolve(string id)
        {
            foreach (var item in Definitions)
                if (item != null && item.DefinitionId == id) return item;
            return null;
        }
    }
}
