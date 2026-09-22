using System;
using System.Collections.Generic;
using FishNet.Connection;
using FishNet.Object;
using UnityEngine;

namespace TwoBirds
{
    public enum InventoryOperation : byte { Pickup, Release, Select, Swap, Insert, DirectUse, Brew, Dispose }
    public enum ItemReleaseIntent : byte { Drop, Throw }

    public struct InventoryRequest
    {
        public uint Operation;
        public InventoryOperation Kind;
        public uint ControlRevision;
        public ItemReleaseIntent ReleaseIntent;
        public bool AutoSelect;
        public int From;
        public int To;
        public uint ExpectedRevision, CauldronRevision, Epoch, ExpectedOperation;
        public int ExpectedReleaser;
        public Vector3 Position;
        public Quaternion Rotation;
        public byte DefinitionId;
        public uint[] Ids;
        public ItemMotion[] Releases;
        public ushort ActionSequence;
        public uint ActionStartedTick;
        public byte ActionStartedFraction, ReleaseArcProgress;
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
        private PlayerCarry carry;
        private PlayerPresentation presentation;
        private PlayerNetworkState networkState;
        private uint nextOperation;
        private uint lastOperation;
        private uint serverRevision;
        private uint receivedRevision;
        private sbyte serverSelection = -1;
        private sbyte confirmedSelection = -1;
        private sbyte viewSelection = -1;
        public PlayerPotionEffects Effects { get; private set; }
        public PlayerEquipment Equipment { get; private set; }
        public PlayerItemHitbox Hitbox { get; private set; }
        public int Count => SlotCount;
        public bool CanAct => networkState.CanGameplayActions && (!carry || !carry.IsCarried) && (!seating || !seating.PlacementPending && !seating.AwaitingReference);
        public bool CanCraft => CanEquip && (!carry || carry.Role == CarryRole.Free) && (!seating || !seating.TransitionPending);
        public bool CanEquip => CanAct && (!seating || seating.CanEquip);
        public sbyte SelectedSlot => !CanEquip ? (sbyte)-1 : IsServerInitialized && !IsOwner ? serverSelection : viewSelection;
        public event Action InventoryChanged;
        public event Action ControlPermissionsChanged;

        private void Awake()
        {
            Effects = GetComponent<PlayerPotionEffects>();
            motor = GetComponent<PlayerMotor>();
            seating = GetComponent<PlayerSeating>();
            carry = GetComponent<PlayerCarry>();
            presentation = GetComponent<PlayerPresentation>();
            networkState = GetComponent<PlayerNetworkState>();
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
            if (!CanAct || !CanEquip || slot < -1 || slot >= HotbarSize) return;
            Equipment.CancelUse();
            Submit(new InventoryRequest { Kind = InventoryOperation.Select, From = viewSelection == slot ? -1 : slot });
        }

        public void SwapSlots(int from, int to)
        {
            if (!CanAct || from < 0 || from >= SlotCount || to < 0 || to >= SlotCount || from == to) return;
            Submit(new InventoryRequest { Kind = InventoryOperation.Swap, From = from, To = to });
        }

        public void Collect(WorldItem item)
        {
            if (!IsOwner || !CanAct || !item.CanInteract) return;
            var request = new InventoryRequest { Kind = InventoryOperation.Pickup, DefinitionId = item.Record.DefinitionId,
                Ids = new[] { item.Record.Motion.Id }, ExpectedRevision = item.Record.Motion.Revision,
                ExpectedReleaser = item.Record.Releaser, ExpectedOperation = item.Record.Operation };
            if (FindSpace(viewSlots, request.DefinitionId) < 0) return;
            if (!IsServerInitialized) item.PredictPickup();
            Submit(request);
        }

        public void CauldronAction(Cauldron cauldron, InventoryOperation operation)
        {
            if (!IsOwner || !CanCraft) return;
            Equipment.CancelUse();
            if (operation == InventoryOperation.Insert) ConsumeEquipped(operation, cauldron);
            else if (GetEquipped().IsEmpty) Submit(new InventoryRequest { Kind = operation, To = cauldron.ObjectId,
                CauldronRevision = cauldron.State.Revision });
        }

        internal bool DirectUse()
        {
            if (!IsOwner || !CanCraft || !Equipment.HeldPresentation.ReadyForUse) return false;
            return ConsumeEquipped(InventoryOperation.DirectUse, null);
        }

