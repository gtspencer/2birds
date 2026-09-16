using System;
using System.Collections.Generic;
using FishNet.Connection;
using FishNet.Object;
using UnityEngine;

namespace TwoBirds
{
    public enum InventoryOperation : byte { Pickup, Release, Select, Swap }
    public enum ItemReleaseIntent : byte { Drop, Throw }

    public struct InventoryRequest
    {
        public uint Operation;
        public InventoryOperation Kind;
        public uint SeatingRevision;
        public ItemReleaseIntent ReleaseIntent;
        public bool AutoSelect;
        public int From;
        public int To;
        public byte DefinitionId;
        public uint[] Ids;
        public ItemMotion[] Releases;
    }

    public sealed class PlayerInventory : NetworkBehaviour
    {
        public const int SlotCount = 24;
        public const int HotbarSize = 8;
        private readonly ItemStack[] serverSlots = new ItemStack[SlotCount];
        private readonly ItemStack[] viewSlots = new ItemStack[SlotCount];
        private readonly List<InventoryRequest> pending = new();
        private ItemStack[] confirmedSlots = new ItemStack[SlotCount];
        private WorldItemRegistry registry;
        private PlayerMotor motor;
        private PlayerSeating seating;
        private PlayerPresentation presentation;
        private uint nextOperation;
        private uint lastOperation;
        private uint serverRevision;
        private uint receivedRevision;
        private sbyte serverSelection = -1;
        private sbyte confirmedSelection = -1;
        private sbyte viewSelection = -1;
        public PlayerEquipment Equipment { get; private set; }
        public PlayerItemHitbox Hitbox { get; private set; }
        public int Count => SlotCount;
        public bool CanEquip => seating == null || seating.CanEquip;
        public sbyte SelectedSlot => !CanEquip ? (sbyte)-1 : IsServerInitialized && !IsOwner ? serverSelection : viewSelection;
        public event Action InventoryChanged;

        private void Awake()
        {
            motor = GetComponent<PlayerMotor>();
            seating = GetComponent<PlayerSeating>();
            presentation = GetComponent<PlayerPresentation>();
            Equipment = GetComponent<PlayerEquipment>();
            Hitbox = GetComponentInChildren<PlayerItemHitbox>(true);
        }

        public override void OnStartNetwork()
        {
            registry = WorldItemRegistry.Instance;
            Hitbox.transform.SetParent(null, true);
            registry.RegisterPlayer(this);
        }

        public override void OnStartClient()
        {
            NetworkObject.RigidbodyPauser.UpdateRigidbodies(new[] { motor.Body });
            registry.RegisterPlayer(this);
        }

        public override void OnSpawnServer(NetworkConnection connection)
        {
            if (connection == Owner) ReplyInventory(true, 0);
        }

        public override void OnOwnershipClient(NetworkConnection previousOwner) => registry.RegisterPlayer(this);

        public ItemStack GetSlot(int index)
        {
            if (index < 0 || index >= SlotCount) return default;
            return IsServerInitialized && !IsOwner ? serverSlots[index] : viewSlots[index];
        }

        public ItemStack GetEquipped() => GetSlot(SelectedSlot);

        public void SelectSlot(sbyte slot)
        {
            if (!CanEquip || slot < -1 || slot >= HotbarSize) return;
            Submit(new InventoryRequest { Kind = InventoryOperation.Select, From = viewSelection == slot ? -1 : slot });
        }

        public void SwapSlots(int from, int to)
        {
            if (from < 0 || from >= SlotCount || to < 0 || to >= SlotCount || from == to) return;
            Submit(new InventoryRequest { Kind = InventoryOperation.Swap, From = from, To = to });
        }

        public void Collect(WorldItem item)
        {
            if (!IsOwner || !item.CanInteract) return;
            var request = new InventoryRequest { Kind = InventoryOperation.Pickup, DefinitionId = item.Record.DefinitionId,
                Ids = new[] { item.Record.Motion.Id } };
            if (FindSpace(viewSlots, request.DefinitionId) < 0) return;
            if (!IsServerInitialized) item.PredictPickup();
            Submit(request);
        }

        public void DropSelected() => DropSlot(SelectedSlot, false);
        public void DropSlot(int index) => DropSlot(index, true);

