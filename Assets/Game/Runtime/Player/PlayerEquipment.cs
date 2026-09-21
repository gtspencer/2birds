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
