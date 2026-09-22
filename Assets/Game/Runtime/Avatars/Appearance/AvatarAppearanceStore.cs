using System;
using System.Collections.Generic;
using UnityEngine;

namespace TwoBirds
{
    public sealed class AvatarAppearanceStore
    {
        private const string Key = "AvatarAppearance.v1";
        private readonly AvatarRegistry avatars;
        private readonly HatCatalog hats;
        private readonly TattooCatalog tattoos;
        private AvatarAppearance committed;
        public AvatarAppearance Committed => committed.Clone();
        public event Action<AvatarAppearance> CommittedChanged;

        public AvatarAppearanceStore(AvatarRegistry avatars, HatCatalog hats, TattooCatalog tattoos)
        {
            this.avatars = avatars; this.hats = hats; this.tattoos = tattoos;
            AvatarAppearance saved = null;
            try { saved = JsonUtility.FromJson<AvatarAppearance>(PlayerPrefs.GetString(Key, "")); }
            catch (ArgumentException) { }
            committed = Resolve(saved);
        }

        public AvatarAppearance Resolve(AvatarAppearance value)
        {
            var result = value?.Clone() ?? new AvatarAppearance();
            if (!avatars.TryResolve(result.Avatar, out var entry))
            {
                entry = avatars.Entries.Find(e => e.Settings && e.Settings.UnlockedByDefault && avatars.TryResolve(e.Id, out _));
                if (entry == null) throw new InvalidOperationException("Register a default-unlocked avatar.");
                result.Avatar = entry.Id;
            }
            if (!hats || !hats.TryResolve(result.Hat, out _)) result.Hat = default;
            var valid = new List<TattooAppearance>();
            foreach (var tattoo in result.Tattoos)
                if (valid.Count < AvatarAppearance.MaximumTattoos && tattoos && tattoos.TryResolve(tattoo.Design, out _) &&
                    AvatarTattooPlacement.Resolve(entry.Settings.TattooRegions, tattoo, out _, out _)) valid.Add(tattoo);
            result.Tattoos = valid.ToArray();
            return result;
        }

        public void Commit(AvatarAppearance value)
        {
            var next = Resolve(value);
            if (committed.Equals(next)) return;
            committed = next;
            PlayerPrefs.SetString(Key, JsonUtility.ToJson(committed));
            PlayerPrefs.Save();
            CommittedChanged?.Invoke(committed.Clone());
        }
    }
}