        private bool ConsumeEquipped(InventoryOperation operation, Cauldron cauldron)
        {
            var stack = GetEquipped();
            if (stack.IsEmpty || !registry.TryGetItem(stack.WorldIds[0], out var item)) return false;
            if (operation == InventoryOperation.DirectUse && item.Definition is not PotionDefinition) return false;
            if (operation == InventoryOperation.Insert && !item.Definition.CanBeIngredient) return false;
            return Submit(new InventoryRequest { Kind = operation, From = SelectedSlot, To = cauldron ? cauldron.ObjectId : -1,
                DefinitionId = stack.ItemId, Ids = new[] { stack.WorldIds[0] }, Rotation = item.PresentedRotation,
                Position = operation == InventoryOperation.Insert ? item.PresentedRootPosition : motor.PhysicalFeet });
        }

        public void DropSelected()
        {
            if (carry && carry.IsCarrying) carry.Drop();
            else DropSlot(SelectedSlot, false);
        }
        public void DropSlot(int index) => DropSlot(index, true);

        private void DropSlot(int index, bool wholeStack)
        {
            if (!IsOwner || !CanAct) return;
            Equipment.CancelUse();
            var stack = GetSlot(index);
            if (!stack.IsEmpty) ReleaseSlot(index, registry.GetDefinition(stack.ItemId).DropSpeed, wholeStack, ItemReleaseIntent.Drop);
        }

        public bool TryReleaseEquipped(uint id, float launchSpeed)
        {
            if (!IsOwner || !CanEquip || carry && carry.Role != CarryRole.Free || !Equipment.HeldPresentation.ReadyForUse) return false;
            var equipped = GetEquipped();
            if (equipped.IsEmpty || equipped.WorldIds[0] != id) return false;
            return ReleaseSlot(SelectedSlot, launchSpeed, false, ItemReleaseIntent.Throw);
        }

        private bool ReleaseSlot(int index, float launchSpeed, bool wholeStack, ItemReleaseIntent intent)
        {
            if (!IsOwner || !CanAct || intent == ItemReleaseIntent.Throw && !CanEquip ||
                seating != null && (seating.TransitionPending || seating.PlacementPending)) return false;
            var stack = GetSlot(index);
            if (stack.IsEmpty) return false;
            int count = wholeStack ? stack.Count : 1;
            var ids = new uint[count];
            Array.Copy(stack.WorldIds, ids, count);
            var definition = registry.GetDefinition(stack.ItemId);
            var aimPose = presentation.AimPose;
            var aim = aimPose.rotation;
            Vector3 forward = aim * Vector3.forward;
            if (intent == ItemReleaseIntent.Throw)
            {
                if (!registry.TryGetItem(ids[0], out var item) ||
                    !Equipment.HeldPresentation.TryPrepareRelease(item, out var release, out byte progress)) return false;
                var motion = new ItemMotion { Id = ids[0], Position = release.position, Rotation = release.rotation,
                    PositionIsSphereCenter = false,
                    Velocity = forward * launchSpeed + ItemReleaseVelocity.Movement(seating, motor) * definition.VelocityInheritance,
                    AngularVelocity = aim * definition.InitialSpin };
                return Submit(new InventoryRequest { Kind = InventoryOperation.Release, From = index, DefinitionId = stack.ItemId,
                    Ids = ids, Releases = new[] { motion }, ReleaseIntent = intent, ReleaseArcProgress = progress });
            }
            Vector3 origin = aimPose.position;
            Vector3 position = origin + forward * 0.65f;
            Quaternion rotation = aim * definition.WorldPrefab.transform.localRotation;
            var releases = new ItemMotion[count];
            for (int i = 0; i < count; i++)
            {
                // Spread stack drops to avoid spawning mutually overlapping bodies.
                Vector3 offset = aim * new Vector3((i % 4 - (Mathf.Min(count, 4) - 1) * 0.5f) * 0.24f, (i / 4) * 0.24f, 0f);
                if (!registry.TryGetItem(ids[i], out var item) || !item ||
                    !ItemReleaseClearance.TryDrop(new Pose(position + offset, rotation), origin, item.ReleaseSphere,
                        aim, registry.EnvironmentMask, out var release)) return false;
                releases[i] = new ItemMotion { Id = ids[i], Position = release.position,
                    Rotation = rotation,
                    Velocity = forward * launchSpeed + ItemReleaseVelocity.Movement(seating, motor) * definition.VelocityInheritance,
                    AngularVelocity = aim * definition.InitialSpin };
            }
            return Submit(new InventoryRequest { Kind = InventoryOperation.Release, From = index, DefinitionId = stack.ItemId,
                Ids = ids, Releases = releases, ReleaseIntent = intent });
        }

