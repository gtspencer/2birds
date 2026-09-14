using FishNet.Object;
using UnityEngine;
using UnityEngine.InputSystem;

namespace TwoBirds
{
    public sealed class PlayerInteraction : NetworkBehaviour
    {
        [SerializeField] private float pickupRange = 3f;

        private PlayerInputReader inputReader;
        private InputAction interact;

        private Camera mainCamera;

        private void Awake()
        {
            inputReader = GetComponent<PlayerInputReader>();
            mainCamera = Camera.main;
        }

        public override void OnStartClient()
        {
            if (!IsOwner) return;
            interact = InputSystem.actions.FindAction("Player/Interact");
        }

        private void Update()
        {
            if (!IsOwner || interact == null) return;
            if (inputReader != null && inputReader.InventoryOpen) return;
            if (!interact.WasPerformedThisFrame()) return;
            TryPickup();
        }

        private void TryPickup()
        {
            if (!Physics.Raycast(mainCamera.transform.position, mainCamera.transform.forward, out var hit, pickupRange))
                return;

            var pickup = hit.collider.GetComponent<BakedPickup>();
            if (pickup == null || !pickup.gameObject.activeSelf) return;
            if (PickupRegistry.Instance == null) return;

            pickup.gameObject.SetActive(false);
            PickupRegistry.Instance.CmdCollectPickup(pickup.BakedId, pickup.ItemId);
        }
    }
}
