using UnityEngine;

namespace TwoBirds
{
    [DefaultExecutionOrder(175), DisallowMultipleComponent]
    public sealed class PlayerHeldItemPresentation : MonoBehaviour
    {
        private PlayerEquipment equipment;
        private PlayerInventory inventory;
        private PlayerNetworkState networkState;
        private PlayerAvatarPresentation playerAvatar;
        private PlayerPresentation player;
        private PlayerSeating seating;
        private PlayerCarry carry;
        private WorldItemRegistry registry;
        private PlayerEmote emote;
        private ItemDefinition selected, actionDefinition;
        private WorldItem selectedItem;
        private SlingshotPresentation slingshot;
        private uint selectedId;
        internal HeldItemPresentationState State { get; private set; }
        internal bool CanShowHeldItem => State != null && State.CanShowHeldItem;
        internal bool ReadyForUse => State != null && State.ReadyForUse;
        internal Transform Attachment => State?.Attachment;
        internal float HeavyFrameWeight => State?.HeavyFrameWeight ?? 0f;
        internal float ArmWeight => State?.ArmWeight ?? 0f;
        internal bool IsPendingRelease(uint id) => State != null && State.IsPendingRelease(id);
        internal bool MatchesPendingRelease(in ItemRecord record) => State != null && State.MatchesPendingRelease(record);

        private void Awake()
        {
            equipment = GetComponent<PlayerEquipment>(); inventory = GetComponent<PlayerInventory>();
            networkState = GetComponent<PlayerNetworkState>(); playerAvatar = GetComponent<PlayerAvatarPresentation>();
            player = GetComponent<PlayerPresentation>(); seating = GetComponent<PlayerSeating>(); carry = GetComponent<PlayerCarry>();
        }
        private void OnEnable() { if (equipment && equipment.IsClientInitialized) StartPresentation(); }
        internal void StartPresentation()
        {
            if (State != null || !isActiveAndEnabled) return;
            registry = WorldItemRegistry.Instance;
            if (!registry) return;
            State = new HeldItemPresentationState(playerAvatar.Presentation, transform);
            State.CommitItem = pose => { if (selectedItem) selectedItem.CommitHeldPose(pose, inventory.ObjectId); };
            inventory.InventoryChanged += SelectionChanged;
            inventory.ControlPermissionsChanged += ContextChanged;
            networkState.ActionChanged += ActionChanged;
            seating.PresentationContextChanged += ContextChanged;
            carry.PresentationContextChanged += ContextChanged;
            registry.PresentationChanged += LifecycleChanged;
            playerAvatar.Presentation.WillUnbind += Unbind;
            playerAvatar.Presentation.DidBind += Bound;
            playerAvatar.Presentation.IdentityResolved += IdentityResolved;
            emote = playerAvatar.Emote;
            emote.Changed += ContextChanged;
            SelectionChanged(); ActionChanged();
        }
        internal void StopPresentation()
        {
            if (State == null) return;
            inventory.InventoryChanged -= SelectionChanged;
            inventory.ControlPermissionsChanged -= ContextChanged;
            networkState.ActionChanged -= ActionChanged;
            seating.PresentationContextChanged -= ContextChanged;
            carry.PresentationContextChanged -= ContextChanged;
            if (registry)
            {
                registry.PresentationChanged -= LifecycleChanged;
                registry.DetachHolder(inventory);
            }
            playerAvatar.Presentation.WillUnbind -= Unbind;
            playerAvatar.Presentation.DidBind -= Bound;
            playerAvatar.Presentation.IdentityResolved -= IdentityResolved;
            emote.Changed -= ContextChanged;
            selected = actionDefinition = null; selectedItem = null; slingshot = null; selectedId = 0;
            State.Dispose(); State = null;
            networkState.ResetItemAction();
        }
        private void OnDisable() => StopPresentation();
        private void OnDestroy() => StopPresentation();
        private void Unbind(AvatarBinding binding) { State?.InvalidateBinding(); registry.RefreshHolder(inventory); }
        private void Bound(AvatarBinding binding) { ContentChanged(); registry.RefreshHolder(inventory); }
        private void IdentityResolved(AvatarRegistry.Entry entry) => ContentChanged();
        private void ContentChanged() => State?.RefreshContent();
        private void ContextChanged()
        {
            if (State == null) return;
            if (!inventory.CanEquip || !networkState.CanCharge) { equipment.CancelUse(); networkState.ResetItemAction(); }
            SelectionChanged(); Sync(); registry.RefreshHolder(inventory);
        }
        private void SelectionChanged()
        {
            if (State == null) return;
            uint id = 0;
            ItemDefinition definition = null;
            if (inventory.IsOwner)
            {
                var stack = inventory.GetEquipped();
                if (!stack.IsEmpty) { id = stack.WorldIds[0]; definition = registry.GetDefinition(stack.ItemId); }
            }
            else
            {
                var item = registry.EquippedPresentation(inventory.ObjectId);
                if (item) { id = item.Record.Motion.Id; definition = item.Definition; }
            }
            if (id != selectedId || definition != selected)
            {
                selected = definition; selectedId = id;
                if (id != 0 && playerAvatar.Hands) playerAvatar.Hands.ItemSelected();
                registry.TryGetItem(id, out selectedItem);
                slingshot = selectedItem ? selectedItem.GetComponent<SlingshotPresentation>() : null;
                Sync(); registry.RefreshHolder(inventory);
            }
        }
        private void ActionChanged()
        {
            if (State == null) return;
            actionDefinition = registry.GetDefinition(networkState.ItemAction.DefinitionId);
            Sync(); registry.RefreshHolder(inventory);
        }
        private void LifecycleChanged(uint id, int previousHolder, int holder)
        { if (previousHolder == inventory.ObjectId || holder == inventory.ObjectId || id == selectedId) SelectionChanged(); }

