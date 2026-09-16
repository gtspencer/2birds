using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;
using Cursor = UnityEngine.Cursor;

namespace TwoBirds
{
    [DefaultExecutionOrder(200)]
    public sealed class HudController : MonoBehaviour
    {
        [SerializeField] private ItemRegistry itemRegistry;

        private PlayerInventory inventory;
        private PlayerNetworkState playerState;
        private PlayerEquipment equipment;
        private VisualElement root;
        private VisualElement hotbar;
        private VisualElement inventoryPanel;
        private VisualElement inventoryGrid;
        private VisualElement healthFill;
        private Label healthText;
        private VisualElement equippedPreview;
        private Label equippedLabel;
        private VisualElement crosshair;
        private VisualElement chargeTrack;
        private VisualElement chargeFill;
        private VisualElement[] hotbarSlots;
        private VisualElement[] inventorySlots;
        private bool inventoryOpen;
        private int dragFromSlot = -1;
        private VisualElement dragGhost;
        private SteamInputGlyphs glyphs;
        private InteractionTooltip interactionTooltip;
        private ControlsHintPanel controlsHint;
        private PlayerInteraction interaction;
        private PlayerInputReader inputReader;
        private PlayerSeating seating;
        private ControlHint[] cartDriverHints;
        private ControlHint[] cartPassengerHints;
        private Label seatFeedback;

        public bool InventoryOpen => inventoryOpen;

        private void OnEnable()
        {
            root = GetComponent<UIDocument>().rootVisualElement;
            seatFeedback = root.Q<Label>("seat-feedback");
            hotbar = root.Q("hotbar");
            inventoryPanel = root.Q("inventory-panel");
            inventoryGrid = root.Q("inventory-grid");
            healthFill = root.Q("health-fill");
            healthText = root.Q<Label>("health-text");
            equippedPreview = root.Q("equipped-preview");
            equippedLabel = root.Q<Label>("equipped-label");
            crosshair = root.Q("crosshair");
            chargeTrack = root.Q("charge-track");
            chargeFill = root.Q("charge-fill");
            HideCharge();
            glyphs = new SteamInputGlyphs();
            interactionTooltip = new InteractionTooltip(root, glyphs);
            controlsHint = new ControlsHintPanel(root, glyphs);
            BuildHotbar();
            BuildInventoryGrid();
        }

        private void BuildHotbar()
        {
            hotbar.Clear();
            hotbarSlots = new VisualElement[PlayerInventory.HotbarSize];
            for (int i = 0; i < PlayerInventory.HotbarSize; i++)
            {
                int idx = i;
                var slot = MakeSlot(i);
                var key = new Label((i + 1).ToString());
                key.AddToClassList("slot-key");
                slot.Add(key);
                slot.RegisterCallback<ClickEvent>(_ => OnHotbarClick(idx));
                hotbar.Add(slot);
                hotbarSlots[i] = slot;
            }
        }

        private void BuildInventoryGrid()
        {
            inventoryGrid.Clear();
            inventorySlots = new VisualElement[PlayerInventory.SlotCount];
            for (int i = 0; i < PlayerInventory.SlotCount; i++)
            {
                var slot = MakeSlot(i);
                slot.RegisterCallback<PointerDownEvent>(OnSlotPointerDown);
                slot.RegisterCallback<PointerMoveEvent>(OnSlotPointerMove);
                slot.RegisterCallback<PointerUpEvent>(OnSlotPointerUp);
                inventoryGrid.Add(slot);
                inventorySlots[i] = slot;
            }
        }

        private static VisualElement MakeSlot(int index)
        {
            var slot = new VisualElement();
            slot.AddToClassList("slot");
            slot.userData = index;
            var label = new Label { name = "slot-label" };
            label.AddToClassList("slot-label");
            slot.Add(label);
            var count = new Label { name = "slot-count" };
            count.AddToClassList("slot-count");
            slot.Add(count);
            return slot;
        }

        private void OnHotbarClick(int slot)
        {
            if (inventory != null && inventory.IsOwner && inventory.CanEquip)
                inventory.SelectSlot((sbyte)slot);
        }

        private void OnSlotPointerDown(PointerDownEvent evt)
        {
            if (inventory == null || !inventory.IsOwner) return;
            var slot = evt.currentTarget as VisualElement;
            int index = (int)slot.userData;
            if (inventory.GetSlot(index).IsEmpty) return;
            dragFromSlot = index;
            slot.CapturePointer(evt.pointerId);
            EnsureDragGhost();
            dragGhost.style.display = DisplayStyle.Flex;
            var item = inventory.GetSlot(index);
            dragGhost.Q<Label>("slot-label").text = GetItemName(item.ItemId);
            PositionGhost(evt.position);
        }

