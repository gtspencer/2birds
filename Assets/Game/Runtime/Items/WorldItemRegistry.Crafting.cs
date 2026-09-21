using System.Collections.Generic;
using FishNet.Connection;
using FishNet.Transporting;
using UnityEngine;

namespace TwoBirds
{
    public sealed partial class WorldItemRegistry
    {
        private readonly Dictionary<int, Cauldron> cauldrons = new();
        private readonly Dictionary<int, CauldronRecord> mixtures = new();
        private uint nextCraftedId;
        private readonly List<int> cauldronSteps = new();
        private readonly Queue<CraftingTransition> deferredFeatures = new();
        internal uint Epoch => epoch;
        internal uint DurationTicks(float seconds) => (uint)System.Math.Max(1, System.Math.Ceiling(seconds / TickDelta));

        private void RegisterCraftingMessages()
        {
            predictionManager.OnPostReconcile += EffectsAfterReconcile;
            GolfCartNetwork.LifetimeChanged += CartEffectLifetimeChanged;
            network.ClientManager.RegisterBroadcast<CraftingTransition>(ReceiveCrafting);
            network.ServerManager.RegisterBroadcast<PotionContact>(ReceivePotionContact);
            network.ClientManager.RegisterBroadcast<PotionDoseMessage>(ReceiveDose);
            network.ClientManager.RegisterBroadcast<PotionContactResult>(ContactResolved);
            network.ServerManager.RegisterBroadcast<PotionDoseMessage>(AcceptDose);
        }
        private void UnregisterCraftingMessages()
        {
            predictionManager.OnPostReconcile -= EffectsAfterReconcile;
            GolfCartNetwork.LifetimeChanged -= CartEffectLifetimeChanged;
            network.ClientManager.UnregisterBroadcast<CraftingTransition>(ReceiveCrafting);
            network.ServerManager.UnregisterBroadcast<PotionContact>(ReceivePotionContact);
            network.ClientManager.UnregisterBroadcast<PotionDoseMessage>(ReceiveDose);
            network.ClientManager.UnregisterBroadcast<PotionContactResult>(ContactResolved);
            network.ServerManager.UnregisterBroadcast<PotionDoseMessage>(AcceptDose);
        }
        private void BeginCraftingWorld()
        {
            nextCraftedId = 0;
            foreach (uint id in records.Keys) if (id > nextCraftedId) nextCraftedId = id;
        }
        internal void RegisterCauldron(Cauldron cauldron)
        {
            cauldrons[cauldron.ObjectId] = cauldron;
            if (!mixtures.TryGetValue(cauldron.ObjectId, out var state))
            {
                state = new CauldronRecord { Id = cauldron.ObjectId, Revision = 1, Ingredients = new() };
                mixtures[state.Id] = state;
            }
            cauldron.Apply(state, true);
        }
        internal void UnregisterCauldron(Cauldron cauldron)
        {
            cauldrons.Remove(cauldron.ObjectId);
            if (mixtures.Remove(cauldron.ObjectId, out var state) && IsHost && state.Output != 0) Remove(state.Output);
        }
        internal bool OutputReady(ItemRecord record) => record.State == WorldItemState.CauldronOutput &&
            mixtures.TryGetValue(record.Cauldron, out var state) && state.Phase == CauldronPhase.Ready && state.Output == record.Motion.Id;

