using FishNet.Connection;
using FishNet.Object;
using UnityEngine;

namespace TwoBirds
{
    public sealed class PlayerEquipment : NetworkBehaviour
    {
        [SerializeField] private Transform equipSlot;
        private PlayerInventory inventory;
        private PlayerNetworkState networkState;
        private PlayerCarry carry;
        private PlayerMotor motor;
        private PlayerSeating seating;
        private PlayerPresentation presentation;
        private uint shotSequence, acceptedShot, acceptedLifetime;
        private WorldItemRegistry registry;
        private WorldItem activeItem;
        private uint activeId;
        private sbyte activeSlot = -1;
        private bool subscribed;
        public PlayerHeldItemPresentation HeldPresentation { get; private set; }
        public Transform HeldTransform => HeldPresentation.Attachment ? HeldPresentation.Attachment : equipSlot;
        public bool IsCharging => carry && carry.IsCarrying ? carry.IsCharging : activeItem != null && activeItem.IsCharging;
        public float Charge01 => carry && carry.IsCarrying ? carry.Charge01 : activeItem != null ? activeItem.Charge01 : 0f;

        private void Awake()
        {
            inventory = GetComponent<PlayerInventory>();
            networkState = GetComponent<PlayerNetworkState>();
            carry = GetComponent<PlayerCarry>();
            motor = GetComponent<PlayerMotor>();
            seating = GetComponent<PlayerSeating>();
            presentation = GetComponent<PlayerPresentation>();
            HeldPresentation = GetComponent<PlayerHeldItemPresentation>();
        }

        public override void OnStartNetwork() => registry = WorldItemRegistry.Instance;

        public override void OnStartClient()
        {
            if (IsOwner) InitializeOwner();
            HeldPresentation.StartPresentation();
            registry?.RefreshHolders();
        }

        private void InitializeOwner()
        {
            if (!subscribed)
            {
                inventory.InventoryChanged += OnInventoryChanged;
                subscribed = true;
            }

        }

        public void BeginUse()
        {
            if (carry && carry.IsCarrying) { carry.BeginUse(); return; }
            if (!IsOwner || !HeldPresentation.ReadyForUse || activeItem) return;
            var equipped = inventory.GetEquipped();
            if (equipped.IsEmpty || !registry.TryGetItem(equipped.WorldIds[0], out var item) ||
                item == null || !item.isActiveAndEnabled) return;
            activeItem = item;
            activeId = equipped.WorldIds[0];
            activeSlot = inventory.SelectedSlot;
            item.BeginUse(this);
            if (item.IsCharging) networkState.BeginItemCharge(item.Definition.ItemId, activeId);
        }

        public void DirectUse()
        {
            if (!IsOwner || !inventory.CanCraft || !HeldPresentation.ReadyForUse) return;
            CancelUse();
            inventory.DirectUse();
        }

        public void EndUse()
        {
            if (carry && carry.IsCarrying) { carry.EndUse(); return; }
            if (!inventory.CanEquip) { CancelUse(); return; }
            var item = ClearActiveUse();
            if (!item) return;
            item.EndUse();
            networkState.CancelItemCharge();
        }

        public void CancelUse()
        {
            if (carry) carry.CancelUse();
            var item = ClearActiveUse();
            if (!item) return;
            item.CancelUse();
            networkState.CancelItemCharge();
        }

        private WorldItem ClearActiveUse()
        {
            var item = activeItem;
            activeItem = null;
            activeId = 0;
            activeSlot = -1;
            return item;
        }

        private void OnInventoryChanged()
        {
            if (activeItem == null) return;
            var equipped = inventory.GetEquipped();
            if (inventory.SelectedSlot != activeSlot || equipped.IsEmpty || equipped.WorldIds[0] != activeId)
                CancelUse();
        }

        public bool TryReleaseItem(uint id, float launchSpeed) =>
            HeldPresentation.ReadyForUse && inventory.TryReleaseEquipped(id, launchSpeed);