        private bool Submit(InventoryRequest request)
        {
            if (!IsOwner || !CanAct) return false;
            request.Operation = ++nextOperation;
            request.Epoch = registry.Epoch;
            request.ControlRevision = motor.ControlRevision;
            request.AutoSelect = CanEquip;
            bool throwing = request.Kind == InventoryOperation.Release && request.ReleaseIntent == ItemReleaseIntent.Throw;
            if (throwing)
            {
                var action = networkState.PredictRecovery(request.DefinitionId, request.Ids[0], request.Operation, request.ReleaseArcProgress);
                request.ActionSequence = action.TransitionSequence;
                request.ActionStartedTick = action.StartedTick;
                request.ActionStartedFraction = action.StartedFraction;
            }
            pending.Add(request);
            if (request.Kind == InventoryOperation.Release)
                for (int i = 0; i < request.Ids.Length; i++)
                    registry.PredictRelease(request.Ids[i], request.Operation, request.Releases[i], this, request.ReleaseIntent);
            if (request.Kind is InventoryOperation.Insert or InventoryOperation.DirectUse)
                registry.PredictConsumption(request, this);
            RebuildView();
            bool accepted = true;
            if (IsServerInitialized) accepted = ProcessRequest(request);
            else CmdOperate(request);
            if (throwing && accepted) Equipment.HeldPresentation.ReleaseSubmitted();
            return accepted;
        }

        [ServerRpc]
        private void CmdOperate(InventoryRequest request) => ProcessRequest(request);

        private bool ProcessRequest(InventoryRequest request)
        {
            if (request.Operation <= lastOperation)
            {
                ReplyInventory(true, 0);
                return false;
            }
            bool accepted = Commit(request);
            lastOperation = request.Operation;
            serverRevision++;
            registry.UpdateEquipment(this, CanEquip ? EquippedId(serverSlots, serverSelection) : 0);
            ReplyInventory(accepted, request.Operation);
            return accepted;
        }