        internal bool AdmitHeld(uint id, int cauldronId, PlayerInventory player, uint operation, Vector3 position, Quaternion rotation)
        {
            if (!cauldrons.TryGetValue(cauldronId, out var cauldron) || !cauldron.Accepting ||
                !records.TryGetValue(id, out var item) || item.State != WorldItemState.Held || item.Holder != player.ObjectId) return false;
            Admit(item, cauldron, player.ObjectId, operation, position, rotation);
            return true;
        }
        private void Admit(ItemRecord item, Cauldron cauldron, int player, uint operation, Vector3 position, Quaternion rotation)
        {
            var state = mixtures[cauldron.ObjectId];
            state.Ingredients = new List<IngredientRecord>(state.Ingredients);
            state.Ingredients.Add(new IngredientRecord { Definition = item.DefinitionId, WorldId = item.Motion.Id, Player = player, Operation = operation,
                Position = position, Rotation = rotation, StartTick = ServerTick });
            state.Phase = CauldronPhase.Occupied;
            state.Revision++;
            state.Deadline = ServerTick + DurationTicks(cauldron.InsertionSeconds);
            CommitFeature(new CraftingTransition { HasCauldron = true, Cauldron = state, Items = new() { Tombstone(item) } });
        }
        internal bool ResolveMixture(int id, uint revision, bool dispose)
        {
            if (!cauldrons.TryGetValue(id, out var cauldron) || !cauldron.Settled || cauldron.State.Revision != revision) return false;
            var state = mixtures[id];
            var result = dispose ? null : cauldron.Recipes.Resolve(state.Ingredients);
            state.Result = result ? result.ItemId : (byte)0;
            state.Ingredients = new();
            state.Revision++;
            state.StartTick = ServerTick;
            state.Phase = dispose ? CauldronPhase.Disposing : CauldronPhase.Brewing;
            state.Deadline = ServerTick + DurationTicks(dispose ? cauldron.ResultSeconds : cauldron.BrewSeconds);
            CommitFeature(new CraftingTransition { HasCauldron = true, Cauldron = state });
            return true;
        }
        private ItemRecord Tombstone(ItemRecord item)
        {
            BirdRegistry.Instance?.CompleteRelease(item);
            item.State = WorldItemState.Removed;
            item.Armed = false;
            item.Motion.Revision++;
            item.Motion.Tick = ServerTick;
            return item;
        }
        private void PublishCollection(ItemRecord item)
        {
            foreach (var pair in mixtures)
            {
                if (pair.Value.Output != item.Motion.Id) continue;
                var state = pair.Value;
                state.Phase = CauldronPhase.Empty;
                state.Output = 0;
                state.Revision++;
                CommitFeature(new CraftingTransition { HasCauldron = true, Cauldron = state, Items = new() { item } });
                return;
            }
            Publish(item);
        }
        private void CommitFeature(CraftingTransition transition)
        {
            transition.Epoch = epoch;
            ApplyFeature(transition);
            network.ServerManager.Broadcast(observers, transition);
        }
        private void ReceiveCrafting(CraftingTransition transition, Channel channel)
        {
            if (IsHost || !worldReady || transition.Epoch != epoch) return;
            if (Replaying) deferredFeatures.Enqueue(transition);
            else ApplyFeature(transition);
        }
        private void ApplyFeature(CraftingTransition transition)
        {
            GameObject predictedCloud = null;
            if (transition.HasActivation)
            {
                var source = transition.Activation;
                predictedClouds.Remove((source.SourceItem, source.Player, source.Operation), out predictedCloud);
            }
            Cauldron changed = null;
            if (transition.HasCauldron)
            {
                var state = transition.Cauldron;
                if (!mixtures.TryGetValue(state.Id, out var previous) || state.Revision > previous.Revision)
                {
                    mixtures[state.Id] = state;
                    cauldrons.TryGetValue(state.Id, out changed);
                }
            }
            if (transition.HasActivation && LocalInventory && items.TryGetValue(transition.Activation.SourceItem, out var bottle))
                bottle.SamplePlayerContact(LocalInventory.Hitbox);
            if (transition.Items != null) ApplyLifecycle(transition.Items);
            if (changed)
            {
                changed.Apply(transition.Cauldron, transition.Snapshot);
                if (transition.Blast && !transition.Snapshot) ApplyBlast(changed);
            }
            if (transition.HasActivation) CreateArea(transition.Activation, transition.Snapshot, predictedCloud);
        }
        private void StepCrafting()
        {
            while (deferredFeatures.Count > 0) ApplyFeature(deferredFeatures.Dequeue());
            StepAreas();
            if (!IsHost) return;
            cauldronSteps.Clear();
            foreach (var pair in mixtures)
                if ((pair.Value.Phase is CauldronPhase.Brewing or CauldronPhase.Rising or CauldronPhase.Failed or CauldronPhase.Disposing) &&
                    ServerTick >= pair.Value.Deadline) cauldronSteps.Add(pair.Key);
            foreach (int id in cauldronSteps)
            {
                if (!cauldrons.TryGetValue(id, out var cauldron)) continue;
                var state = mixtures[id];
                state.Revision++;
                state.StartTick = ServerTick;
                var transition = new CraftingTransition { HasCauldron = true };
                if (state.Phase == CauldronPhase.Brewing && state.Result != 0)
                {
                    var item = new ItemRecord { DefinitionId = state.Result, State = WorldItemState.CauldronOutput,
                        Cauldron = id, Holder = -1, Releaser = -1, Simulator = -1,
                        Motion = new ItemMotion { Id = ++nextCraftedId, Revision = 1, Tick = ServerTick,
                            Position = cauldron.IntakeAnchor.position, Rotation = Quaternion.identity } };
                    state.Output = item.Motion.Id;
                    state.Phase = CauldronPhase.Rising;
                    state.Deadline = ServerTick + DurationTicks(cauldron.RiseSeconds);
                    transition.Items = new() { item };
                }
                else if (state.Phase == CauldronPhase.Brewing)
                {
                    state.Phase = CauldronPhase.Failed;
                    state.Deadline = ServerTick + DurationTicks(cauldron.ResultSeconds);
                    transition.Blast = true;
                }
                else state.Phase = state.Phase == CauldronPhase.Rising ? CauldronPhase.Ready : CauldronPhase.Empty;
                transition.Cauldron = state;
                CommitFeature(transition);
            }
        }
        private void SendCraftingBaseline(NetworkConnection connection)
        {
            foreach (var state in mixtures.Values)
                network.ServerManager.Broadcast(connection, new CraftingTransition { Epoch = epoch, Snapshot = true, HasCauldron = true, Cauldron = state });
            foreach (var area in activations.Values)
                if (GetDefinition(area.Definition) is PotionDefinition potion && potion.Application == PotionApplication.Zone && area.Expiry > ServerTick)
                    network.ServerManager.Broadcast(connection, new CraftingTransition { Epoch = epoch, Snapshot = true, HasActivation = true, Activation = area });
            foreach (var dose in doses.Values)
                network.ServerManager.Broadcast(connection, new PotionDoseMessage { Epoch = epoch, Dose = dose });
        }
        private void EndCraftingWorld()
        {
            foreach (var area in areas.Values) if (area) Destroy(area.gameObject);
            foreach (var cloud in predictedClouds.Values) if (cloud) Destroy(cloud);
            predictedClouds.Clear(); deferredDoses.Clear(); reseedAreas = false;
            areas.Clear(); activations.Clear(); doses.Clear(); contacts.Clear(); deferredFeatures.Clear();
            mixtures.Clear(); cauldrons.Clear(); cauldronSteps.Clear(); nextCraftedId = nextEffectId = 0;
        }
    }
}