        private void OnSlotPointerMove(PointerMoveEvent evt)
        {
            if (dragFromSlot >= 0) PositionGhost(evt.position);
        }

        private void OnSlotPointerUp(PointerUpEvent evt)
        {
            if (dragFromSlot < 0) return;
            (evt.currentTarget as VisualElement)?.ReleasePointer(evt.pointerId);
            if (dragGhost != null) dragGhost.style.display = DisplayStyle.None;

            int target = FindSlotAt(evt.position);
            if (target >= 0 && target != dragFromSlot)
                inventory.SwapSlots(dragFromSlot, target);
            else if (target < 0 && !inventoryPanel.worldBound.Contains(evt.position))
                inventory.DropSlot(dragFromSlot);

            dragFromSlot = -1;
        }

        private int FindSlotAt(Vector3 pos)
        {
            for (int i = 0; i < inventorySlots.Length; i++)
                if (inventorySlots[i].worldBound.Contains(pos))
                    return i;
            return -1;
        }

        private void EnsureDragGhost()
        {
            if (dragGhost != null) return;
            dragGhost = MakeSlot(0);
            dragGhost.AddToClassList("drag-ghost");
            dragGhost.pickingMode = PickingMode.Ignore;
            foreach (var child in dragGhost.Children())
                child.pickingMode = PickingMode.Ignore;
            root.Add(dragGhost);
        }

        private void PositionGhost(Vector3 pos)
        {
            if (dragGhost == null) return;
            dragGhost.style.left = pos.x - 25;
            dragGhost.style.top = pos.y - 25;
        }

        private void Bind(PlayerInventory inv, PlayerNetworkState state)
        {
            HideCharge();
            if (inventory != null) inventory.InventoryChanged -= Refresh;
            inventory = inv;
            playerState = state;
            equipment = inv != null ? inv.Equipment : null;
            interaction = inv != null ? inv.GetComponent<PlayerInteraction>() : null;
            inputReader = inv != null ? inv.GetComponent<PlayerInputReader>() : null;
            seating = inv != null ? inv.GetComponent<PlayerSeating>() : null;
            if (inventory != null) inventory.InventoryChanged += Refresh;
            EnsureCartHints();
            EnsurePassengerHints();
            Refresh();
        }

        private void Update()
        {
            var player = SessionController.Instance?.LocalPlayer;
            if (player == null)
            {
                if (inventory != null) Bind(null, null);
                HideCharge();
                return;
            }
            if (inventory == null || inventory.gameObject != player.gameObject)
                Bind(player.GetComponent<PlayerInventory>(), player.GetComponent<PlayerNetworkState>());
            if (!inventory.IsOwner) return;

            var session = SessionController.Instance;
            if (session == null || session.Phase != SessionPhase.InGame || session.PanelOpen) return;

            var keyboard = Keyboard.current;
            if (keyboard != null && keyboard.tabKey.wasPressedThisFrame)
                ToggleInventory();

            var gamepad = Gamepad.current;
            if (gamepad != null && gamepad.selectButton.wasPressedThisFrame)
                ToggleInventory();

            if (!inventoryOpen && inventory.CanEquip)
            {
                if (keyboard != null)
                {
                    for (int i = 0; i < PlayerInventory.HotbarSize; i++)
                        if (keyboard[Key.Digit1 + i].wasPressedThisFrame)
                        { inventory.SelectSlot((sbyte)i); break; }
                }
                if (gamepad != null)
                {
                    if (gamepad.leftShoulder.wasPressedThisFrame) CycleHotbar(-1);
                    if (gamepad.rightShoulder.wasPressedThisFrame) CycleHotbar(1);
                }
            }
        }

        private void CycleHotbar(int direction)
        {
            sbyte current = inventory.SelectedSlot;
            sbyte next = current < 0
                ? (sbyte)(direction > 0 ? 0 : PlayerInventory.HotbarSize - 1)
                : (sbyte)((current + direction + PlayerInventory.HotbarSize) % PlayerInventory.HotbarSize);
            inventory.SelectSlot(next);
        }

        private void LateUpdate()
        {
            seatFeedback.text = seating ? seating.RequestFeedback : "";
            var session = SessionController.Instance;
            if (session == null || session.Phase != SessionPhase.InGame || session.PanelOpen ||
                session.LocalPlayer == null || inventory == null || !inventory.IsOwner ||
                inputReader == null || !inputReader.GameplayActive)
            {
                HideCharge();
                interactionTooltip.Hide();
                controlsHint.Hide();
                return;
            }
            if (equipment != null && equipment.IsCharging)
            {
                chargeTrack.style.display = DisplayStyle.Flex;
                chargeFill.style.width = Length.Percent(equipment.Charge01 * 100f);
            }
            else HideCharge();
            interactionTooltip.Update(interaction, inputReader.ActiveDevice);
            if (seating && seating.Seated && !seating.TransitionPending)
                controlsHint.Show(seating.IsDriver ? cartDriverHints : cartPassengerHints, inputReader.ActiveDevice);
            else controlsHint.Hide();
        }