        private void DropSlot(int index, bool wholeStack)
        {
            if (!IsOwner) return;
            Equipment.CancelUse();
            var stack = GetSlot(index);
            if (!stack.IsEmpty) ReleaseSlot(index, registry.GetDefinition(stack.ItemId).DropSpeed, wholeStack, ItemReleaseIntent.Drop);
        }

        public void ReleaseEquipped(uint id, float launchSpeed)
        {
            if (!IsOwner || !CanEquip) return;
            var equipped = GetEquipped();
            if (equipped.IsEmpty || equipped.WorldIds[0] != id) return;
            ReleaseSlot(SelectedSlot, launchSpeed, false, ItemReleaseIntent.Throw);
        }

        private void ReleaseSlot(int index, float launchSpeed, bool wholeStack, ItemReleaseIntent intent)
        {
            if (!IsOwner || intent == ItemReleaseIntent.Throw && !CanEquip ||
                seating != null && (seating.TransitionPending || seating.PlacementPending)) return;
            var stack = GetSlot(index);
            if (stack.IsEmpty) return;
            int count = wholeStack ? stack.Count : 1;
            var ids = new uint[count];
            Array.Copy(stack.WorldIds, ids, count);
            var definition = registry.GetDefinition(stack.ItemId);
            var aimPose = presentation.AimPose;
            var aim = aimPose.rotation;
            Vector3 forward = aim * Vector3.forward;
            Vector3 origin = aimPose.position;
            Vector3 position = origin + forward * 0.65f;
            if (Physics.SphereCast(origin, 0.12f, forward, out var wall, 0.65f, registry.EnvironmentMask, QueryTriggerInteraction.Ignore))
                position = origin + forward * Mathf.Max(0f, wall.distance - 0.01f);
            var releases = new ItemMotion[count];
            for (int i = 0; i < count; i++)
            {
                // Spread stack drops to avoid spawning mutually overlapping bodies.
                Vector3 offset = aim * new Vector3((i % 4 - (Mathf.Min(count, 4) - 1) * 0.5f) * 0.24f, (i / 4) * 0.24f, 0f);
                Vector3 releasePosition = position + offset;
                if (offset != Vector3.zero && Physics.SphereCast(position, 0.12f, offset.normalized, out wall,
                    offset.magnitude, registry.EnvironmentMask, QueryTriggerInteraction.Ignore))
                    releasePosition = position + offset.normalized * Mathf.Max(0f, wall.distance - 0.01f);
                releases[i] = new ItemMotion { Id = ids[i], Position = releasePosition,
                    Rotation = aim * definition.WorldPrefab.transform.localRotation,
                    Velocity = forward * launchSpeed + (seating != null ? seating.PointVelocity : motor.Body.linearVelocity) * definition.VelocityInheritance,
                    AngularVelocity = aim * definition.InitialSpin };
            }
            Submit(new InventoryRequest { Kind = InventoryOperation.Release, From = index, DefinitionId = stack.ItemId,
                Ids = ids, Releases = releases, ReleaseIntent = intent });
        }

        private void Submit(InventoryRequest request)
        {
            if (!IsOwner) return;
            request.Operation = ++nextOperation;
            request.SeatingRevision = seating != null ? seating.Revision : 0;
            request.AutoSelect = CanEquip;
            pending.Add(request);
            if (request.Kind == InventoryOperation.Release)
                for (int i = 0; i < request.Ids.Length; i++)
                    registry.PredictRelease(request.Ids[i], request.Operation, request.Releases[i], this);
            RebuildView();
            if (IsServerInitialized) ProcessRequest(request);
            else CmdOperate(request);
        }

        [ServerRpc]
        private void CmdOperate(InventoryRequest request) => ProcessRequest(request);

        private void ProcessRequest(InventoryRequest request)
        {
            if (request.Operation <= lastOperation)
            {
                ReplyInventory(true, 0);
                return;
            }
            bool accepted = Commit(request);
            lastOperation = request.Operation;
            serverRevision++;
            registry.UpdateEquipment(this, EquippedId(serverSlots, serverSelection));
            ReplyInventory(accepted, request.Operation);
        }

