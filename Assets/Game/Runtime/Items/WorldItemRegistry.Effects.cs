using System.Collections.Generic;
using FishNet.Connection;
using FishNet.Transporting;
using UnityEngine;

namespace TwoBirds
{
    public sealed partial class WorldItemRegistry
    {
        private readonly Dictionary<(uint Epoch, uint Item, int Releaser, uint Operation), ItemContact> contacts = new();
        private readonly Dictionary<(uint Epoch, uint Item, int Releaser, uint Operation), HashSet<NetworkConnection>> contactReporters = new();
        private readonly Dictionary<uint, PotionActivation> activations = new();
        private readonly Dictionary<uint, PotionArea> areas = new();
        private readonly Dictionary<int, PotionDose> doses = new();
        private readonly Dictionary<(uint Item, int Player, uint Operation), GameObject> predictedClouds = new();
        private readonly List<uint> expiredAreas = new();
        private readonly List<(uint Item, int Player, uint Operation)> staleClouds = new();
        private uint nextEffectId;
        private bool reseedAreas;
        private readonly Queue<PotionDoseMessage> deferredDoses = new();
        private readonly Queue<ItemContactResult> deferredContactResults = new();
        private void EffectsAfterReconcile(uint clientTick, uint serverTick) => reseedAreas = true;

