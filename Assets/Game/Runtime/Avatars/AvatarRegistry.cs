using System;
using System.Collections.Generic;
using UnityEngine;

namespace TwoBirds
{
    [CreateAssetMenu(menuName = "Two Birds/Avatar Registry")]
    public sealed class AvatarRegistry : ScriptableObject
    {
        [Serializable]
        public sealed class Entry
        {
            public AvatarId Id;
            public GameObject Source;
            public string SourceGuid;
            public GameObject Prefab;
            public AvatarSettings Settings;
        }

        public AvatarId DefaultId;
        public AvatarAnimationSet Animations;
        public List<Entry> Entries = new();
        private Dictionary<ulong, Entry> lookup;
        public event Action ContentChanged;

        public bool TryResolve(AvatarId id, out Entry entry)
        {
            if (lookup == null) BuildLookup();
            return lookup.TryGetValue(id.Value, out entry) && entry != null;
        }

        private void BuildLookup()
        {
            lookup = new Dictionary<ulong, Entry>(Entries.Count);
            var seen = new HashSet<ulong>();
            foreach (var entry in Entries)
            {
                if (entry == null) continue;
                if (!seen.Add(entry.Id.Value))
                {
                    lookup[entry.Id.Value] = null;
                    Debug.LogError($"Duplicate avatar ID {entry.Id} in {name}; repair the registry/settings.", this);
                    continue;
                }
                if (!entry.Id.IsValid || !entry.Settings || !entry.Prefab || !entry.Source ||
                    entry.Settings.Id != entry.Id || entry.Settings.Generated.SourceGuid != entry.SourceGuid ||
                    entry.Settings.Generated.Source != entry.Source)
                {
                    Debug.LogError($"Invalid avatar entry {entry.Id} in {name}; process its source again.", this);
                    continue;
                }
                lookup.Add(entry.Id.Value, entry);
            }
        }

        public void Invalidate() { lookup = null; ContentChanged?.Invoke(); }
        private void OnEnable() => lookup = null;
        private void OnValidate() => Invalidate();
    }
}
