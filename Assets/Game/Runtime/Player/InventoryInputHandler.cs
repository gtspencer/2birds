using System;
using UnityEngine;
using UnityEngine.InputSystem;

namespace TwoBirds
{
    internal sealed class InventoryInputHandler : IDisposable
    {
        private readonly SessionController session;
        private readonly Action toggle;
        private readonly InputAction inventoryAction, previous, next, scroll;
        private readonly InputAction[] slots = new InputAction[PlayerInventory.HotbarSize];
        private readonly InputAction[] uiNavigation;
        private PlayerInventory inventory;
        private PlayerInputReader input;
        private int suppressedFrame = -1;

        public InventoryInputHandler(SessionController session, Action toggle)
        {
            this.session = session;
            this.toggle = toggle;
            uiNavigation = new[] { InputSystem.actions.FindAction("UI/Submit"), InputSystem.actions.FindAction("UI/Cancel"),
                InputSystem.actions.FindAction("UI/Pause"), InputSystem.actions.FindAction("UI/Navigate") };
            var map = InputSystem.actions.FindActionMap("Player");
            inventoryAction = map.FindAction("Inventory");
            previous = map.FindAction("Previous");
            next = map.FindAction("Next");
            scroll = InputSystem.actions.FindAction("UI/ScrollWheel");
            inventoryAction.performed += Toggle;
            previous.performed += Cycle;
            next.performed += Cycle;
            scroll.performed += Scroll;
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
            session.Phase == SessionPhase.InGame && !session.PanelOpen && !session.ConsoleOpen && !session.EditorOpen &&
            !input.InputSuppressed && !ControlsRemapPanel.SuppressMenuInput &&
            suppressedFrame != UnityEngine.Time.frameCount;
        private bool CanSelect => Available && inventory.CanAct && !input.InventoryOpen && inventory.CanEquip;

        private void Toggle(InputAction.CallbackContext context)
        {
            if (input && input.InventoryOpen)
                foreach (var action in uiNavigation)
                    foreach (var control in action.controls)
                        if (context.control == control || context.control.parent == control) return;
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
            Cycle(context.action == previous ? -1 : 1);
        }

        private void Scroll(InputAction.CallbackContext context)
        {
            Vector2 value = context.ReadValue<Vector2>();
            if (value.y != 0) Cycle(value.y > 0 ? -1 : 1);
        }

        private void Cycle(int direction)
        {
            if (!CanSelect) return;
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
            scroll.performed -= Scroll;
            foreach (var slot in slots) slot.performed -= Select;
            Bind(null, null);
        }
    }
}
