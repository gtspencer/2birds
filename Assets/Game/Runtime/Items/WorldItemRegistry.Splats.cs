using System.Collections.Generic;
using FishNet.Object;
using UnityEngine;

namespace TwoBirds
{
    public sealed partial class WorldItemRegistry
    {
        private readonly Dictionary<(uint Epoch, uint Item, int Releaser, uint Operation), SplatEvent> acceptedSplats = new();
        private readonly Dictionary<(uint Epoch, uint Item, int Releaser, uint Operation), SplatPresentation> predictedSplats = new();
        private readonly HashSet<(uint Epoch, uint Item, int Releaser, uint Operation)> reportedSplats = new();
        private readonly HashSet<SplatPresentation> splatPresentations = new();
        private readonly HashSet<SplatTargetLifetime> splatOwners = new();
        private SplatAttachment splatAttachment;
        private bool samplingAcceptedContact;

        private static (uint Epoch, uint Item, int Releaser, uint Operation) ContactKey(ItemContact report) =>
            (report.Epoch, report.Item, report.Releaser, report.Operation);
        private static (uint Epoch, uint Item, int Releaser, uint Operation) SplatKey(SplatEvent value) =>
            (value.Epoch, value.Item, value.Releaser, value.Operation);
        private (uint Epoch, uint Item, int Releaser, uint Operation) SplatKey(ItemRecord record) =>
            (epoch, record.Motion.Id, record.Releaser, record.Operation);

        internal ItemRecord PreserveSplatConsumption(ItemRecord record)
        {
            if (record.State != WorldItemState.World || acceptedSplats.ContainsKey(SplatKey(record))) record.SplatArmed = false;
            return record;
        }

        internal void QueueSplat(WorldItem item, Collider collider, Vector3 point, Vector3 normal)
        {
            if (Replaying || samplingAcceptedContact || !item.ReleaseAvailable || !item.Record.SplatArmed || !item.Definition.CanSpawnSplat) return;
            var key = SplatKey(item.Record);
            if (acceptedSplats.ContainsKey(key) || !reportedSplats.Add(key)) return;
            var report = ContactFor(item);
            if (!report.HasSplat)
            {
                report.HasSplat = true;
                splatAttachment.Capture(report.Item, collider, point, normal, out report.Target, out report.SplatPoint, out report.SplatRotation);
            }
            if (item.Record.Armed && !report.Impact)
            {
                var cart = collider.GetComponentInParent<GolfCartNetwork>();
                report.Impact = true;
                report.Cart = cart ? cart.ObjectId : -1;
                report.CartLifetime = cart ? cart.EffectLifetime : 0;
                report.Position = cart ? Quaternion.Inverse(cart.Controller.Body.rotation) * (point - cart.Controller.Body.position) : point;
            }
            contacts[key] = report;
            if (report.Impact) PredictContactCloud(report, item.Definition, point);
        }

        private SplatEvent BuildSplat(ItemContact report, byte definition) => new()
        {
            Epoch = report.Epoch, Item = report.Item, Releaser = report.Releaser, Operation = report.Operation,
            Definition = definition, Target = report.Target, Point = report.SplatPoint, Rotation = report.SplatRotation
        };

        private void PredictSplat(ItemContact report)
        {
            var key = ContactKey(report);
            if (!report.HasSplat || report.Cauldron >= 0 || acceptedSplats.ContainsKey(key) || predictedSplats.ContainsKey(key) ||
                !items.TryGetValue(report.Item, out var item)) return;
            var predicted = PresentSplat(BuildSplat(report, item.Record.DefinitionId));
            if (predicted) predictedSplats[key] = predicted;
        }

        private SplatPresentation PresentSplat(SplatEvent value)
        {
            var definition = GetDefinition(value.Definition);
            if (!definition || !definition.CanSpawnSplat ||
                !splatAttachment.Resolve(value.Target, out var target, out var owner)) return null;
            TrackSplatOwner(owner);
            var root = new GameObject("Splat");
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(root, worldScene);
            root.SetActive(false);
            var splat = root.AddComponent<SplatPresentation>();
            splatPresentations.Add(splat);
            splat.Disposed += SplatDisposed;
            splat.Initialize(definition.SplatDefinition);
            splat.Place(value, owner, target);
            return splat;
        }

