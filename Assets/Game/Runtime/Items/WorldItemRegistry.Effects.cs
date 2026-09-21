using System.Collections.Generic;
using FishNet.Connection;
using FishNet.Transporting;
using UnityEngine;

namespace TwoBirds
{
    public sealed partial class WorldItemRegistry
    {
        private readonly Dictionary<uint, PotionContact> contacts = new();
        private readonly Dictionary<uint, PotionActivation> activations = new();
        private readonly Dictionary<uint, PotionArea> areas = new();
        private readonly Dictionary<int, PotionDose> doses = new();
        private readonly Dictionary<(uint Item, int Player, uint Operation), GameObject> predictedClouds = new();
        private readonly List<uint> expiredAreas = new();
        private readonly List<(uint Item, int Player, uint Operation)> staleClouds = new();
        private uint nextEffectId;
        private bool reseedAreas;
        private readonly Queue<PotionDoseMessage> deferredDoses = new();
        private void EffectsAfterReconcile(uint clientTick, uint serverTick) => reseedAreas = true;

        internal void QueueIntake(WorldItem item, Cauldron cauldron)
        {
            if (Replaying || !item.ReleaseAvailable || !item.Simulating) return;
            var report = ContactFor(item);
            report.Cauldron = cauldron.ObjectId;
            contacts[report.Item] = report;
        }
        internal void QueuePotionImpact(WorldItem item, Vector3 position, GolfCartNetwork cart = null)
        {
            if (Replaying || !item.ReleaseAvailable || !item.Record.Armed || !item.Simulating) return;
            var report = ContactFor(item);
            if (report.Impact) return;
            report.Impact = true;
            report.Cart = cart ? cart.ObjectId : -1;
            report.CartLifetime = cart ? cart.EffectLifetime : 0;
            report.Position = cart ? Quaternion.Inverse(cart.Controller.Body.rotation) * (position - cart.Controller.Body.position) : position;
            contacts[report.Item] = report;
            if (!IsHost && item.Definition is PotionDefinition potion && potion.CloudVfx)
            {
                var key = (report.Item, report.Releaser, report.Operation);
                if (!predictedClouds.ContainsKey(key)) predictedClouds[key] = Instantiate(potion.CloudVfx, position, Quaternion.identity);
            }
        }
        private PotionContact ContactFor(WorldItem item)
        {
            var record = item.Record;
            if (contacts.TryGetValue(record.Motion.Id, out var existing) && existing.Operation == record.Operation && existing.Releaser == record.Releaser)
                return existing;
            return new PotionContact { Epoch = epoch, Item = record.Motion.Id, Revision = record.Motion.Revision,
                Releaser = record.Releaser, Operation = record.Operation, Cauldron = -1, Cart = -1, Position = item.PresentedRootPosition };
        }
        private void FlushPotionContacts()
        {
            if (contacts.Count == 0) return;
            var reports = new List<PotionContact>(contacts.Values);
            foreach (var report in reports)
            {
                if (pendingReleases.ContainsKey(report.Item)) continue;
                contacts.Remove(report.Item);
                if (IsHost)
                {
                    AcceptContact(report);
                    if (players.TryGetValue(report.Releaser, out var player) && player.Owner.IsActive && !player.IsOwner)
                        network.ServerManager.Broadcast(player.Owner, new PotionContactResult { Epoch = epoch, Item = report.Item,
                            Releaser = report.Releaser, Operation = report.Operation });
                }
                else network.ClientManager.Broadcast(report);
            }
        }
        private void ReceivePotionContact(NetworkConnection connection, PotionContact report, Channel channel)
        {
            if (!worldReady || report.Epoch != epoch) return;
            if (Replaying) { contacts[report.Item] = report; return; }
            AcceptContact(report);
            network.ServerManager.Broadcast(connection, new PotionContactResult { Epoch = epoch, Item = report.Item,
                Releaser = report.Releaser, Operation = report.Operation });
        }
        private void ContactResolved(PotionContactResult result, Channel channel)
        {
            if (result.Epoch == epoch) RemovePredictedCloud((result.Item, result.Releaser, result.Operation));
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
        private void AcceptContact(PotionContact report)
        {
            if (!records.TryGetValue(report.Item, out var item) || item.State != WorldItemState.World ||
                item.Operation != report.Operation || item.Releaser != report.Releaser || item.Motion.Revision < report.Revision) return;
            if (report.Cauldron >= 0 && cauldrons.TryGetValue(report.Cauldron, out var cauldron) && cauldron.Accepting)
            {
                var visual = items[report.Item];
                Admit(item, cauldron, item.Releaser, item.Operation, visual.PresentedRootPosition, visual.PresentedRotation);
                return;
            }
            if (!report.Impact || !item.Armed || GetDefinition(item.DefinitionId) is not PotionDefinition) return;
            CommitActivation(item, item.Releaser, item.Operation, report.Position, report.Cart, report.CartLifetime);
        }
        internal bool UsePotion(ItemRecord item, PlayerInventory player, uint operation, Vector3 position)
        {
            if (GetDefinition(item.DefinitionId) is not PotionDefinition || !Finite(position)) return false;
            CommitActivation(item, player.ObjectId, operation, position, -1);
            return true;
        }
        private void CommitActivation(ItemRecord item, int player, uint operation, Vector3 position, int cart, uint cartLifetime = 0)
        {
            var definition = (PotionDefinition)GetDefinition(item.DefinitionId);
            var activation = new PotionActivation { Id = ++nextEffectId, Definition = item.DefinitionId, StartTick = ServerTick,
                Expiry = ServerTick + DurationTicks(definition.Application == PotionApplication.Zone ? definition.ZoneLifetime : 0.5f),
                Player = player, Operation = operation, SourceItem = item.Motion.Id, Position = position, Cart = cart, CartLifetime = cartLifetime };
            CommitFeature(new CraftingTransition { Items = new() { Tombstone(item) }, HasActivation = true, Activation = activation });
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
            foreach (uint id in cleanup) { contacts.Remove(id); ClearPredictedClouds(id); }
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