        internal void QueueIntake(WorldItem item, Cauldron cauldron)
        {
            if (Replaying || !item.ReleaseAvailable || !item.Simulating || !item.Definition.CanBeIngredient) return;
            var report = ContactFor(item);
            report.Cauldron = cauldron.ObjectId;
            contacts[ContactKey(report)] = report;
        }
        internal void QueueItemImpact(WorldItem item, Vector3 position, GolfCartNetwork cart = null)
        {
            if (Replaying || !item.ReleaseAvailable || !item.Record.Armed || !item.Simulating) return;
            var report = ContactFor(item);
            if (!report.Impact)
            {
                report.Impact = true;
                report.Cart = cart ? cart.ObjectId : -1;
                report.CartLifetime = cart ? cart.EffectLifetime : 0;
                report.Position = cart ? Quaternion.Inverse(cart.Controller.Body.rotation) * (position - cart.Controller.Body.position) : position;
                contacts[ContactKey(report)] = report;
            }
            PredictContactCloud(report, item.Definition, position);
        }
        private void PredictContactCloud(ItemContact report, ItemDefinition definition, Vector3 position)
        {
            if (!IsHost && definition is PotionDefinition potion && potion.CloudVfx)
            {
                var key = (report.Item, report.Releaser, report.Operation);
                if (!predictedClouds.ContainsKey(key)) predictedClouds[key] = Instantiate(potion.CloudVfx, position, Quaternion.identity);
            }
        }
        private ItemContact ContactFor(WorldItem item)
        {
            var record = item.Record;
            if (contacts.TryGetValue(SplatKey(record), out var existing))
                return existing;
            return new ItemContact { Epoch = epoch, Item = record.Motion.Id, Revision = record.Motion.Revision,
                Releaser = record.Releaser, Operation = record.Operation, Cauldron = -1, Cart = -1, Position = item.PresentedRootPosition };
        }
        private void FlushItemContacts()
        {
            if (contacts.Count == 0) return;
            var reports = new List<ItemContact>(contacts.Values);
            foreach (var report in reports)
            {
                if (!IsHost) PredictSplat(report);
                if (pendingReleases.ContainsKey(report.Item)) continue;
                var key = ContactKey(report);
                contacts.Remove(key);
                if (IsHost)
                {
                    bool duplicate = acceptedSplats.ContainsKey(key);
                    bool accepted = AcceptContact(report);
                    var result = new ItemContactResult { Epoch = epoch, Item = report.Item,
                        Releaser = report.Releaser, Operation = report.Operation, HasSplat = report.HasSplat || accepted, SplatAccepted = accepted };
                    if (contactReporters.Remove(key, out var reporters))
                        foreach (var reporter in reporters)
                        {
                            if (!reporter.IsActive) continue;
                            if (accepted && duplicate)
                                network.ServerManager.Broadcast(reporter, new CraftingTransition { Epoch = epoch, HasSplat = true, Splat = acceptedSplats[key] });
                            network.ServerManager.Broadcast(reporter, result);
                        }
                    if (players.TryGetValue(report.Releaser, out var player) && player.Owner.IsActive && !player.IsOwner)
                        network.ServerManager.Broadcast(player.Owner, result);
                }
                else network.ClientManager.Broadcast(report);
            }
        }
        private void ReceiveItemContact(NetworkConnection connection, ItemContact report, Channel channel)
        {
            if (!worldReady || report.Epoch != epoch) return;
            var key = ContactKey(report);
            if (contacts.TryGetValue(key, out var existing))
            {
                if (existing.Cauldron < 0) existing.Cauldron = report.Cauldron;
                if (!existing.Impact && report.Impact)
                {
                    existing.Impact = true; existing.Position = report.Position;
                    existing.Cart = report.Cart; existing.CartLifetime = report.CartLifetime;
                }
                if (!existing.HasSplat && report.HasSplat)
                {
                    existing.HasSplat = true; existing.Target = report.Target;
                    existing.SplatPoint = report.SplatPoint; existing.SplatRotation = report.SplatRotation;
                    existing.SplatVelocity = report.SplatVelocity;
                }
                report = existing;
            }
            contacts[key] = report;
            if (!contactReporters.TryGetValue(key, out var reporters)) contactReporters[key] = reporters = new();
            reporters.Add(connection);
        }
        private void ContactResolved(ItemContactResult result, Channel channel)
        {
            if (result.Epoch != epoch) return;
            if (Replaying) { deferredContactResults.Enqueue(result); return; }
            RemovePredictedCloud((result.Item, result.Releaser, result.Operation));
            if (result.HasSplat && !result.SplatAccepted) RejectSplat((result.Epoch, result.Item, result.Releaser, result.Operation));
        }
        private void RemovePredictedCloud((uint, int, uint) key)
        {
            if (predictedClouds.Remove(key, out var cloud) && cloud) Destroy(cloud);
        }
        private void ClearPredictedClouds(uint item)
        {
            staleClouds.Clear();
            foreach (var key in predictedClouds.Keys) if (key.Item == item) staleClouds.Add(key);
            foreach (var key in staleClouds) RemovePredictedCloud(key);
        }
        private bool AcceptContact(ItemContact report)
        {
            if (report.Epoch != epoch) return false;
            bool acceptedSplat = acceptedSplats.ContainsKey(ContactKey(report));
            if (!records.TryGetValue(report.Item, out var item) || item.State != WorldItemState.World ||
                item.Operation != report.Operation || item.Releaser != report.Releaser || item.Motion.Revision < report.Revision) return acceptedSplat;
            if (report.Cauldron >= 0 && GetDefinition(item.DefinitionId).CanBeIngredient &&
                cauldrons.TryGetValue(report.Cauldron, out var cauldron) && cauldron.Accepting)
            {
                var visual = items[report.Item];
                Admit(item, cauldron, item.Releaser, item.Operation, visual.PresentedRootPosition, visual.PresentedRotation);
                return acceptedSplat;
            }
            var definition = GetDefinition(item.DefinitionId);
            bool splat = !acceptedSplat && report.HasSplat && item.SplatArmed && definition.CanSpawnSplat;
            bool potion = report.Impact && item.Armed && definition is PotionDefinition;
            if (!splat && !potion) return acceptedSplat;
            var transition = new CraftingTransition { HasSplat = splat, HasActivation = potion };
            if (splat)
            {
                transition.Splat = BuildSplat(report, item.DefinitionId);
                item.SplatArmed = false;
            }
            if (potion) transition.Activation = BuildActivation(item, item.Releaser, item.Operation, report.Position, report.Cart, report.CartLifetime);
            if (potion || splat && definition.DestroyOnSplat) transition.Items = new() { Tombstone(item) };
            CommitFeature(transition);
            return splat || acceptedSplat;
        }
        internal bool UsePotion(ItemRecord item, PlayerInventory player, uint operation, Vector3 position)
        {
            if (GetDefinition(item.DefinitionId) is not PotionDefinition || !Finite(position)) return false;
            CommitActivation(item, player.ObjectId, operation, position, -1);
            return true;
        }
        private void CommitActivation(ItemRecord item, int player, uint operation, Vector3 position, int cart, uint cartLifetime = 0)
        {
            CommitFeature(new CraftingTransition { Items = new() { Tombstone(item) }, HasActivation = true,
                Activation = BuildActivation(item, player, operation, position, cart, cartLifetime) });
        }
        private PotionActivation BuildActivation(ItemRecord item, int player, uint operation, Vector3 position, int cart, uint cartLifetime)
        {
            var definition = (PotionDefinition)GetDefinition(item.DefinitionId);
            var activation = new PotionActivation { Id = ++nextEffectId, Definition = item.DefinitionId, StartTick = ServerTick,
                Expiry = ServerTick + DurationTicks(definition.Application == PotionApplication.Zone ? definition.ZoneLifetime : 0.5f),
                Player = player, Operation = operation, SourceItem = item.Motion.Id, Position = position, Cart = cart, CartLifetime = cartLifetime };
            return activation;
        }
        private void CreateArea(PotionActivation activation, bool snapshot, GameObject predictedCloud)
        {
            if (activations.ContainsKey(activation.Id)) { if (predictedCloud) Destroy(predictedCloud); return; }
            activations[activation.Id] = activation;
            if (activation.Expiry <= ServerTick) { if (predictedCloud) Destroy(predictedCloud); return; }
            var root = new GameObject("Potion area");
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(root, worldScene);
            var area = root.AddComponent<PotionArea>();
            areas[activation.Id] = area;
            area.Initialize(this, activation, (PotionDefinition)GetDefinition(activation.Definition), snapshot, predictedCloud);
        }
        private void StepAreas()
        {
            while (deferredDoses.Count > 0) ReceiveDose(deferredDoses.Dequeue(), Channel.Reliable);
            if (reseedAreas)
            {
                reseedAreas = false;
                RefreshEffectPoses();
                foreach (var area in areas.Values) if (area) area.ReseedZone();
            }
            expiredAreas.Clear();
            foreach (var pair in areas)
                if (!pair.Value || !pair.Value.Step()) expiredAreas.Add(pair.Key);
            foreach (uint id in expiredAreas)
            {
                if (areas.Remove(id, out var area) && area) Destroy(area.gameObject);
            }
            foreach (var player in players.Values) if (player && player.Effects) player.Effects.Step();
        }
        private void CartEffectLifetimeChanged(GolfCartNetwork cart, bool spawned)
        {
            SplatTargetLifetime.Invalidate(cart.transform);
            if (spawned) return;
            expiredAreas.Clear();
            foreach (var activation in activations.Values)
                if (activation.Cart == cart.ObjectId && activation.CartLifetime == cart.EffectLifetime) expiredAreas.Add(activation.Id);
            foreach (uint id in expiredAreas)
            {
                activations.Remove(id);
                if (areas.Remove(id, out var area) && area) Destroy(area.gameObject);
            }
        }
        internal void RefreshEffectPoses()
        {
            foreach (var player in players.Values) if (player && player.Effects) player.Effects.RefreshPhysicalPose();
            foreach (var area in areas.Values) if (area) area.RefreshPlacement();
            Physics.SyncTransforms();
        }
        internal void ReseedReceiver(PlayerEffectReceiver receiver)
        {
            if (Replaying || !worldReady) return;
            RefreshEffectPoses();
            foreach (var area in areas.Values) if (area) area.Reseed(receiver);
        }
        internal void RemoveReceiver(PlayerEffectReceiver receiver)
        {
            if (Replaying) return;
            foreach (var area in areas.Values) if (area) area.Remove(receiver);
        }
        internal void ReportDose(PotionDose dose)
        {
            if (Replaying) return;
            if (IsHost) StoreDose(dose);
            else network.ClientManager.Broadcast(new PotionDoseMessage { Epoch = epoch, Dose = dose });
        }
        private void AcceptDose(NetworkConnection connection, PotionDoseMessage message, Channel channel)
        {
            if (message.Epoch != epoch || !players.TryGetValue(message.Dose.Player, out var player) || player.Owner != connection) return;
            StoreDose(message.Dose);
        }
        private void StoreDose(PotionDose dose)
        {
            if (!players.TryGetValue(dose.Player, out var player) || !player.Effects ||
                player.Effects.Lifetime != dose.Lifetime || player.Effects.Reset != dose.Reset ||
                doses.TryGetValue(dose.Player, out var old) && old.Revision >= dose.Revision ||
                !activations.TryGetValue(dose.Effect, out var activation) || activation.Definition != dose.Definition) return;
            player.Effects.Motor.ScheduleAcceptedDose(ref dose);
            doses[dose.Player] = dose;
            player.Effects.AcceptDose(dose);
            network.ServerManager.Broadcast(observers, new PotionDoseMessage { Epoch = epoch, Dose = dose });
        }
        private void ReceiveDose(PotionDoseMessage message, Channel channel)
        {
            if (IsHost || message.Epoch != epoch) return;
            if (Replaying) { deferredDoses.Enqueue(message); return; }
            var dose = message.Dose;
            if (doses.TryGetValue(dose.Player, out var old) && old.Lifetime == dose.Lifetime && old.Revision >= dose.Revision) return;
            doses[dose.Player] = dose;
            if (players.TryGetValue(dose.Player, out var player) && player.Effects) player.Effects.AcceptDose(dose);
        }
        internal void BindEffects(PlayerPotionEffects effects)
        {
            if (doses.TryGetValue(effects.ObjectId, out var dose)) effects.AcceptDose(dose);
        }
        private void ResolvePlayerEffects(PlayerInventory player)
        {
            if (doses.TryGetValue(player.ObjectId, out var dose) && player.Effects) player.Effects.AcceptDose(dose);
        }
        private void ForgetPlayerEffects(PlayerInventory player)
        {
            doses.Remove(player.ObjectId);
            cleanup.Clear();
            foreach (var report in contacts.Values) if (report.Releaser == player.ObjectId) cleanup.Add(report.Item);
            foreach (uint id in cleanup) { CancelItemContacts(id); ClearPredictedClouds(id); }
        }
        internal void ClearPlayerDose(int id) => doses.Remove(id);

