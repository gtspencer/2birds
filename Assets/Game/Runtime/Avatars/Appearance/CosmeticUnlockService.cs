using System;
using System.Collections.Generic;
using UnityEngine;

namespace TwoBirds
{
    public sealed class CosmeticUnlockService
    {
        private readonly AvatarRegistry avatars;
        private readonly HatCatalog hats;
        private readonly HashSet<string> avatarGrants, hatGrants;
        public event Action Changed;
        public CosmeticUnlockService(AvatarRegistry avatars, HatCatalog hats)
        {
            this.avatars = avatars; this.hats = hats;
            avatarGrants = Load("AvatarUnlocks.v1"); hatGrants = Load("HatUnlocks.v1");
        }
        private static HashSet<string> Load(string key) => new(PlayerPrefs.GetString(key, "").Split(';'));
        public bool Available(AvatarId id) => avatars.TryResolve(id, out var entry) &&
            (entry.Settings.UnlockedByDefault || avatarGrants.Contains(id.ToString()));
        public bool Available(HatId id) => !id.IsValid || hats && hats.TryResolve(id, out var entry) &&
            (entry.UnlockedByDefault || hatGrants.Contains(id.ToString()));
        public void UnlockAvatar(AvatarId id)
        {
            if (!avatars.TryResolve(id, out _)) return;
            bool available = Available(id);
            Grant(avatarGrants, "AvatarUnlocks.v1", id.ToString(), available);
        }
        public void UnlockHat(HatId id)
        {
            if (!hats || !hats.TryResolve(id, out _)) return;
            bool available = Available(id);
            Grant(hatGrants, "HatUnlocks.v1", id.ToString(), available);
        }
        private void Grant(HashSet<string> grants, string key, string id, bool available)
        {
            if (!grants.Add(id)) return;
            PlayerPrefs.SetString(key, string.Join(";", grants)); PlayerPrefs.Save();
            if (!available) Changed?.Invoke();
        }
    }
}