        private bool Commit(InventoryRequest request)
        {
            if (!CanAct || request.Epoch != registry.Epoch || request.ControlRevision != motor.ControlRevision ||
                carry && carry.IsCarrying && request.Kind == InventoryOperation.Release && request.ReleaseIntent == ItemReleaseIntent.Throw) return false;
            if (request.Kind == InventoryOperation.Select || request.Kind == InventoryOperation.Release)
            {
                if (request.ControlRevision != motor.ControlRevision) return false;
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
                        (item.State != WorldItemState.World && !registry.OutputReady(item)) || item.DefinitionId != request.DefinitionId ||
                        item.Releaser != request.ExpectedReleaser || item.Operation != request.ExpectedOperation ||
                        item.Motion.Revision < request.ExpectedRevision) return false;
                    int slot = FindSpace(serverSlots, request.DefinitionId);
                    if (slot < 0) return false;
                    AddId(serverSlots, slot, item.DefinitionId, request.Ids[0]);
                    if (CanEquip && request.AutoSelect && (request.ControlRevision == motor.ControlRevision) &&
                        serverSelection < 0 && slot < HotbarSize) serverSelection = (sbyte)slot;
                    registry.SetHeld(request.Ids[0], this, EquippedId(serverSlots, serverSelection) == request.Ids[0]);
                    return true;
                case InventoryOperation.Brew:
                case InventoryOperation.Dispose:
                    return CanCraft && EquippedId(serverSlots, serverSelection) == 0 &&
                        registry.ResolveMixture(request.To, request.CauldronRevision, request.Kind == InventoryOperation.Dispose);
                case InventoryOperation.Insert:
                case InventoryOperation.DirectUse:
                    if (!CanCraft || request.Ids == null || request.Ids.Length != 1 || request.From != serverSelection ||
                        EquippedId(serverSlots, serverSelection) != request.Ids[0] ||
                        !registry.TryGetRecord(request.Ids[0], out var consumed) || consumed.State != WorldItemState.Held ||
                        consumed.Holder != ObjectId || consumed.DefinitionId != request.DefinitionId) return false;
                    bool committed = request.Kind == InventoryOperation.Insert
                        ? registry.AdmitHeld(request.Ids[0], request.To, this, request.Operation, request.Position, request.Rotation)
                        : registry.UsePotion(consumed, this, request.Operation, request.Position);
                    if (!committed) return false;
                    RemoveId(serverSlots, request.Ids[0]);
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
                    if (request.ReleaseIntent == ItemReleaseIntent.Throw)
                    {
                        if (request.Ids.Length != 1 || request.From != serverSelection || stack.WorldIds[0] != request.Ids[0]) return false;
                        networkState.AcceptRecovery(new ItemActionSnapshot { State = ItemActionState.Recovering,
                            DefinitionId = request.DefinitionId, WorldId = request.Ids[0], Operation = request.Operation,
                            ControlRevision = request.ControlRevision, TransitionSequence = request.ActionSequence,
                            StartedTick = request.ActionStartedTick, StartedFraction = request.ActionStartedFraction,
                            ReleaseArcProgress = request.ReleaseArcProgress });
                    }
                    foreach (uint id in request.Ids) RemoveId(serverSlots, id);
                    for (int i = 0; i < request.Ids.Length; i++) registry.Release(request.Ids[i], request.Operation, request.Releases[i], this, request.ReleaseIntent);
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
                if (request.Kind == InventoryOperation.Release)
                    foreach (uint id in request.Ids)
                        BirdRegistry.Instance?.ReleaseResolved(id, request.Operation, accepted || request.Operation != operation);
                if (!accepted && request.Operation == operation && request.Ids != null)
                {
                    foreach (uint id in request.Ids)
                        registry.Rollback(id, request.Operation);
                    if (request.Kind == InventoryOperation.Release && request.ReleaseIntent == ItemReleaseIntent.Throw)
                        Equipment.HeldPresentation.RejectRelease(request.Ids[0], request.Operation);
                }
                registry.ResolveConsumption(request, accepted || request.Operation != operation);
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
                if (!CanAct || request.ControlRevision != motor.ControlRevision) continue;
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
                        if (CanEquip && request.AutoSelect && (request.ControlRevision == motor.ControlRevision) &&
                            viewSelection < 0 && slot < HotbarSize) viewSelection = (sbyte)slot;
                        break;
                    case InventoryOperation.Insert:
                    case InventoryOperation.DirectUse:
                    case InventoryOperation.Release:
                        foreach (uint id in request.Ids) RemoveId(viewSlots, id);
                        break;
                }
            }
            foreach (var stack in (ItemStack[])viewSlots.Clone())
                if (!stack.IsEmpty) foreach (uint id in stack.WorldIds)
                    if (registry.TryGetRecord(id, out var record) && record.State == WorldItemState.Removed) RemoveId(viewSlots, id);
            RefreshHeldPresentation();
            InventoryChanged?.Invoke();
        }

        internal void ApplyControlPermissions()
        {
            Equipment.CancelUse();
            ControlPermissionsChanged?.Invoke();
            for (int i = pending.Count - 1; i >= 0; i--)
            {
                var request = pending[i];
                if (request.ControlRevision == motor.ControlRevision) continue;
                if (request.Kind == InventoryOperation.Release && request.ReleaseIntent == ItemReleaseIntent.Throw)
                    Equipment.HeldPresentation.RejectRelease(request.Ids[0], request.Operation);
                if (request.Ids != null)
                    foreach (uint id in request.Ids)
                    {
                        if (request.Kind == InventoryOperation.Release) BirdRegistry.Instance?.ReleaseResolved(id, request.Operation, false);
                        registry.Rollback(id, request.Operation);
                    }
                registry.ResolveConsumption(request, false);
                pending.RemoveAt(i);
            }
            if (IsServerInitialized) registry.UpdateEquipment(this, CanEquip ? EquippedId(serverSlots, serverSelection) : 0);
            RebuildView();
            registry.RefreshHolders();
        }

        internal void RefreshHeldPresentation()
        {
            if (!IsOwner) return;
            uint equipped = CanEquip ? EquippedId(viewSlots, viewSelection) : 0;
            foreach (var stack in viewSlots)
            {
                if (stack.IsEmpty) continue;
                foreach (uint id in stack.WorldIds)
                    if (registry.TryGetItem(id, out var item) && item.Definition != null &&
                        (item.Record.State != WorldItemState.Removed && (!IsServerInitialized || item.Record.State == WorldItemState.Held))) item.PresentHeld(this, id == equipped);
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
