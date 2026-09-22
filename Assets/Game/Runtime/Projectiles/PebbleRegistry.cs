using System.Collections.Generic;
using FishNet.Connection;
using FishNet.Managing.Timing;
using UnityEngine;

namespace TwoBirds
{
    internal sealed class PebbleRegistry
    {
        internal const float LifetimeSeconds = 30f;
        private sealed class Shot
        {
            internal PebbleRecord Record;
            internal PebbleProjectile Body;
            internal bool Pending, Retired, LocalSimulation;
            internal PebbleEnd End;
            internal ItemMotion? EarlyMotion;
            internal readonly List<PebbleTransition> Waiting = new();
        }
        private readonly WorldItemRegistry world;
        private readonly Dictionary<uint, Shot> live = new();
        private readonly Dictionary<(int, uint, uint), Shot> pending = new();
        private readonly List<Shot> shots = new();
        private readonly Dictionary<PebbleProjectile, RigidbodyInstancePool<PebbleProjectile>> pools = new();
        private readonly Dictionary<ParticleSystem, RigidbodyInstancePool<ParticleSystem>> dirtPools = new();
        private readonly List<(ParticleSystem Instance, ParticleSystem Prefab)> effects = new();
        private readonly Queue<PebbleTransition> deferred = new();
        private readonly Queue<PebbleSpawn> deferredSpawns = new();
        private readonly Queue<(PebbleTransition Transition, int Simulator)> deferredAcceptance = new();
        private readonly HashSet<uint> retired = new();
        private readonly Queue<uint> retirementOrder = new();

        internal PebbleRegistry(WorldItemRegistry owner) => world = owner;

        private static (int, uint, uint) Key(PebbleRecord record) => (record.Shooter, record.Lifetime, record.Shot);

        internal PebbleRecord Create(PebbleFire fire, int shooter, int simulator) => new()
        {
            Definition = fire.Action.DefinitionId, Shooter = shooter, Simulator = simulator,
            Lifetime = fire.Lifetime, Weapon = fire.Weapon, Shot = fire.Shot,
            Motion = fire.Motion, LaunchTick = fire.Action.StartedTick, LaunchFraction = fire.Action.StartedFraction,
            Expiry = fire.Action.StartedTick + new PreciseTick(fire.Action.StartedTick, fire.Action.StartedFraction).PercentAsDouble +
                LifetimeSeconds / world.TickDelta,
            BirdPlayer = BirdRegistry.Instance ? BirdRegistry.Instance.PlayerToken(shooter) : 0
        };

        internal void Predict(PebbleFire fire, int shooter)
        {
            var record = Create(fire, shooter, world.LocalConnection);
            record.Motion.Revision = 1;
            var shot = new Shot { Record = record, Pending = true };
            pending.Add(Key(record), shot); shots.Add(shot);
            Spawn(shot, true, Vector3.zero);
        }

        internal void Accept(PebbleFire fire, int shooter, int simulator)
        {
            var record = Create(fire, shooter, simulator);
            record.Motion.Id = world.AllocateRuntimeId(); record.Motion.Revision = 1;
            BirdRegistry.Instance?.AcceptSource(record.Motion.Id, record.BirdPlayer, record.Shot);
            var message = new PebbleSpawn { Epoch = world.Epoch, Accepted = true, Record = record };
            world.PublishPebble(message);
            Receive(message);
        }

        internal void Reject(PebbleFire fire, int shooter, NetworkConnection target)
        {
            var message = new PebbleSpawn { Epoch = world.Epoch, Record = Create(fire, shooter, -1) };
            if (world.LocalInventory && world.LocalInventory.ObjectId == shooter) Receive(message);
            else world.PublishPebble(message, target);
        }