        private bool Commit(InventoryRequest request)
        {
            if (request.Kind == InventoryOperation.Select || request.Kind == InventoryOperation.Release)
            {
                if (seating != null && request.SeatingRevision != seating.Revision) return false;
                if (!CanEquip && (request.Kind == InventoryOperation.Select || request.ReleaseIntent != ItemReleaseIntent.Drop)) return false;
            }
            switch (request.Kind)
            {
                case InventoryOperation.Select:
                    if (request.From < -1 || request.From >= HotbarSize) return false;
                    serverSelection = (sbyte)request.From;
                    return true;
                case InventoryOperation.Swap:
                    if (request.From < 0 || request.From >= SlotCount || request.To < 0 || request.To >= SlotCount) return false;
                    (serverSlots[request.From], serverSlots[request.To]) = (serverSlots[request.To], serverSlots[request.From]);
                    return true;
                case InventoryOperation.Pickup:
                    if (request.Ids == null || request.Ids.Length != 1 || !registry.TryGetRecord(request.Ids[0], out var item) ||
                        item.State != WorldItemState.World || item.DefinitionId != request.DefinitionId) return false;
                    int slot = FindSpace(serverSlots, request.DefinitionId);
                    if (slot < 0) return false;
                    AddId(serverSlots, slot, item.DefinitionId, request.Ids[0]);
                    if (CanEquip && request.AutoSelect && (seating == null || request.SeatingRevision == seating.Revision) &&
                        serverSelection < 0 && slot < HotbarSize) serverSelection = (sbyte)slot;
                    registry.SetHeld(request.Ids[0], this, EquippedId(serverSlots, serverSelection) == request.Ids[0]);
                    return true;
                case InventoryOperation.Release:
                    if (request.Ids == null || request.Ids.Length == 0 || request.Releases == null ||
                        request.Releases.Length != request.Ids.Length || request.From < 0 || request.From >= SlotCount) return false;
                    var stack = serverSlots[request.From];
                    if (stack.ItemId != request.DefinitionId || stack.Count < request.Ids.Length) return false;
                    for (int i = 0; i < request.Ids.Length; i++)
                    {
                        if (Array.IndexOf(stack.WorldIds, request.Ids[i]) < 0 || Array.IndexOf(request.Ids, request.Ids[i]) != i ||
                            !registry.TryGetRecord(request.Ids[i], out var record) || record.State != WorldItemState.Held || record.Holder != ObjectId ||
                            !ValidRelease(request.Releases[i])) return false;
                    }
                    foreach (uint id in request.Ids) RemoveId(serverSlots, id);
                    for (int i = 0; i < request.Ids.Length; i++) registry.Release(request.Ids[i], request.Operation, request.Releases[i], this);
                    return true;
            }
            return false;
        }

        private static bool ValidRelease(ItemMotion motion) => WorldItemRegistry.Finite(motion.Position) &&
            WorldItemRegistry.Finite(motion.Velocity) && WorldItemRegistry.Finite(motion.AngularVelocity) &&
            float.IsFinite(motion.Rotation.x) && float.IsFinite(motion.Rotation.y) && float.IsFinite(motion.Rotation.z) &&
            float.IsFinite(motion.Rotation.w) && Quaternion.Dot(motion.Rotation, motion.Rotation) > 0.5f;

        private void ReplyInventory(bool accepted, uint operation)
        {
            if (IsOwner) AcceptInventory(serverRevision, lastOperation, serverSlots, serverSelection, accepted, operation);
            else if (Owner.IsActive) TargetInventory(Owner, serverRevision, lastOperation, serverSlots, serverSelection, accepted, operation);
        }

        [TargetRpc]
        private void TargetInventory(NetworkConnection target, uint revision, uint acknowledged, ItemStack[] slots, sbyte selected, bool accepted, uint operation)
        {
            if (!IsServerInitialized) AcceptInventory(revision, acknowledged, slots, selected, accepted, operation);
        }

        private void AcceptInventory(uint revision, uint acknowledged, ItemStack[] slots, sbyte selected, bool accepted, uint operation)
        {
            if (revision < receivedRevision) return;
            receivedRevision = revision;
            confirmedSlots = (ItemStack[])slots.Clone();
            confirmedSelection = selected;
            for (int i = pending.Count - 1; i >= 0; i--)
            {
                var request = pending[i];
                if (request.Operation > acknowledged) continue;
                if (!accepted && request.Operation == operation && request.Ids != null)
                    foreach (uint id in request.Ids) registry.Rollback(id, request.Operation);
                pending.RemoveAt(i);
            }
            RebuildView();
        }

