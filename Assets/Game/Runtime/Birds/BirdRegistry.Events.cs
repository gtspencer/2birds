using System;
using System.Collections.Generic;
using FishNet.Connection;
using FishNet.Transporting;
using UnityEngine;

namespace TwoBirds
{
    public sealed partial class BirdRegistry
    {
        private readonly BirdRewardLedger ledger = new();
        private readonly Dictionary<int, uint> playerTokens = new();
        private readonly Dictionary<uint, PlayerInventory> tokenPlayers = new();
        private readonly Dictionary<(int cart, uint epoch, uint revision), (uint player, float expires)> cartDrivers = new();
        private readonly List<(int cart, uint epoch, uint revision)> oldDrivers = new();
        private readonly Dictionary<uint, Vector3> waitingThreats = new();
        private uint nextPlayerToken, rewardRevision;
        internal BirdHitReporter hitReporter;
        public int LocalBalance { get; private set; }
        public event Action<BirdReward> RewardChanged;

        internal uint PlayerToken(int objectId) => playerTokens.TryGetValue(objectId, out uint token) ? token : 0;
        internal void RegisterPlayer(PlayerInventory player)
        {
            if (!active || !Host) return;
            if (!playerTokens.TryGetValue(player.ObjectId, out uint token))
            {
                token = ++nextPlayerToken; playerTokens.Add(player.ObjectId, token); tokenPlayers.Add(token, player);
                var message = new BirdPlayerToken { Epoch = epoch, ObjectId = player.ObjectId, Token = token };
                network.ServerManager.Broadcast(observers, message);
                foreach (var transfer in transfers) network.ServerManager.Broadcast(transfer.Connection, message);
            }
        }
        internal void UnregisterPlayer(PlayerInventory player)
        {
            if (!playerTokens.Remove(player.ObjectId, out uint token)) return;
            tokenPlayers.Remove(token);
        }
        private void ReceivePlayerToken(BirdPlayerToken message, Channel channel)
        {
            if (Host || !active || message.Epoch != epoch) return;
            playerTokens[message.ObjectId] = message.Token;
        }
        internal void AcceptRelease(ItemRecord record)
        {
            if (active && Host && IsRock(WorldItemRegistry.Instance.GetDefinition(record.DefinitionId)))
                AcceptSource(record.Motion.Id, record.BirdPlayer, record.Operation);
        }
        internal void CompleteRelease(ItemRecord record)
        {
            if (active && Host && record.State == WorldItemState.World)
                CompleteSource(record.Motion.Id, record.BirdPlayer, record.Operation);
        }
        internal void AcceptSource(uint source, uint player, uint operation)
        {
            if (active && Host) ledger.Accept(source, player, operation);
        }
        internal void CompleteSource(uint source, uint player, uint operation)
        {
            if (active && Host) ledger.Complete(source, player, operation, Time.unscaledTime);
        }
        internal void RejectContact(BirdHitReport report)
        {
            if (report.Hits == null) return;
            foreach (var hit in report.Hits) hitReporter?.Confirmed(hit.Life, false, hit.Contact);
        }

        internal void ReleaseResolved(uint rock, uint operation, bool accepted) => hitReporter?.ReleaseResolved(rock, operation, accepted);
        internal bool IsRock(ItemDefinition definition)
        {
            if (!settings) return false;
            foreach (var rock in settings.Rocks) if (rock == definition) return true;
            return false;
        }

        internal void RememberCart(GolfCartNetwork cart)
        {
            if (!active || !Host) return;
            float now = Time.unscaledTime;
            oldDrivers.Clear();
            foreach (var pair in cartDrivers)
            {
                if (pair.Value.expires < now) oldDrivers.Add(pair.Key);
                else if (pair.Key.cart == cart.ObjectId && float.IsPositiveInfinity(pair.Value.expires)) oldDrivers.Add(pair.Key);
            }
            foreach (var key in oldDrivers)
            {
                var value = cartDrivers[key];
                if (value.expires < now) cartDrivers.Remove(key);
                else cartDrivers[key] = (value.player, now + 3f);
            }
            cartDrivers[(cart.ObjectId, cart.Epoch, cart.StateRevision)] = (PlayerToken(cart.DriverId), float.PositiveInfinity);
        }
        internal void ForgetCart(GolfCartNetwork cart)
        {
            hitReporter?.ForgetCart(cart.ObjectId);
            if (!Host) return;
            oldDrivers.Clear();
            foreach (var pair in cartDrivers) if (pair.Key.cart == cart.ObjectId) oldDrivers.Add(pair.Key);
            foreach (var key in oldDrivers) cartDrivers[key] = (cartDrivers[key].player, Time.unscaledTime + 3f);
        }

        internal void SubmitScare(BirdScareReport report)
        {
            if (!active || !ready || Replaying || report.Lives.Count == 0) return;
            report.Epoch = epoch;
            if (Host) ReceiveScare(null, report, Channel.Reliable);
            else network.ClientManager.Broadcast(report);
        }

        private void ReceiveScare(NetworkConnection connection, BirdScareReport report, Channel channel)
        {
            if (!active || report.Epoch != epoch) return;
            double now = SimulationTick;
            AdvanceClaims(now);
            foreach (uint life in report.Lives)
            {
                if (!records.TryGetValue(life, out var record) || record.Interrupt != BirdInterrupt.Calm) continue;
                var bird = species[record.Species];
                if (record.Reserved != record.Route.Perch) ReleaseClaim(record.Reserved, life, record.Revision);
                record.Reserved = 0; record.HasNext = false;
                record.Interrupt = BirdInterrupt.WaitingToFlee;
                Alert(life);
                record.FleeAt = (uint)Math.Ceiling(now + bird.ScareDelay / Delta);
                waitingThreats[life] = report.Position;
                record.Revision++;
                records[life] = record;
                decisions[life] = now;
                Publish(BirdEventKind.Plan, record);
            }
        }

