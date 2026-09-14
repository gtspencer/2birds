using FishNet.Object;
using UnityEngine;

namespace TwoBirds
{
    public sealed class PlayerEquipment : NetworkBehaviour
    {
        [SerializeField] private Transform equipSlot;
        [SerializeField] private ItemRegistry itemRegistry;

        private PlayerInventory inventory;
        private GameObject currentModel;
        private GameObject viewmodel;
        private Transform viewmodelSlot;
        private byte displayedItemId;
        private int pickupLayer;

        private void Awake()
        {
            inventory = GetComponent<PlayerInventory>();
            pickupLayer = LayerMask.NameToLayer("Pickup/Generic");
        }

        public override void OnStartClient()
        {
            inventory.InventoryChanged += Refresh;
            Refresh();
        }

        private void LateUpdate()
        {
            if (!IsOwner || viewmodelSlot != null) return;
            var cam = Camera.main;
            if (cam == null) return;
            viewmodelSlot = new GameObject("ViewmodelSlot").transform;
            viewmodelSlot.SetParent(cam.transform, false);
            viewmodelSlot.localPosition = new Vector3(0.35f, -0.3f, 0.6f);
            viewmodelSlot.localScale = Vector3.one * 0.5f;
            Refresh();
        }

        private void Refresh()
        {
            var equipped = inventory.GetEquipped();
            byte targetId = equipped.IsEmpty ? (byte)0 : equipped.ItemId;
            if (targetId == displayedItemId) return;
            displayedItemId = targetId;

            if (currentModel != null) { Destroy(currentModel); currentModel = null; }
            if (viewmodel != null) { Destroy(viewmodel); viewmodel = null; }

            if (targetId == 0 || itemRegistry == null) return;
            var def = itemRegistry.Get(targetId);
            if (def == null || def.WorldPrefab == null) return;

            if (equipSlot != null)
            {
                currentModel = Instantiate(def.WorldPrefab, equipSlot);
                SetHeldLayer(currentModel, def);
                currentModel.transform.localPosition = Vector3.zero;
                currentModel.transform.localRotation = Quaternion.identity;
                if (IsOwner)
                    foreach (var r in currentModel.GetComponentsInChildren<Renderer>())
                        r.enabled = false;
            }

            if (IsOwner && viewmodelSlot != null)
            {
                viewmodel = Instantiate(def.WorldPrefab, viewmodelSlot);
                SetHeldLayer(viewmodel, def);
                viewmodel.transform.localPosition = Vector3.zero;
                viewmodel.transform.localRotation = Quaternion.identity;
            }
        }

        private void SetHeldLayer(GameObject model, ItemDefinition def)
        {
            if (def.UsePrefabLayerWhenHeld) return;
            foreach (var child in model.GetComponentsInChildren<Transform>(true))
                child.gameObject.layer = pickupLayer;
        }

        public override void OnStopClient()
        {
            if (inventory != null) inventory.InventoryChanged -= Refresh;
            if (currentModel != null) Destroy(currentModel);
            if (viewmodel != null) Destroy(viewmodel);
            if (viewmodelSlot != null) Destroy(viewmodelSlot.gameObject);
        }
    }
}
