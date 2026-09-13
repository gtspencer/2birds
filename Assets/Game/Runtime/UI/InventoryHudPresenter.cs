using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

namespace TwoBirds
{
    public sealed class InventoryHudPresenter : MonoBehaviour
    {
        private SessionController session;
        private UIDocument document;
        private bool bound;
        private PlayerInventory inventory;
        private PlayerHealth health;
        private VisualElement root, hud, panel, hotbar;
        private Label healthText, feedback, tooltip;
        private VisualElement healthFill;
        private Button close;
        private InventorySlotView[] slots, hotSlots;
        private InventoryDragController drag;
        private InputAction toggle;
        private readonly InputAction[] selections = new InputAction[8];
        private bool wasOpen;
        public int SelectedIndex { get; private set; }
        public InventoryEntry SelectedSlot => inventory != null ? inventory.Snapshot.Get(SelectedIndex) : default;

        private void OnEnable()
        {
            if (bound) return;
            session = SessionController.Instance;
            if (session == null) return;
            document = GetComponent<UIDocument>();
            root = document.rootVisualElement;
            if (!document.isActiveAndEnabled || root?.panel == null) return;
            bound = true;
            root.RegisterCallback<DetachFromPanelEvent>(DocumentDetached);
            root.pickingMode = PickingMode.Ignore;
            root.style.position = Position.Absolute;
            root.style.left = root.style.right = root.style.top = root.style.bottom = 0;
            foreach (var template in root.Query<TemplateContainer>().ToList())
            {
                template.pickingMode = PickingMode.Ignore;
                template.style.flexGrow = 1;
            }
            hud = root.Q("inventory-hud"); panel = root.Q("inventory-panel"); hotbar = root.Q("hotbar");
            healthText = root.Q<Label>("health-text"); healthFill = root.Q("health-fill");
            feedback = root.Q<Label>("inventory-feedback"); tooltip = root.Q<Label>("inventory-tooltip");
            close = root.Q<Button>("inventory-close");
            slots = new InventorySlotView[24]; hotSlots = new InventorySlotView[8];
            for (int i = 0; i < 24; i++)
            {
                slots[i] = CreateSlot(i);
                root.Q("inventory-row-" + i / 8).Add(slots[i]);
                if (i < 8) { hotSlots[i] = CreateSlot(i); hotSlots[i].focusable = false; hotbar.Add(hotSlots[i]); }
            }
            drag = new InventoryDragController(root, panel, slots, () => inventory, Select);
            session.CancelInventoryDrag = drag.Cancel;
            close.clicked += Close;
            session.Changed += Bind;
            toggle = InputSystem.actions.FindAction("UI/Inventory");
            toggle.performed += Toggle;
            toggle.Enable();
            for (int i = 0; i < 8; i++)
            {
                selections[i] = InputSystem.actions.FindAction("UI/Slot" + (i + 1));
                selections[i].performed += SelectKey;
                selections[i].Enable();
            }
            Bind();
        }
        private void Update()
        {
            if (!bound && document != null && document.isActiveAndEnabled && document.rootVisualElement?.panel != null) OnEnable();
        }
        private void DocumentDetached(DetachFromPanelEvent evt) { if (evt.target == root) OnDisable(); }
        private InventorySlotView CreateSlot(int index)
        {
            var slot = new InventorySlotView(index);
            slot.RegisterCallback<PointerEnterEvent>(_ => tooltip.text = slot.ItemName);
            slot.RegisterCallback<PointerLeaveEvent>(_ => tooltip.text = "");
            slot.RegisterCallback<FocusInEvent>(_ => tooltip.text = slot.ItemName);
            slot.RegisterCallback<FocusOutEvent>(_ => tooltip.text = "");
            return slot;
        }
        private bool TextFocused()
        {
            var focused = root.focusController?.focusedElement as VisualElement;
            return focused is TextField || focused?.GetFirstAncestorOfType<TextField>() != null;
        }
        private void Toggle(InputAction.CallbackContext context)
        {
            if (!TextFocused()) session.SetInventory(!session.InventoryOpen);
        }
        private void SelectKey(InputAction.CallbackContext context)
        {
            for (int i = 0; i < selections.Length; i++) if (selections[i] == context.action) Select(i);
        }
        private void Select(int index)
        {
            if (index >= 8 || TextFocused() || inventory == null || session.PanelOpen ||
                !session.ApplicationFocused || (!session.GameplayAllowed && !session.InventoryOpen)) return;
            SelectedIndex = index;
            Render();
        }
        private void Close() => session.SetInventory(false);
        private void Bind()
        {
            var next = session.LocalPlayer != null && session.LocalPlayer.IsOwner ? session.LocalPlayer.GetComponent<PlayerInventory>() : null;
            if (next != inventory)
            {
                Unbind();
                inventory = next;
                SelectedIndex = 0;
                if (inventory != null)
                {
                    health = inventory.GetComponent<PlayerHealth>();
                    inventory.Changed += Render;
                    health.Changed += Render;
                }
            }
            Render();
        }
        private void Render()
        {
            bool ready = inventory != null && inventory.IsOwner && session.Phase == SessionPhase.InGame;
            hud.style.display = ready ? DisplayStyle.Flex : DisplayStyle.None;
            bool open = ready && session.InventoryOpen;
            feedback.style.display = ready ? DisplayStyle.Flex : DisplayStyle.None;
            feedback.style.bottom = open ? 24 : 140;
            tooltip.style.display = ready ? DisplayStyle.Flex : DisplayStyle.None;
            tooltip.style.bottom = open ? 60 : 110;
            panel.style.display = open ? DisplayStyle.Flex : DisplayStyle.None;
            hotbar.style.display = ready && !open ? DisplayStyle.Flex : DisplayStyle.None;
            if (!open) drag.Cancel();
            drag.SnapshotChanged();
            if (ready)
            {
                var snapshot = inventory.Snapshot;
                for (int i = 0; i < 24; i++)
                {
                    slots[i].Render(snapshot.Get(i), inventory.Catalog, i == SelectedIndex, inventory.PendingSource == i);
                    if (i < 8) hotSlots[i].Render(snapshot.Get(i), inventory.Catalog, i == SelectedIndex, inventory.PendingSource == i);
                }
                var value = health.Snapshot;
                healthText.text = $"Health {value.Current} / {value.Maximum}";
                healthFill.style.width = Length.Percent(value.Maximum > 0 ? Mathf.Clamp(100f * value.Current / value.Maximum, 0, 100) : 0);
                feedback.text = inventory.Message;
            }
            else { feedback.text = ""; tooltip.text = ""; }
            if (open && !wasOpen) slots[SelectedIndex].Focus();
            wasOpen = open;
        }
        private void Unbind()
        {
            drag?.Cancel();
            if (inventory != null) inventory.Changed -= Render;
            if (health != null) health.Changed -= Render;
            inventory = null; health = null;
        }
        private void OnDisable()
        {
            if (!bound) return;
            bound = false;
            root.UnregisterCallback<DetachFromPanelEvent>(DocumentDetached);
            session.Changed -= Bind;
            session.CancelInventoryDrag = null;
            session.SetPanel(true);
            toggle.performed -= Toggle;
            foreach (var action in selections) if (action != null) action.performed -= SelectKey;
            close.clicked -= Close;
            Unbind();
            drag.Dispose();
            foreach (var slot in slots) slot.RemoveFromHierarchy();
            foreach (var slot in hotSlots) slot.RemoveFromHierarchy();
            hud.style.display = DisplayStyle.None;
            feedback.style.display = DisplayStyle.None;
            tooltip.style.display = DisplayStyle.None;
            panel.style.display = DisplayStyle.None;
            hotbar.style.display = DisplayStyle.None;
            wasOpen = false;
        }
    }
}