        private void RebuildView()
        {
            Array.Copy(confirmedSlots, viewSlots, SlotCount);
            viewSelection = confirmedSelection;
            foreach (var request in pending)
            {
                if (seating != null && request.SeatingRevision != seating.Revision &&
                    (request.Kind == InventoryOperation.Select || request.Kind == InventoryOperation.Release)) continue;
                switch (request.Kind)
                {
                    case InventoryOperation.Select:
                        viewSelection = (sbyte)request.From;
                        break;
                    case InventoryOperation.Swap:
                        (viewSlots[request.From], viewSlots[request.To]) = (viewSlots[request.To], viewSlots[request.From]);
                        break;
                    case InventoryOperation.Pickup:
                        int slot = FindSpace(viewSlots, request.DefinitionId);
                        if (slot < 0) break;
                        AddId(viewSlots, slot, request.DefinitionId, request.Ids[0]);
                        if (CanEquip && request.AutoSelect && (seating == null || request.SeatingRevision == seating.Revision) &&
                            viewSelection < 0 && slot < HotbarSize) viewSelection = (sbyte)slot;
                        break;
                    case InventoryOperation.Release:
                        foreach (uint id in request.Ids) RemoveId(viewSlots, id);
                        break;
                }
            }
            if (!CanEquip) viewSelection = -1;
            RefreshHeldPresentation();
            InventoryChanged?.Invoke();
        }

        internal void ApplySeatPermissions()
        {
            Equipment.CancelUse();
            confirmedSelection = viewSelection = -1;
            if (IsServerInitialized)
            {
                serverSelection = -1;
                serverRevision++;
                registry.UpdateEquipment(this, 0);
                ReplyInventory(true, 0);
            }
            foreach (var request in pending)
                if (request.Kind == InventoryOperation.Release && request.SeatingRevision != seating.Revision)
                    foreach (uint id in request.Ids) registry.Rollback(id, request.Operation);
            RebuildView();
            registry.RefreshHolders();
        }

        internal void RefreshHeldPresentation()
        {
            if (!IsOwner) return;
            uint equipped = EquippedId(viewSlots, viewSelection);
            foreach (var stack in viewSlots)
            {
                if (stack.IsEmpty) continue;
                foreach (uint id in stack.WorldIds)
                    if (registry.TryGetItem(id, out var item) && item.Definition != null &&
                        (!IsServerInitialized || item.Record.State == WorldItemState.Held)) item.PresentHeld(this, id == equipped);
            }
        }

        private int FindSpace(ItemStack[] slots, byte definitionId)
        {
            var definition = registry.GetDefinition(definitionId);
            if (definition == null) return -1;
            if (definition.Stackable)
                for (int i = 0; i < SlotCount; i++)
                    if (slots[i].ItemId == definitionId && slots[i].Count < definition.MaxStack) return i;
            for (int i = 0; i < SlotCount; i++)
                if (slots[i].IsEmpty) return i;
            return -1;
        }

        private static void AddId(ItemStack[] slots, int index, byte definitionId, uint id)
        {
            var previous = slots[index];
            var ids = new uint[previous.Count + 1];
            if (!previous.IsEmpty) Array.Copy(previous.WorldIds, ids, previous.Count);
            ids[ids.Length - 1] = id;
            slots[index] = new ItemStack(definitionId, ids);
        }

        private static void RemoveId(ItemStack[] slots, uint id)
        {
            for (int i = 0; i < SlotCount; i++)
            {
                if (slots[i].IsEmpty) continue;
                int index = Array.IndexOf(slots[i].WorldIds, id);
                if (index < 0) continue;
                var previous = slots[i];
                if (previous.Count == 1) slots[i] = default;
                else
                {
                    var ids = new uint[previous.Count - 1];
                    Array.Copy(previous.WorldIds, 0, ids, 0, index);
                    Array.Copy(previous.WorldIds, index + 1, ids, index, previous.Count - index - 1);
                    slots[i] = new ItemStack(previous.ItemId, ids);
                }
                return;
            }
        }

        private static uint EquippedId(ItemStack[] slots, int selected) =>
            selected < 0 || slots[selected].IsEmpty ? 0 : slots[selected].WorldIds[0];

        public override void OnStopNetwork()
        {
            registry?.UnregisterPlayer(this);
            if (Hitbox != null) Destroy(Hitbox.gameObject);
            pending.Clear();
        }
    }
}
