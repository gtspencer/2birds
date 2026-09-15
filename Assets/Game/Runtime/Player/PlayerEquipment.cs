using FishNet.Connection;
using FishNet.Object;
using UnityEngine;

namespace TwoBirds
{
    public sealed class PlayerEquipment : NetworkBehaviour
    {
        [SerializeField] private Transform equipSlot;
        private PlayerPresentation presentation;
        private PlayerInventory inventory;
        private PlayerNetworkState networkState;
        private WorldItemRegistry registry;
        private Transform viewmodelSlot;
        private WorldItem activeItem;
        private uint activeId;
        private sbyte activeSlot = -1;
        private bool subscribed;
        public Transform HeldTransform => IsOwner && viewmodelSlot != null ? viewmodelSlot : equipSlot;
        public bool IsCharging => activeItem != null && activeItem.IsCharging;
        public float Charge01 => activeItem != null ? activeItem.Charge01 : 0f;

        private void Awake()
        {
            presentation = GetComponent<PlayerPresentation>();
            inventory = GetComponent<PlayerInventory>();
            networkState = GetComponent<PlayerNetworkState>();
        }

        public override void OnStartNetwork() => registry = WorldItemRegistry.Instance;

        public override void OnStartClient()
        {
            if (IsOwner) InitializeOwner();
            registry?.RefreshHolders();
        }

        private void InitializeOwner()
        {
            if (!subscribed)
            {
                inventory.InventoryChanged += OnInventoryChanged;
                subscribed = true;
            }
            if (viewmodelSlot == null && presentation.ViewCamera != null)
            {
                viewmodelSlot = new GameObject("ViewmodelSlot").transform;
                viewmodelSlot.SetParent(presentation.ViewCamera.transform, false);
                viewmodelSlot.localPosition = new Vector3(0.35f, -0.3f, 0.6f);
                viewmodelSlot.localScale = Vector3.one * 0.5f;
            }
        }

        public void BeginUse()
        {
            if (!IsOwner || !inventory.CanEquip || activeItem != null) return;
            var equipped = inventory.GetEquipped();
            if (equipped.IsEmpty || !registry.TryGetItem(equipped.WorldIds[0], out var item) ||
                item == null || !item.isActiveAndEnabled) return;
            activeItem = item;
            activeId = equipped.WorldIds[0];
            activeSlot = inventory.SelectedSlot;
            item.BeginUse(this);
            networkState.SetChargingUse(IsCharging);
        }

        public void EndUse()
        {
            if (!inventory.CanEquip) { CancelUse(); return; }
            var item = ClearActiveUse();
            if (item != null) item.EndUse();
        }

        public void CancelUse()
        {
            var item = ClearActiveUse();
            if (item != null) item.CancelUse();
        }

        private WorldItem ClearActiveUse()
        {
            var item = activeItem;
            activeItem = null;
            activeId = 0;
            activeSlot = -1;
            networkState.SetChargingUse(false);
            return item;
        }

        private void OnInventoryChanged()
        {
            if (activeItem == null) return;
            var equipped = inventory.GetEquipped();
            if (inventory.SelectedSlot != activeSlot || equipped.IsEmpty || equipped.WorldIds[0] != activeId)
                CancelUse();
        }

        public void ReleaseItem(uint id, float launchSpeed)
        {
            if (inventory.CanEquip) inventory.ReleaseEquipped(id, launchSpeed);
        }

        public override void OnOwnershipClient(NetworkConnection previousOwner)
        {
            if (IsOwner) InitializeOwner();
            else ReleaseOwner();
            registry?.RefreshHolders();
        }

        public override void OnStopClient() => ReleaseOwner();

        private void ReleaseOwner()
        {
            CancelUse();
            if (subscribed) inventory.InventoryChanged -= OnInventoryChanged;
            subscribed = false;
            if (viewmodelSlot != null) Destroy(viewmodelSlot.gameObject);
            viewmodelSlot = null;
        }
    }
}