        internal void Receive(PebbleSpawn message)
        {
            if (world.Replaying) { deferredSpawns.Enqueue(message); return; }
            var record = message.Record;
            if (record.Motion.Id != 0 && retired.Contains(record.Motion.Id)) return;
            if (pending.Remove(Key(record), out var predicted))
            {
                predicted.Pending = false;
                if (!message.Accepted)
                {
                    foreach (var transition in predicted.Waiting)
                        if (transition.HasBird) BirdRegistry.Instance?.RejectContact(transition.Bird);
                    if (predicted.Body) ReturnBody(predicted);
                    predicted.Retired = true;
                    if (world.TryGetPlayer(record.Shooter, out var player))
                        player.Equipment.HeldPresentation.RejectRelease(record.Weapon, record.Shot);
                    return;
                }
                predicted.Record = record;
                live.Add(record.Motion.Id, predicted);
                if (predicted.Body) predicted.Body.Associate(record);
                foreach (var saved in predicted.Waiting)
                {
                    var transition = saved;
                    transition.Record.Motion.Id = record.Motion.Id;
                    transition.Record.BirdPlayer = record.BirdPlayer;
                    transition.Record.Simulator = record.Simulator;
                    Submit(transition);
                }
                predicted.Waiting.Clear();
                return;
            }
            if (!message.Accepted) return;
            if (live.TryGetValue(record.Motion.Id, out var existing))
            {
                if (record.Motion.Revision <= existing.Record.Motion.Revision) return;
                existing.Record = record;
                if (existing.Body) ReturnBody(existing);
                Spawn(existing, world.Simulates(record.Simulator), Vector3.zero, true);
                return;
            }
            var shot = new Shot { Record = record };
            live.Add(record.Motion.Id, shot); shots.Add(shot);
            Vector3 departure = Vector3.zero;
            if (!message.Baseline && world.TryGetPlayer(record.Shooter, out var shooter) &&
                shooter.Equipment.HeldPresentation.TryPebbleDeparture(record.Weapon, record.Shot, record.LaunchTick, out var center))
                departure = center - record.Motion.Position;
            Spawn(shot, world.Simulates(record.Simulator), departure, message.Baseline);
        }

        private void Spawn(Shot shot, bool simulate, Vector3 departure, bool rebase = false)
        {
            var definition = (SlingshotDefinition)world.GetDefinition(shot.Record.Definition);
            if (!pools.TryGetValue(definition.PebblePrefab, out var pool)) pools[definition.PebblePrefab] = pool = new();
            shot.Body = pool.Rent(definition.PebblePrefab.gameObject, world.WorldScene);
            shot.LocalSimulation = simulate;
            shot.Body.gameObject.SetActive(true);
            shot.Body.Initialize(this, world, definition, shot.Record, simulate, departure, rebase);
        }

        internal void Transition(PebbleProjectile body, PebbleEnd end, bool hasBird, BirdHitReport bird)
        {
            var record = body.Record;
            var transition = new PebbleTransition { Epoch = world.Epoch, Record = record, End = end, HasBird = hasBird, Bird = bird };
            if (end == PebbleEnd.Impact) Dirt(record);
            if (pending.TryGetValue(Key(record), out var shot)) shot.Waiting.Add(transition);
            else Submit(transition);
        }

        private void Submit(PebbleTransition transition)
        {
            if (transition.HasBird)
            {
                transition.Bird.Source = transition.Record.Motion.Id;
                transition.Bird.Player = transition.Record.BirdPlayer;
            }
            if (world.IsHost) AcceptTransition(transition, transition.Record.Simulator);
            else world.SendPebble(transition);
        }

        internal void AcceptTransition(PebbleTransition transition, int simulator)
        {
            if (world.Replaying) { deferredAcceptance.Enqueue((transition, simulator)); return; }
            var record = transition.Record;
            if (!live.TryGetValue(record.Motion.Id, out var shot) || shot.Retired || shot.Record.Simulator != simulator ||
                record.Motion.Revision != shot.Record.Motion.Revision || record.Impact < shot.Record.Impact ||
                record.Impact > shot.Record.Impact + 1) return;
            bool impact = record.Impact > shot.Record.Impact;
            if (!impact && transition.End == PebbleEnd.None && record.ShooterCleared == shot.Record.ShooterCleared &&
                record.Touching == shot.Record.Touching) return;
            if (transition.HasBird) BirdRegistry.Instance?.SubmitProjectileHits(transition.Bird, simulator);
            ApplyTransition(shot, transition);
            world.PublishPebble(transition);
            if (transition.End != PebbleEnd.None)
                BirdRegistry.Instance?.CompleteSource(record.Motion.Id, record.BirdPlayer, record.Shot);
        }