        internal float ItemCharge01(ItemDefinition definition) => networkState.ItemAction.State == ItemActionState.Charging
            ? Mathf.Clamp01((float)(networkState.ActionAge(networkState.ItemAction) / definition.ThrowChargeTime)) : 0f;

        internal bool TryFireSlingshot(WorldItem item)
        {
            if (!IsOwner || !HeldPresentation.ReadyForUse || item.Definition is not SlingshotDefinition definition ||
                !definition.PebblePrefab) return false;
            var stack = inventory.GetEquipped();
            if (stack.IsEmpty || stack.WorldIds[0] != item.Record.Motion.Id ||
                !HeldPresentation.TryPreparePebble(item, out Vector3 center, out float charge)) return false;
            Vector3 direction = SlingshotAim.Direction(presentation.AimPose, center, inventory);
            uint shot = ++shotSequence;
            var action = networkState.PredictRecovery(definition.ItemId, item.Record.Motion.Id, shot,
                (byte)Mathf.RoundToInt(charge * 255f));
            var fire = new PebbleFire { Epoch = registry.Epoch, Lifetime = networkState.Lifetime, Shot = shot,
                Weapon = item.Record.Motion.Id, Action = action,
                Motion = new ItemMotion { Tick = action.StartedTick, Position = center, Rotation = Quaternion.identity,
                    PositionIsSphereCenter = true, Velocity = direction * Mathf.Lerp(definition.MinThrowSpeed, definition.MaxThrowSpeed, charge) +
                        ItemReleaseVelocity.Movement(seating, motor) * definition.VelocityInheritance } };
            registry.Pebbles.Predict(fire, ObjectId);
            HeldPresentation.ReleaseSubmitted();
            if (IsServerInitialized) AcceptShot(fire);
            else CmdFireSlingshot(fire);
            return true;
        }

        [ServerRpc(RequireOwnership = true)]
        private void CmdFireSlingshot(PebbleFire fire) => AcceptShot(fire);

        private void AcceptShot(PebbleFire fire)
        {
            if (acceptedLifetime != networkState.Lifetime)
            { acceptedLifetime = networkState.Lifetime; acceptedShot = 0; }
            if (fire.Shot <= acceptedShot) return;
            acceptedShot = fire.Shot;
            var equipped = inventory.GetEquipped();
            bool accepted = fire.Epoch == registry.Epoch && fire.Lifetime == networkState.Lifetime &&
                fire.Action.ControlRevision == motor.ControlRevision && inventory.CanEquip && networkState.CanCharge &&
                !equipped.IsEmpty && equipped.WorldIds[0] == fire.Weapon && equipped.ItemId == fire.Action.DefinitionId &&
                fire.Action.MatchesRelease(fire.Weapon, fire.Shot) &&
                registry.GetDefinition(equipped.ItemId) is SlingshotDefinition &&
                WorldItemRegistry.Finite(fire.Motion.Position) && WorldItemRegistry.Finite(fire.Motion.Velocity);
            if (!accepted) { registry.Pebbles.Reject(fire, ObjectId, Owner); return; }
            networkState.AcceptRecovery(fire.Action);
            registry.Pebbles.Accept(fire, ObjectId, Owner.ClientId);
        }

        public override void OnOwnershipClient(NetworkConnection previousOwner)
        {
            HeldPresentation.StopPresentation();
            if (IsOwner) InitializeOwner();
            else ReleaseOwner();
            HeldPresentation.StartPresentation();
            registry?.RefreshHolders();
        }

        public override void OnStopClient()
        {
            HeldPresentation.StopPresentation();
            ReleaseOwner();
        }

        public override void OnStopNetwork() => HeldPresentation.StopPresentation();

        private void ReleaseOwner()
        {
            CancelUse();
            if (subscribed) inventory.InventoryChanged -= OnInventoryChanged;
            subscribed = false;
        }
    }
}