        internal void PredictConsumption(InventoryRequest request, PlayerInventory player)
        {
            if (IsHost) return;
            if (request.Kind == InventoryOperation.Insert && cauldrons.TryGetValue(request.To, out var cauldron))
                cauldron.Presentation.Predict(request, player.ObjectId);
            else if (GetDefinition(request.DefinitionId) is PotionDefinition potion && potion.CloudVfx)
                predictedClouds[(request.Ids[0], player.ObjectId, request.Operation)] = Instantiate(potion.CloudVfx, request.Position, Quaternion.identity);
            if (items.TryGetValue(request.Ids[0], out var item)) item.PredictPickup();
        }
        internal void ResolveConsumption(InventoryRequest request, bool accepted)
        {
            if (request.Kind is not (InventoryOperation.Insert or InventoryOperation.DirectUse)) return;
            if (!accepted)
            {
                if (cauldrons.TryGetValue(request.To, out var cauldron)) cauldron.Presentation.Reject(request.Operation);
                if (LocalInventory) RemovePredictedCloud((request.Ids[0], LocalInventory.ObjectId, request.Operation));
            }
        }
        private void ApplyBlast(Cauldron cauldron)
        {
            RefreshEffectPoses();
            Vector3 origin = cauldron.BlastAnchor.position;
            var seen = new HashSet<int>();
            foreach (var collider in Physics.OverlapSphere(origin, cauldron.BlastRadius, LayerMask.GetMask("PlayerEffectReceiver"), QueryTriggerInteraction.Collide))
            {
                if (!collider.TryGetComponent<PlayerEffectReceiver>(out var receiver) || !receiver.Eligible ||
                    !receiver.Effects.IsOwner || !seen.Add(receiver.Effects.ObjectId)) continue;
                Vector3 offset = collider.bounds.center - origin;
                bool blocked = false;
                foreach (var hit in Physics.RaycastAll(origin, offset.normalized, offset.magnitude, EnvironmentMask, QueryTriggerInteraction.Ignore))
                    if (!hit.collider.transform.IsChildOf(cauldron.transform)) { blocked = true; break; }
                if (blocked) continue;
                float falloff = Mathf.Lerp(0.35f, 1f, Mathf.Clamp01(1f - offset.magnitude / cauldron.BlastRadius));
                offset.y = 0f;
                Vector3 shove = receiver.Effects.Motor.Suspended ? Vector3.zero :
                    (offset.normalized * cauldron.BlastOutward + Vector3.up * cauldron.BlastUpward) * falloff;
                receiver.Effects.ApplyDamage(cauldron.BlastDamage, shove);
                if (!receiver.Effects.Motor.Suspended) receiver.Effects.Motor.SubmitWorldImpact(shove, 0.35f);
            }
        }
    }
}
