using FishNet.Object;
using FishNet.Object.Synchronizing;
using UnityEngine;

namespace TwoBirds
{
    public sealed class PlayerInventory : NetworkBehaviour
    {
        public const int SlotCount = 24;
        public const int HotbarSize = 8;

        private readonly SyncList<ItemStack> slots = new();
        private readonly SyncVar<sbyte> selectedSlot = new();

        public int Count => slots.Count;
        public ItemStack GetSlot(int index) => index >= 0 && index < slots.Count ? slots[index] : default;
        public sbyte SelectedSlot => selectedSlot.Value;
        public event System.Action InventoryChanged;

        public override void OnStartServer()
        {
            for (int i = 0; i < SlotCount; i++)
                slots.Add(default);
            selectedSlot.Value = -1;
        }

        public override void OnStartNetwork()
        {
            slots.OnChange += OnSlotsChanged;
            selectedSlot.OnChange += OnSelectionChanged;
        }

        private void OnSlotsChanged(SyncListOperation op, int index, ItemStack oldItem, ItemStack newItem, bool asServer)
        {
            if (!asServer) InventoryChanged?.Invoke();
        }

        private void OnSelectionChanged(sbyte prev, sbyte next, bool asServer)
        {
            if (!asServer) InventoryChanged?.Invoke();
        }

        public bool TryAddItem(byte itemId, ushort count, ItemDefinition def)
        {
            if (!IsServerInitialized || itemId == 0) return false;

            if (def != null && def.Stackable)
            {
                for (int i = 0; i < slots.Count && count > 0; i++)
                {
                    if (slots[i].ItemId == itemId && slots[i].Count < def.MaxStack)
                    {
                        ushort space = (ushort)(def.MaxStack - slots[i].Count);
                        ushort add = count > space ? space : count;
                        slots[i] = new ItemStack(itemId, (ushort)(slots[i].Count + add));
                        count -= add;
                    }
                }
            }

            for (int i = 0; i < slots.Count && count > 0; i++)
            {
                if (!slots[i].IsEmpty) continue;
                ushort add = (def != null && def.Stackable) ? (ushort)Mathf.Min(count, def.MaxStack) : (ushort)1;
                slots[i] = new ItemStack(itemId, add);
                count -= add;
            }

            return count == 0;
        }

        [ServerRpc]
        public void CmdSelectSlot(sbyte slot)
        {
            if (slot < -1 || slot >= HotbarSize) return;
            selectedSlot.Value = selectedSlot.Value == slot ? (sbyte)-1 : slot;
        }

        [ServerRpc]
        public void CmdSwapSlots(int from, int to)
        {
            if (from < 0 || from >= SlotCount || to < 0 || to >= SlotCount || from == to) return;
            var temp = slots[from];
            slots[from] = slots[to];
            slots[to] = temp;
        }

        [ServerRpc]
        public void CmdDropSelected()
        {
            sbyte sel = selectedSlot.Value;
            if (sel < 0 || sel >= HotbarSize) return;
            if (slots[sel].IsEmpty) return;
            if (slots[sel].Count <= 1)
                slots[sel] = default;
            else
                slots[sel] = new ItemStack(slots[sel].ItemId, (ushort)(slots[sel].Count - 1));
        }

        [ServerRpc]
        public void CmdDropSlot(int index)
        {
            if (index < 0 || index >= SlotCount) return;
            slots[index] = default;
        }

        public ItemStack GetEquipped()
        {
            sbyte sel = selectedSlot.Value;
            if (sel < 0 || sel >= HotbarSize) return default;
            return GetSlot(sel);
        }
    }
}
