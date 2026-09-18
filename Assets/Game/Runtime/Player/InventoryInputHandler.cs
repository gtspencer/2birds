using System;
using UnityEngine.InputSystem;

namespace TwoBirds
{
    internal sealed class InventoryInputHandler : IDisposable
    {
        private readonly SessionController session;
        private readonly Action toggle;
        private readonly InputAction inventoryAction, previous, next;
        private readonly InputAction[] slots = new InputAction[PlayerInventory.HotbarSize];
        private PlayerInventory inventory;
        private PlayerInputReader input;
        private int suppressedFrame = -1;

        public InventoryInputHandler(SessionController session, Action toggle)
        {
            this.session = session;
            this.toggle = toggle;
            var map = InputSystem.actions.FindActionMap("Player");
            inventoryAction = map.FindAction("Inventory");
            previous = map.FindAction("Previous");
            next = map.FindAction("Next");
            inventoryAction.performed += Toggle;
            previous.performed += Cycle;
            next.performed += Cycle;
            for (int i = 0; i < slots.Length; i++)
            {
                slots[i] = map.FindAction($"Hotbar{i + 1}");
                slots[i].performed += Select;
            }
        }

        public void Bind(PlayerInventory inventory, PlayerInputReader input)
        { this.inventory = inventory; this.input = input; }

        public void SuppressInput() => suppressedFrame = UnityEngine.Time.frameCount;

        private bool Available => inventory && inventory.IsOwner && input && session &&
            session.Phase == SessionPhase.InGame && !session.PanelOpen && !session.ConsoleOpen &&
            suppressedFrame != UnityEngine.Time.frameCount;
        private bool CanSelect => Available && !input.InventoryOpen && inventory.CanEquip;

        private void Toggle(InputAction.CallbackContext context)
        {
            if (Available) toggle();
        }

        private void Select(InputAction.CallbackContext context)
        {
            if (!CanSelect) return;
            for (int i = 0; i < slots.Length; i++)
                if (context.action == slots[i]) { inventory.SelectSlot((sbyte)i); return; }
        }

        private void Cycle(InputAction.CallbackContext context)
        {
            if (!CanSelect) return;
            int direction = context.action == previous ? -1 : 1;
            sbyte current = inventory.SelectedSlot;
            sbyte selected = current < 0
                ? (sbyte)(direction > 0 ? 0 : PlayerInventory.HotbarSize - 1)
                : (sbyte)((current + direction + PlayerInventory.HotbarSize) % PlayerInventory.HotbarSize);
            inventory.SelectSlot(selected);
        }

        public void Dispose()
        {
            inventoryAction.performed -= Toggle;
            previous.performed -= Cycle;
            next.performed -= Cycle;
            foreach (var slot in slots) slot.performed -= Select;
            Bind(null, null);
        }
    }
}
