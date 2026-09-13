using System;
using System.Collections.Generic;

namespace TwoBirds
{
    [Serializable]
    public struct InventoryEntry
    {
        public string DefinitionId;
        public int Quantity;
        public ulong EntryId;
        public bool IsEmpty => EntryId == 0;
    }
    public struct InventorySnapshot
    {
        public ulong Revision;
        public InventoryEntry[] Slots;
        public InventoryEntry Get(int index) => Slots != null && index >= 0 && index < Slots.Length ? Slots[index] : default;
        public InventorySnapshot Copy() => new() { Revision = Revision, Slots = Slots == null ? null : (InventoryEntry[])Slots.Clone() };
    }
    public enum InventoryStatus : byte { Success, NoChange, Invalid, Stale, Full, DropUnavailable, TransferFailed }
    public struct InventoryAddResult
    {
        public int Accepted, Remainder;
        public ulong Revision;
        public InventoryStatus Status;
    }
    public interface IInventoryDropSink
    {
        // Failure must have no side effects; success takes ownership synchronously.
        bool TryAccept(int playerId, InventoryEntry entry);
    }
    public sealed class InventoryModel : IDisposable
    {
        public const int SlotCount = 24, HotbarCount = 8;
        private static ulong nextEntryId;
        private static readonly HashSet<ulong> liveIds = new();
        private readonly ItemCatalog catalog;
        private InventoryEntry[] slots = new InventoryEntry[SlotCount];
        private bool transaction;
        public ulong Revision { get; private set; } = 1;
        public InventorySnapshot Snapshot => new() { Revision = Revision, Slots = (InventoryEntry[])slots.Clone() };
        public InventoryModel(ItemCatalog catalog) { this.catalog = catalog; catalog.Validate(); }

        public InventoryAddResult Add(string definitionId, int quantity, ulong uniqueId = 0)
        {
            var result = new InventoryAddResult { Remainder = quantity, Revision = Revision, Status = InventoryStatus.Invalid };
            var definition = catalog.Resolve(definitionId);
            if (transaction || definition == null || quantity <= 0 || Revision == ulong.MaxValue ||
                (uniqueId != 0 && (definition.MaximumStack != 1 || quantity != 1))) return result;
            if (uniqueId != 0 && liveIds.Contains(uniqueId)) return result;
            var staged = (InventoryEntry[])slots.Clone();
            ulong stagedId = Math.Max(nextEntryId, uniqueId);
            int remaining = quantity;
            for (int group = 0; group < 4 && remaining > 0; group++)
            {
                int start = group < 2 ? 0 : HotbarCount, end = group < 2 ? HotbarCount : SlotCount;
                bool empty = group % 2 == 1;
                for (int i = start; i < end && remaining > 0; i++)
                {
                    var entry = staged[i];
                    if (empty ? !entry.IsEmpty : entry.IsEmpty || entry.DefinitionId != definitionId || definition.MaximumStack == 1) continue;
                    int amount = Math.Min(remaining, definition.MaximumStack - entry.Quantity);
                    if (amount == 0) continue;
                    if (entry.IsEmpty)
                    {
                        if (uniqueId == 0 && stagedId == ulong.MaxValue) return result;
                        entry.EntryId = uniqueId != 0 ? uniqueId : ++stagedId;
                        entry.DefinitionId = definitionId;
                    }
                    entry.Quantity += amount;
                    staged[i] = entry;
                    remaining -= amount;
                }
            }
            result.Accepted = quantity - remaining;
            result.Remainder = remaining;
            result.Status = result.Accepted > 0 ? InventoryStatus.Success : InventoryStatus.Full;
            if (result.Accepted > 0) { Commit(staged); nextEntryId = stagedId; }
            result.Revision = Revision;
            return result;
        }

        private void Commit(InventoryEntry[] staged)
        {
            foreach (var entry in slots) if (!entry.IsEmpty) liveIds.Remove(entry.EntryId);
            foreach (var entry in staged) if (!entry.IsEmpty) liveIds.Add(entry.EntryId);
            slots = staged;
            ++Revision;
        }
        public void Dispose()
        {
            if (transaction) throw new InvalidOperationException("Inventory transfer in progress");
            foreach (var entry in slots) if (!entry.IsEmpty) liveIds.Remove(entry.EntryId);
            slots = new InventoryEntry[SlotCount];
        }

        public InventoryStatus Mutate(ulong revision, int source, ulong entryId, int destination, bool drop, IInventoryDropSink sink, int playerId)
        {
            if (transaction || source < 0 || source >= SlotCount || (!drop && (destination < 0 || destination >= SlotCount))) return InventoryStatus.Invalid;
            if (revision != Revision) return InventoryStatus.Stale;
            var entry = slots[source];
            if (entry.IsEmpty) return entryId == 0 ? InventoryStatus.NoChange : InventoryStatus.Invalid;
            if (entry.EntryId != entryId || Revision == ulong.MaxValue) return InventoryStatus.Invalid;
            if (!drop && source == destination) return InventoryStatus.NoChange;
            var staged = (InventoryEntry[])slots.Clone();
            if (drop)
            {
                if (sink == null) return InventoryStatus.DropUnavailable;
                transaction = true;
                try { if (!sink.TryAccept(playerId, entry)) return InventoryStatus.TransferFailed; }
                catch { return InventoryStatus.TransferFailed; }
                finally { transaction = false; }
                staged[source] = default;
            }
            else
            {
                var target = staged[destination];
                var definition = catalog.Resolve(entry.DefinitionId);
                if (!target.IsEmpty && target.DefinitionId == entry.DefinitionId && definition.MaximumStack > 1)
                {
                    int amount = Math.Min(entry.Quantity, definition.MaximumStack - target.Quantity);
                    if (amount == 0) return InventoryStatus.Full;
                    target.Quantity += amount;
                    entry.Quantity -= amount;
                    staged[source] = entry.Quantity == 0 ? default : entry;
                    staged[destination] = target;
                }
                else { staged[source] = target; staged[destination] = entry; }
            }
            Commit(staged);
            return InventoryStatus.Success;
        }
    }
}
