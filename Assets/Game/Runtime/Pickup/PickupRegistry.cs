using FishNet.Connection;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using UnityEngine;

namespace TwoBirds
{
    public sealed class PickupRegistry : NetworkBehaviour
    {
        public static PickupRegistry Instance { get; private set; }
        public static event System.Action<PickupRegistry> Available;

        [SerializeField] private ItemRegistry itemRegistry;

        private readonly SyncHashSet<uint> collected = new();

        public event System.Action<uint> PickupCollected;

        public bool IsCollected(uint id) => collected.Contains(id);

        public override void OnStartNetwork()
        {
            Instance = this;
            collected.OnChange += OnCollectedChanged;
            Available?.Invoke(this);
        }

        private void OnCollectedChanged(SyncHashSetOperation op, uint item, bool asServer)
        {
            if (op == SyncHashSetOperation.Add)
                PickupCollected?.Invoke(item);
        }

        [ServerRpc(RequireOwnership = false)]
        public void CmdCollectPickup(uint pickupId, byte itemId, NetworkConnection sender = null)
        {
            if (collected.Contains(pickupId) || sender == null) return;
            collected.Add(pickupId);

            foreach (var nob in sender.Objects)
            {
                var inv = nob.GetComponent<PlayerInventory>();
                if (inv == null) continue;
                var def = itemRegistry != null ? itemRegistry.Get(itemId) : null;
                inv.TryAddItem(itemId, 1, def);
                break;
            }
        }

        public override void OnStopNetwork()
        {
            collected.OnChange -= OnCollectedChanged;
            if (Instance == this) Instance = null;
        }
    }
}