        private void EnsureCartHints()
        {
            if (cartDriverHints != null) return;
            var map = InputSystem.actions?.FindActionMap("Player");
            if (map == null) return;
            cartDriverHints = new[]
            {
                new ControlHint { Action = map.FindAction("Move"), Label = "Drive / Steer" },
                new ControlHint { Action = map.FindAction("Jump"), Label = "Handbrake" },
                new ControlHint { Action = map.FindAction("Lights"), Label = "Lights" },
                new ControlHint { Action = map.FindAction("Horn"), Label = "Horn" },
                new ControlHint { Action = map.FindAction("ExitVehicle"), Label = "Exit" },
            };
        }

        private void EnsurePassengerHints()
        {
            if (cartPassengerHints != null) return;
            var map = InputSystem.actions?.FindActionMap("Player");
            if (map == null) return;
            cartPassengerHints = new[]
            {
                new ControlHint { Action = map.FindAction("ExitVehicle"), Label = "Exit" },
            };
        }

        private void HideCharge()
        {
            if (chargeTrack != null) chargeTrack.style.display = DisplayStyle.None;
            if (chargeFill != null) chargeFill.style.width = Length.Percent(0f);
        }

        private void ToggleInventory()
        {
            if (inventoryOpen) CloseInventory(); else OpenInventory();
        }

        private void OpenInventory()
        {
            HideCharge();
            if (inputReader != null) inputReader.InventoryOpen = true;
            inventoryOpen = true;
            inventoryPanel.style.display = DisplayStyle.Flex;
            if (crosshair != null) crosshair.style.display = DisplayStyle.None;
            Refresh();
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }

        public void CloseInventory()
        {
            if (!inventoryOpen) return;
            inventoryOpen = false;
            inventoryPanel.style.display = DisplayStyle.None;
            if (crosshair != null) crosshair.style.display = DisplayStyle.Flex;
            if (inputReader != null) inputReader.InventoryOpen = false;
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }

        private void Refresh()
        {
            if (inventory == null) return;
            sbyte selected = inventory.SelectedSlot;

            for (int i = 0; i < PlayerInventory.HotbarSize && i < (hotbarSlots?.Length ?? 0); i++)
            {
                UpdateSlotVisual(hotbarSlots[i], inventory.GetSlot(i));
                SetClass(hotbarSlots[i], "selected", selected == i);
            }

            for (int i = 0; i < PlayerInventory.SlotCount && i < (inventorySlots?.Length ?? 0); i++)
            {
                UpdateSlotVisual(inventorySlots[i], inventory.GetSlot(i));
                SetClass(inventorySlots[i], "selected", i < PlayerInventory.HotbarSize && selected == i);
            }

            var equipped = inventory.GetEquipped();
            if (equippedLabel != null)
            {
                equippedLabel.text = equipped.IsEmpty ? "" : GetItemName(equipped.ItemId);
                equippedPreview.style.display = equipped.IsEmpty ? DisplayStyle.None : DisplayStyle.Flex;
            }

            if (playerState != null && healthFill != null)
            {
                float health = playerState.Health;
                healthFill.style.width = new StyleLength(new Length(health, LengthUnit.Percent));
                if (healthText != null) healthText.text = Mathf.CeilToInt(health).ToString();
            }
        }

        private void UpdateSlotVisual(VisualElement slot, ItemStack item)
        {
            var label = slot.Q<Label>("slot-label");
            var count = slot.Q<Label>("slot-count");
            if (item.IsEmpty)
            {
                label.text = "";
                count.text = "";
                SetClass(slot, "filled", false);
            }
            else
            {
                label.text = GetItemName(item.ItemId);
                count.text = item.Count > 1 ? item.Count.ToString() : "";
                SetClass(slot, "filled", true);
            }
        }

        private static void SetClass(VisualElement el, string cls, bool value)
        {
            if (value) el.AddToClassList(cls); else el.RemoveFromClassList(cls);
        }

        private string GetItemName(byte itemId)
        {
            if (itemRegistry == null) return $"Item {itemId}";
            var def = itemRegistry.Get(itemId);
            return def != null ? def.ItemName : $"Item {itemId}";
        }

        private void OnDisable()
        {
            HideCharge();
            interactionTooltip?.Dispose();
            controlsHint?.Dispose();
            glyphs?.Dispose();
            Bind(null, null);
        }
    }
}
