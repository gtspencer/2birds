using System;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace TwoBirds.Tests
{
    public sealed class InventoryModelTests
    {
        private ItemCatalog catalog;
        private ItemDefinition stack, unique, other;
        private InventoryModel model;
        [SetUp] public void SetUp()
        {
            stack = Definition("stack", 10); unique = Definition("unique", 1); other = Definition("other", 10);
            catalog = ScriptableObject.CreateInstance<ItemCatalog>();
            catalog.Definitions = new[] { stack, unique, other };
            model = new InventoryModel(catalog);
        }
        private static ItemDefinition Definition(string id, int maximum)
        {
            var result = ScriptableObject.CreateInstance<ItemDefinition>();
            result.DefinitionId = id; result.DisplayName = id; result.MaximumStack = maximum;
            return result;
        }
        [TearDown] public void TearDown()
        {
            model.Dispose();
            foreach (var asset in new UnityEngine.Object[] { catalog, stack, unique, other }) UnityEngine.Object.DestroyImmediate(asset);
        }
        private InventoryStatus Move(int from, int to) => model.Mutate(model.Revision, from, model.Snapshot.Get(from).EntryId, to, false, null, 42);
        private int Total() => model.Snapshot.Slots.Sum(s => s.Quantity);
        [Test] public void InsertionUsesAllFourPriorityGroups()
        {
            model.Add("stack", 5); Move(0, 8);
            model.Add("stack", 3);
            Assert.That(model.Snapshot.Get(0).Quantity, Is.EqualTo(3));
            Assert.That(model.Snapshot.Get(8).Quantity, Is.EqualTo(5));
            model.Add("stack", 80);
            Assert.That(model.Snapshot.Get(0).Quantity, Is.EqualTo(10));
            Assert.That(model.Snapshot.Get(7).Quantity, Is.EqualTo(10));
            Assert.That(model.Snapshot.Get(8).Quantity, Is.EqualTo(8));
            model.Add("stack", 7);
            Assert.That(model.Snapshot.Get(8).Quantity, Is.EqualTo(10));
            Assert.That(model.Snapshot.Get(9).Quantity, Is.EqualTo(5));
            Assert.That(Total(), Is.EqualTo(95));
        }
        [Test] public void CapacityReturnsRemainderAndDoesNotOverflow()
        {
            var result = model.Add("stack", int.MaxValue);
            Assert.That(result.Accepted, Is.EqualTo(240));
            Assert.That(result.Remainder, Is.EqualTo(int.MaxValue - 240));
            ulong revision = model.Revision;
            Assert.That(model.Add("stack", 1).Accepted, Is.Zero);
            Assert.That(model.Revision, Is.EqualTo(revision));
        }
        [Test] public void MovesSwapsAndMergesConserveQuantityAndIdentity()
        {
            model.Add("stack", 15);
            var first = model.Snapshot.Get(0); var second = model.Snapshot.Get(1);
            Assert.That(Move(0, 1), Is.EqualTo(InventoryStatus.Success));
            Assert.That(model.Snapshot.Get(0).Quantity, Is.EqualTo(5));
            Assert.That(model.Snapshot.Get(0).EntryId, Is.EqualTo(first.EntryId));
            Assert.That(model.Snapshot.Get(1).EntryId, Is.EqualTo(second.EntryId));
            Assert.That(Move(0, 1), Is.EqualTo(InventoryStatus.Full));
            Move(0, 2);
            Assert.That(model.Snapshot.Get(2).EntryId, Is.EqualTo(first.EntryId));
            model.Add("other", 1);
            var otherId = model.Snapshot.Get(0).EntryId;
            Move(0, 2);
            Assert.That(model.Snapshot.Get(0).EntryId, Is.EqualTo(first.EntryId));
            Assert.That(model.Snapshot.Get(2).EntryId, Is.EqualTo(otherId));
            Assert.That(Total(), Is.EqualTo(16));
        }
        [Test] public void CompleteMergeRetiresOnlySourceIdentity()
        {
            model.Add("stack", 3); Move(0, 8); model.Add("stack", 4);
            ulong destination = model.Snapshot.Get(8).EntryId;
            Move(0, 8);
            Assert.That(model.Snapshot.Get(0).IsEmpty, Is.True);
            Assert.That(model.Snapshot.Get(0).DefinitionId, Is.Null);
            Assert.That(model.Snapshot.Get(8).EntryId, Is.EqualTo(destination));
            Assert.That(Total(), Is.EqualTo(7));
        }
        [Test] public void UniqueEntriesSwapAndDuplicateIdentityIsRejected()
        {
            model.Add("unique", 2);
            var ids = model.Snapshot.Slots.Where(x => !x.IsEmpty).Select(x => x.EntryId).ToArray();
            Assert.That(ids[0], Is.Not.EqualTo(ids[1]));
            Assert.That(model.Add("unique", 1, ids[0]).Status, Is.EqualTo(InventoryStatus.Invalid));
            Move(0, 1);
            Assert.That(model.Snapshot.Get(1).EntryId, Is.EqualTo(ids[0]));
            Assert.That(model.Snapshot.Get(0).EntryId, Is.EqualTo(ids[1]));
            Assert.That(Total(), Is.EqualTo(2));
        }
        [Test] public void InvalidAndStaleOperationsPreserveState()
        {
            model.Add("stack", 5); var before = model.Snapshot;
            Assert.That(model.Add("missing", 1).Accepted, Is.Zero);
            Assert.That(model.Add("stack", 0).Accepted, Is.Zero);
            Assert.That(model.Add("stack", -1).Accepted, Is.Zero);
            Assert.That(model.Mutate(before.Revision, -1, 0, 1, false, null, 42), Is.EqualTo(InventoryStatus.Invalid));
            Assert.That(model.Mutate(0, 0, before.Get(0).EntryId, 1, false, null, 42), Is.EqualTo(InventoryStatus.Stale));
            Assert.That(model.Mutate(before.Revision, 0, ulong.MaxValue, 1, false, null, 42), Is.EqualTo(InventoryStatus.Invalid));
            Assert.That(Move(0, 0), Is.EqualTo(InventoryStatus.NoChange));
            Assert.That(model.Revision, Is.EqualTo(before.Revision));
            Assert.That(model.Snapshot.Slots, Is.EqualTo(before.Slots));
        }
        private sealed class Sink : IInventoryDropSink
        {
            public Func<int, InventoryEntry, bool> Accept;
            public int Calls;
            public bool TryAccept(int player, InventoryEntry entry) { Calls++; return Accept(player, entry); }
        }
        [TestCase(false, false)] [TestCase(false, true)] [TestCase(true, false)]
        public void DropIsAtomicAndBlocksReentrancy(bool accept, bool throws)
        {
            model.Add("unique", 1); var before = model.Snapshot;
            var sink = new Sink { Accept = (player, entry) => {
                Assert.That(player, Is.EqualTo(42)); Assert.That(entry, Is.EqualTo(before.Get(0)));
                Assert.That(model.Add("stack", 1).Status, Is.EqualTo(InventoryStatus.Invalid));
                Assert.That(Move(0, 1), Is.EqualTo(InventoryStatus.Invalid));
                if (throws) throw new InvalidOperationException("fixture");
                return accept;
            } };
            var status = model.Mutate(before.Revision, 0, before.Get(0).EntryId, -1, true, sink, 42);
            Assert.That(status, Is.EqualTo(accept ? InventoryStatus.Success : InventoryStatus.TransferFailed));
            Assert.That(Total(), Is.EqualTo(accept ? 0 : 1));
            Assert.That(model.Revision, Is.EqualTo(before.Revision + (accept ? 1ul : 0ul)));
            Assert.That(sink.Calls, Is.EqualTo(1));
        }
        [Test] public void MissingSinkPreservesEntryAndSnapshotsAreDetached()
        {
            model.Add("stack", 5); var snapshot = model.Snapshot;
            Assert.That(model.Mutate(snapshot.Revision, 0, snapshot.Get(0).EntryId, -1, true, null, 42), Is.EqualTo(InventoryStatus.DropUnavailable));
            snapshot.Slots[0] = default;
            Assert.That(Total(), Is.EqualTo(5));
        }
        [Test] public void CatalogRejectsDuplicatesMissingIdsAndInvalidLimits()
        {
            unique.DefinitionId = stack.DefinitionId;
            Assert.Throws<InvalidOperationException>(() => catalog.Validate());
            unique.DefinitionId = "";
            Assert.Throws<InvalidOperationException>(() => catalog.Validate());
            unique.DefinitionId = "unique"; unique.MaximumStack = 0;
            Assert.Throws<InvalidOperationException>(() => catalog.Validate());
        }
        [Test] public void UniqueTransferKeepsIdentityAndCannotExistInTwoInventories()
        {
            model.Add("unique", 1);
            var before = model.Snapshot;
            using var receiver = new InventoryModel(catalog);
            Assert.That(receiver.Add("unique", 1, before.Get(0).EntryId).Status, Is.EqualTo(InventoryStatus.Invalid));
            var sink = new Sink { Accept = (_, _) => true };
            Assert.That(model.Mutate(before.Revision, 0, before.Get(0).EntryId, -1, true, sink, 42), Is.EqualTo(InventoryStatus.Success));
            Assert.That(model.Mutate(before.Revision, 0, before.Get(0).EntryId, -1, true, sink, 42), Is.EqualTo(InventoryStatus.Stale));
            Assert.That(sink.Calls, Is.EqualTo(1));
            Assert.That(receiver.Add("unique", 1, before.Get(0).EntryId).Accepted, Is.EqualTo(1));
            Assert.That(receiver.Snapshot.Get(0).EntryId, Is.EqualTo(before.Get(0).EntryId));
        }
    }
}