        internal void Receive(PebbleTransition transition)
        {
            if (world.Replaying) { deferred.Enqueue(transition); return; }
            var record = transition.Record;
            if (!live.TryGetValue(record.Motion.Id, out var shot) || shot.Retired ||
                record.Motion.Revision != shot.Record.Motion.Revision || record.Impact < shot.Record.Impact) return;
            if (record.Impact == shot.Record.Impact && transition.End == PebbleEnd.None &&
                record.ShooterCleared == shot.Record.ShooterCleared && record.Touching == shot.Record.Touching) return;
            ApplyTransition(shot, transition);
        }

        private void ApplyTransition(Shot shot, PebbleTransition transition)
        {
            bool simulated = shot.LocalSimulation;
            if (shot.Body && !simulated)
            {
                if (transition.Record.Impact > shot.Record.Impact || transition.End != PebbleEnd.None)
                    shot.Body.Boundary(transition.Record, transition.End != PebbleEnd.None);
                else shot.Body.ContactState(transition.Record);
            }
            var record = transition.Record;
            if (shot.Record.Motion.Sequence > record.Motion.Sequence) record.Motion = shot.Record.Motion;
            shot.Record = record;
            if (transition.End != PebbleEnd.None)
            {
                shot.End = transition.End;
                shot.Retired = true;
                live.Remove(record.Motion.Id);
                retired.Add(record.Motion.Id); retirementOrder.Enqueue(record.Motion.Id);
                while (retirementOrder.Count > 512) retired.Remove(retirementOrder.Dequeue());
            }
            else if (shot.EarlyMotion.HasValue)
            {
                var motion = shot.EarlyMotion.Value; shot.EarlyMotion = null;
                ReceiveMotion(motion);
            }
        }

        internal bool ReceiveMotion(ItemMotion next, int? simulator = null)
        {
            if (!live.TryGetValue(next.Id, out var shot)) return false;
            var previous = shot.Record.Motion;
            if (shot.Retired || simulator.HasValue && simulator.Value != shot.Record.Simulator ||
                next.Revision != previous.Revision || next.Sequence <= previous.Sequence) return true;
            if (next.Path > previous.Path)
            {
                if (!shot.EarlyMotion.HasValue || next.Sequence > shot.EarlyMotion.Value.Sequence) shot.EarlyMotion = next;
                return true;
            }
            var record = shot.Record; record.Motion = next; shot.Record = record;
            if (shot.Body && !shot.Body.Simulating) shot.Body.Receive(next);
            return true;
        }

        internal bool AcceptsMotion(ItemMotion motion, int simulator) => live.TryGetValue(motion.Id, out var shot) &&
            !shot.Retired && shot.Record.Simulator == simulator && motion.Revision == shot.Record.Motion.Revision &&
            motion.Sequence > shot.Record.Motion.Sequence;

        internal void BeforePhysics()
        {
            foreach (var shot in shots) if (!shot.Retired && shot.Body) shot.Body.BeforePhysics();
        }
        internal void AfterPhysics(float seconds)
        {
            foreach (var shot in shots) if (!shot.Retired && shot.Body) shot.Body.AfterPhysics(seconds);
        }

        internal void Tick(bool snapshot)
        {
            foreach (var shot in shots)
            {
                if (shot.Retired || !shot.Body || shot.Body.Ended) continue;
                if (shot.Body.Simulating)
                {
                    var motion = shot.Body.Capture(world.ServerTick);
                    if (world.ServerTick >= shot.Record.Expiry) { shot.Body.End(PebbleEnd.Lifetime); continue; }
                    if (world.OutsideWorld(motion)) { shot.Body.End(PebbleEnd.Bounds); continue; }
                    if (shot.Pending || !snapshot) continue;
                    var record = shot.Body.Record;
                    motion.Sequence = record.Motion.Sequence + 1;
                    motion.RotationOmitted = true;
                    record.Motion = motion; shot.Body.SetRecord(record);
                    if (world.IsHost) shot.Record = record;
                    world.QueueMotion(motion);
                }
                else if (world.IsHost && world.ServerTick >= shot.Record.Expiry)
                {
                    var record = shot.Record; record.Motion.Sequence++;
                    AcceptTransition(new PebbleTransition { Epoch = world.Epoch, Record = record, End = PebbleEnd.Lifetime }, record.Simulator);
                }
            }
        }

