using System.Collections.Generic;

namespace TwoBirds
{
    internal sealed class BirdRewardLedger
    {
        internal sealed class Release { public uint Kills; public float Expires = float.PositiveInfinity; }
        private readonly Dictionary<(uint rock, uint player, uint operation), Release> releases = new();
        private readonly Dictionary<uint, (int balance, uint revision)> balances = new();
        private readonly List<(uint rock, uint player, uint operation)> cleanup = new();
        private float nextPrune;

        public void Accept(uint rock, uint player, uint operation)
        {
            var key = (rock, player, operation);
            if (!releases.ContainsKey(key)) releases.Add(key, new Release());
        }
        public void Complete(uint rock, uint player, uint operation, float now)
        {
            if (releases.TryGetValue((rock, player, operation), out var release)) release.Expires = now + 3f;
        }
        public bool Contains(uint rock, uint player, uint operation) => releases.ContainsKey((rock, player, operation));
        public uint Kill(uint rock, uint player, uint operation) => ++releases[(rock, player, operation)].Kills;
        public BirdReward Credit(uint player, int reward, int bonus, uint kills)
        {
            balances.TryGetValue(player, out var account);
            account.balance += reward + bonus; account.revision++;
            balances[player] = account;
            return new BirdReward { Balance = account.balance, Revision = account.revision, Reward = reward, Bonus = bonus, Kills = kills, Notify = true };
        }
        public BirdReward Balance(uint player)
        {
            balances.TryGetValue(player, out var account);
            return new BirdReward { Balance = account.balance, Revision = account.revision };
        }
        public void Prune(float now)
        {
            if (now < nextPrune) return;
            nextPrune = now + 1f; cleanup.Clear();
            foreach (var pair in releases) if (pair.Value.Expires < now) cleanup.Add(pair.Key);
            foreach (var key in cleanup) releases.Remove(key);
        }
        public void Clear() { releases.Clear(); balances.Clear(); cleanup.Clear(); nextPrune = 0f; }
    }
}