        internal HeldItemPresentationInput CaptureInput(bool firstPerson)
        {
            var action = networkState.ItemAction;
            var value = new HeldItemPresentationInput
            {
                SelectedDefinition = selected, SelectedId = selectedId, ActionDefinition = actionDefinition,
                Action = action, ActionAge = networkState.HasActionSnapshot ? networkState.ActionAge(action) : 0d,
                FirstPerson = firstPerson, HasAction = networkState.HasActionSnapshot, Emoting = emote && emote.Presenting,
                CanEquip = inventory.CanEquip, CanCharge = networkState.CanCharge,
                Placement = playerAvatar.CurrentPlacement, Aim = player.AimPose,
                EnvironmentMask = registry.EnvironmentMask
            };
            if (action.State != ItemActionState.Recovering || actionDefinition is SlingshotDefinition) return value;
            bool Matches(ItemRecord record) => record.Motion.Id == action.WorldId && record.Releaser == inventory.ObjectId && record.Operation == action.Operation;
            if (registry.TryGetItem(action.WorldId, out var item) && item)
            {
                value.ProjectileAvailable = item.ReleaseAvailable && Matches(item.Record);
                value.Projectile = new Pose(item.PresentedRootPosition, item.PresentedRotation);
                value.ProjectileUnavailable = !value.ProjectileAvailable && Matches(item.Record);
            }
            if (registry.TryGetRecord(action.WorldId, out var record))
                value.ProjectileUnavailable |= record.State == WorldItemState.Removed ||
                    record.State == WorldItemState.Held && record.Holder != inventory.ObjectId && (int)(record.Motion.Tick - action.StartedTick) >= 0 ||
                    record.State == WorldItemState.World && record.Releaser >= 0 && !Matches(record) && (int)(record.LaunchTick - action.StartedTick) > 0;
            return value;
        }
        private void Sync() => State?.SetInput(CaptureInput(inventory.IsOwner), slingshot);
        private void LateUpdate()
        {
            if (State == null) return;
            Sync(); State.Advance(); CompleteAtDeadline();
            if (!inventory.IsOwner && !playerAvatar.Presentation.EvaluatesTargets) { PrepareHands(null, null); CommitHands(null); }
        }
        private void CompleteAtDeadline()
        {
            if (!inventory.IsOwner || !State.RecoveryFinished) return;
            var action = networkState.ItemAction;
            networkState.CompleteRecovery(action.WorldId, action.Operation);
        }
        internal void PrepareHands(AvatarBinding binding, HeldItemBodyFrame? body) { Sync(); State?.PrepareHands(binding, body); }
        internal Vector3 CommitHands(AvatarBinding binding) => State?.CommitHands(binding) ?? Vector3.zero;
        internal void ReleaseSubmitted() => State?.ReleaseSubmitted();
        internal void RejectRelease(uint id, uint operation)
        {
            if (slingshot && selectedId == id && networkState.ItemAction.Operation == operation) slingshot.ResetPose();
            networkState.CompleteRecovery(id, operation);
        }
        internal bool TryPrepareRelease(WorldItem item, out Pose release, out byte progress)
        {
            release = default; progress = 0;
            if (!ReadyForUse || !item) return false;
            Sync(); State.Advance(); CompleteAtDeadline(); playerAvatar.Hands.SampleImmediately();
            var sample = State.committed;
            if (sample.Item != item.Record.Motion.Id || !sample.Clear || sample.Generation != (playerAvatar.Hands.LocalBinding?.Generation ?? 0))
            { networkState.CancelItemCharge(); return false; }
            release = sample.ItemPose; progress = sample.Progress; State.StageRelease(); return true;
        }
        internal bool TryPreparePebble(WorldItem item, out Vector3 center, out float charge)
        {
            center = default; charge = 0f;
            if (!slingshot || item.Definition is not SlingshotDefinition definition || !TryPrepareRelease(item, out var release, out _)) return false;
            charge = equipment.ItemCharge01(definition);
            var desired = new Pose(slingshot.Center, release.rotation);
            float radius = definition.PebblePrefab.Radius;
            if (!ItemReleaseClearance.TryResolve(desired, player.AimPose.position, radius, State.LastBody.Rotation,
                registry.EnvironmentMask, out var allowed)) return false;
            Vector3 correction = allowed.position - desired.position;
            if (correction.sqrMagnitude > 0.000001f) { playerAvatar.Hands.TranslateRig(correction); State.Shift(correction); }
            center = slingshot.Center;
            return State.committed.Clear && ItemReleaseClearance.TryResolve(new Pose(center, release.rotation), player.AimPose.position, radius,
                State.LastBody.Rotation, registry.EnvironmentMask, out var check) && (check.position - center).sqrMagnitude < 0.000001f;
        }
        internal bool TryPebbleDeparture(uint weapon, uint shot, uint launched, out Vector3 center)
        { center = default; return State != null && State.TryPebbleDeparture(weapon, shot, launched, out center); }
    }
}
