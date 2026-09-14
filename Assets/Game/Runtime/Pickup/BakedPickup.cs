using UnityEngine;

namespace TwoBirds
{
    public sealed class BakedPickup : MonoBehaviour, IInteractable
    {
        [HideInInspector] public uint BakedId;
        public byte ItemId = 1;
        public ItemDefinition Item;

        public string ActionText => "Pick up";
        public string InputActionPath => "Player/Interact";
        public bool CanInteract => isActiveAndEnabled && PickupRegistry.Instance != null &&
                                   !PickupRegistry.Instance.IsCollected(BakedId);

        public void Interact()
        {
            if (!CanInteract) return;
            gameObject.SetActive(false);
            PickupRegistry.Instance.CmdCollectPickup(BakedId, ItemId);
        }

        private void Awake()
        {
            if (PickupRegistry.Instance != null)
                Initialize(PickupRegistry.Instance);
            else
                PickupRegistry.Available += Initialize;
        }

        private void Initialize(PickupRegistry registry)
        {
            PickupRegistry.Available -= Initialize;
            if (registry.IsCollected(BakedId))
            {
                gameObject.SetActive(false);
                return;
            }
            registry.PickupCollected += OnCollected;
        }

        private void OnCollected(uint id)
        {
            if (id == BakedId)
            {
                gameObject.SetActive(false);
                if (PickupRegistry.Instance != null)
                    PickupRegistry.Instance.PickupCollected -= OnCollected;
            }
        }

        private void OnDestroy()
        {
            PickupRegistry.Available -= Initialize;
            if (PickupRegistry.Instance != null)
                PickupRegistry.Instance.PickupCollected -= OnCollected;
        }
    }
}