        internal void Present(PlayerItemHitbox victim)
        {
            while (deferredSpawns.Count > 0) Receive(deferredSpawns.Dequeue());
            while (deferredAcceptance.Count > 0)
            {
                var entry = deferredAcceptance.Dequeue(); AcceptTransition(entry.Transition, entry.Simulator);
            }
            while (deferred.Count > 0) Receive(deferred.Dequeue());
            for (int i = shots.Count - 1; i >= 0; i--)
            {
                var shot = shots[i];
                if (shot.Body) shot.Body.Present(victim);
                if (shot.Body && shot.Body.Ended)
                {
                    if (!shot.LocalSimulation && shot.End == PebbleEnd.Impact) Dirt(shot.Record);
                    ReturnBody(shot);
                }
                if (!shot.Pending && shot.Retired && !shot.Body) shots.RemoveAt(i);
            }
            for (int i = effects.Count - 1; i >= 0; i--)
            {
                var effect = effects[i];
                if (effect.Instance.IsAlive(true)) continue;
                effect.Instance.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                dirtPools[effect.Prefab].Return(effect.Instance);
                effects.RemoveAt(i);
            }
        }

        private void ReturnBody(Shot shot)
        {
            var definition = (SlingshotDefinition)world.GetDefinition(shot.Record.Definition);
            shot.Body.ResetForPool(); pools[definition.PebblePrefab].Return(shot.Body); shot.Body = null;
        }

        private void Dirt(PebbleRecord record)
        {
            var prefab = ((SlingshotDefinition)world.GetDefinition(record.Definition)).DirtPrefab;
            if (!prefab) return;
            if (!dirtPools.TryGetValue(prefab, out var pool)) dirtPools[prefab] = pool = new();
            var effect = pool.Rent(prefab.gameObject, world.WorldScene);
            effect.transform.SetPositionAndRotation(record.ContactPoint, Quaternion.FromToRotation(Vector3.up, record.ContactNormal));
            effect.gameObject.SetActive(true);
            effect.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear); effect.Play(true);
            effects.Add((effect, prefab));
        }

        internal void Baseline(NetworkConnection target)
        {
            foreach (var shot in live.Values)
            {
                var record = shot.Record;
                if (shot.Body && shot.Body.Simulating && !shot.Body.Ended) record = shot.Body.Record;
                world.PublishPebble(new PebbleSpawn { Epoch = world.Epoch, Accepted = true, Baseline = true, Record = record }, target);
            }
        }

        internal void TakeOver(int simulator)
        {
            foreach (var shot in live.Values)
            {
                if (shot.Record.Simulator != simulator) continue;
                var record = shot.Record;
                record.Simulator = -1; record.Motion.Revision++; record.Motion.Sequence = 0; record.Motion.Tick = world.ServerTick;
                shot.Record = record; shot.EarlyMotion = null;
                if (shot.Body) ReturnBody(shot);
                Spawn(shot, true, Vector3.zero, true);
                world.PublishPebble(new PebbleSpawn { Epoch = world.Epoch, Accepted = true, Baseline = true, Record = record });
            }
        }

        internal void Clear()
        {
            foreach (var shot in shots) if (shot.Body) { shot.Body.ResetForPool(); Object.Destroy(shot.Body.gameObject); }
            foreach (var effect in effects) if (effect.Instance) Object.Destroy(effect.Instance.gameObject);
            foreach (var pool in pools.Values) pool.Clear();
            foreach (var pool in dirtPools.Values) pool.Clear();
            shots.Clear(); live.Clear(); pending.Clear(); pools.Clear(); dirtPools.Clear(); effects.Clear(); deferred.Clear();
            deferredSpawns.Clear(); deferredAcceptance.Clear(); retired.Clear(); retirementOrder.Clear();
        }
    }
}