        private void RetryEscape(uint life, double now)
        {
            var record = records[life];
            if (record.HasNext || !waitingThreats.TryGetValue(life, out var threat)) return;
            if (BuildRoute(record, Math.Max(now, record.FleeAt), true, threat, out var route))
            {
                QueueRoute(ref record, route); records[life] = record;
                Publish(BirdEventKind.Plan, record);
            }
            decisions[life] = now + settings.RetrySeconds / Delta;
        }

        internal void SubmitHits(BirdHitReport report)
        {
            if (!active || !ready || Replaying || report.Hits.Count == 0) return;
            report.Epoch = epoch;
            if (Host) ReceiveHits(null, report, Channel.Reliable);
            else network.ClientManager.Broadcast(report);
        }

        internal void SubmitProjectileHits(BirdHitReport report, int simulator)
        {
            if (!active || !ready || Replaying || report.Hits.Count == 0) return;
            report.Epoch = epoch;
            NetworkConnection sender = null;
            if (simulator >= 0) network.ServerManager.Clients.TryGetValue(simulator, out sender);
            ReceiveHits(sender != null && sender.IsLocalClient ? null : sender, report, Channel.Reliable);
        }

        private void ReceiveHits(NetworkConnection connection, BirdHitReport report, Channel channel)
        {
            if (!active || report.Epoch != epoch) return;
            uint player = report.Player;
            bool valid;
            if (report.Cart)
            {
                valid = cartDrivers.TryGetValue(((int)report.Source, report.MotionEpoch, report.SeatRevision), out var driver);
                if (valid) player = driver.player;
            }
            else valid = ledger.Contains(report.Source, player, report.Operation);
            foreach (var hit in report.Hits)
            {
                bool alive = records.TryGetValue(hit.Life, out var record);
                bool dead = !alive && hit.Life <= nextLife;
                if (alive && valid)
                {
                    var bird = species[record.Species];
                    float lethal = bird.LethalSpeed > 0f ? bird.LethalSpeed : settings.LethalSpeed;
                    if (report.Cart || hit.Speed >= lethal)
                    {
                        dead = true;
                        ReleaseClaim(record.Occupied, hit.Life); ReleaseClaim(record.Reserved, hit.Life);
                        ReleaseClaim(record.Route.Perch, hit.Life);
                        waitingThreats.Remove(hit.Life);
                        uint kills = report.Cart ? 0 : ledger.Kill(report.Source, player, report.Operation);
                        int bonus = !report.Cart && kills > 1 ? settings.MultiKillBonus : 0;
                        ShowDeath(hit.Life, record.Species, hit.Position);
                        RemoveLocal(hit.Life);
                        var zone = zones[record.Zone];
                        zoneReplacement.TryGetValue(zone.Id, out double last);
                        double due = Math.Max(Now, last) + UnityEngine.Random.Range(zone.ReplacementSeconds.x, zone.ReplacementSeconds.y) / Delta;
                        zoneReplacement[zone.Id] = due;
                        AddVacancy(zone, due);
                        Publish(BirdEventKind.Death, record, hit.Position);
                        if (player != 0 && tokenPlayers.TryGetValue(player, out var recipient) && recipient && recipient.Owner.IsActive)
                        {
                            var reward = ledger.Credit(player, bird.Reward, bonus, kills); reward.Epoch = epoch;
                            if (recipient.IsOwner) ApplyReward(reward);
                            else network.ServerManager.Broadcast(recipient.Owner, reward);
                        }
                    }
                    else
                    {
                        scareLives.Clear(); scareLives.Add(hit.Life);
                        ReceiveScare(connection, new BirdScareReport { Epoch = epoch, Position = hit.Position, Lives = scareLives }, channel);
                    }
                }
                var result = new BirdHitResult { Epoch = epoch, Life = hit.Life, Contact = hit.Contact, Dead = dead };
                if (connection == null) hitReporter.Confirmed(hit.Life, dead, hit.Contact);
                else network.ServerManager.Broadcast(connection, result);
            }
        }
        private readonly List<uint> scareLives = new(1);
        private void ReceiveHitResult(BirdHitResult result, Channel channel)
        {
            if (!Host && active && result.Epoch == epoch) hitReporter.Confirmed(result.Life, result.Dead, result.Contact);
        }
        private void SendBalance(NetworkConnection connection)
        {
            foreach (var pair in tokenPlayers)
            {
                if (!pair.Value || pair.Value.Owner != connection) continue;
                var reward = ledger.Balance(pair.Key); reward.Epoch = epoch;
                network.ServerManager.Broadcast(connection, reward);
                return;
            }
        }
        private void ReceiveReward(BirdReward reward, Channel channel)
        {
            if (!Host && active && reward.Epoch == epoch) ApplyReward(reward);
        }
        private void ApplyReward(BirdReward reward)
        {
            if (reward.Revision < rewardRevision || reward.Notify && reward.Revision == rewardRevision) return;
            rewardRevision = reward.Revision; LocalBalance = reward.Balance;
            RewardChanged?.Invoke(reward);
        }
    }
}