        private void TrackSplatOwner(SplatTargetLifetime owner)
        {
            if (splatOwners.Add(owner)) owner.Destroyed += SplatOwnerDestroyed;
        }
        private void SplatOwnerDestroyed(SplatTargetLifetime owner) => splatOwners.Remove(owner);
        private void SplatDisposed(SplatPresentation splat) => splatPresentations.Remove(splat);

        private void AcceptSplat(SplatEvent value)
        {
            var key = SplatKey(value);
            if (!acceptedSplats.TryAdd(key, value)) return;
            if (records.TryGetValue(value.Item, out var record) && SplatKey(record) == key)
            {
                record.SplatArmed = false;
                records[value.Item] = record;
            }
            if (items.TryGetValue(value.Item, out var item) && SplatKey(item.Record) == key) item.ConsumeSplat();
            if (predictedSplats.Remove(key, out var predicted))
            {
                if (!predicted) return;
                if (splatAttachment.Resolve(value.Target, out var target, out var owner))
                {
                    TrackSplatOwner(owner);
                    predicted.Place(value, owner, target);
                }
                else predicted.Dispose();
            }
            else PresentSplat(value);
        }

        private void RejectSplat((uint Epoch, uint Item, int Releaser, uint Operation) key)
        {
            if (acceptedSplats.ContainsKey(key)) return;
            if (predictedSplats.Remove(key, out var splat) && splat) splat.Dispose();
        }

        internal void CancelItemContacts(uint item)
        {
            foreach (var key in new List<(uint Epoch, uint Item, int Releaser, uint Operation)>(contacts.Keys))
            {
                if (key.Item != item) continue;
                contacts.Remove(key, out var contact);
                if (contactReporters.Remove(key, out var reporters))
                    foreach (var reporter in reporters)
                        if (reporter.IsActive)
                            network.ServerManager.Broadcast(reporter, new ItemContactResult { Epoch = key.Epoch, Item = key.Item,
                                Releaser = key.Releaser, Operation = key.Operation, HasSplat = contact.HasSplat,
                                SplatAccepted = acceptedSplats.ContainsKey(key) });
            }
            foreach (var key in new List<(uint Epoch, uint Item, int Releaser, uint Operation)>(predictedSplats.Keys))
                if (key.Item == item) RejectSplat(key);
            reportedSplats.RemoveWhere(key => key.Item == item);
        }

        internal bool TryGetSplatNetworkObject(int id, out NetworkObject target) =>
            (IsHost ? network.ServerManager.Objects.Spawned : network.ClientManager.Objects.Spawned).TryGetValue(id, out target);

        private void SplatNetworkRemoved(int id, NetworkObject target)
        {
            if (target) SplatTargetLifetime.Invalidate(target.transform);
        }

        private void BeginSplatWorld(UnityEngine.SceneManagement.Scene scene)
        {
            splatAttachment = new SplatAttachment(this, scene);
            var objects = IsHost ? (FishNet.Managing.Object.ManagedObjects)network.ServerManager.Objects : network.ClientManager.Objects;
            objects.OnSpawnedRemove += SplatNetworkRemoved;
            objects.OnSpawnedClear += ClearSplatTargets;
        }

        private void ClearSplatTargets()
        {
            foreach (var owner in new List<SplatTargetLifetime>(splatOwners)) if (owner) owner.Clear();
        }

        private void EndSplatWorld()
        {
            network.ServerManager.Objects.OnSpawnedRemove -= SplatNetworkRemoved;
            network.ClientManager.Objects.OnSpawnedRemove -= SplatNetworkRemoved;
            network.ServerManager.Objects.OnSpawnedClear -= ClearSplatTargets;
            network.ClientManager.Objects.OnSpawnedClear -= ClearSplatTargets;
            foreach (var splat in new List<SplatPresentation>(splatPresentations)) if (splat) splat.Dispose();
            foreach (var owner in splatOwners)
                if (owner) { owner.Destroyed -= SplatOwnerDestroyed; Destroy(owner); }
            splatOwners.Clear(); splatPresentations.Clear();
            predictedSplats.Clear(); acceptedSplats.Clear(); reportedSplats.Clear(); contactReporters.Clear();
            splatAttachment = null;
        }
    }
}
