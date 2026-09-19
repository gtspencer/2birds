using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

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
        private Image equippedIcon;
        private Label equippedLabel;
        private VisualElement crosshair;
        private VisualElement chargeTrack;
        private VisualElement chargeFill;
        private VisualElement[] hotbarSlots;
        private VisualElement[] inventorySlots;
        private bool inventoryOpen;
        private int dragFromSlot = -1;
        private int dragPointer, moveSource = -1, focusedSlot = -1;
        private uint moveItem;
        private VisualElement dragElement;
        private MenuNavigation navigation;
        private SessionController session;
        private VisualElement dragGhost;
        private InputPresentation presentation;
        private InventoryInputHandler inventoryInput;
        private InputAction inventoryAction;
        private Label inventoryShortcut;
        private InteractionTooltip interactionTooltip;
        private ControlsHintPanel controlsHint;
        private PlayerInteraction interaction;
        private PlayerInputReader inputReader;
        private PlayerSeating seating;
        private ControlHint[] cartDriverHints;
        private ControlHint[] cartPassengerHints;
        private Label seatFeedback;
        private Label birdBalance, birdReward;
        private BirdRegistry birds;
        private IVisualElementScheduledItem hideReward;
        private int rewardTotal, rewardBonus;

        public bool InventoryOpen => inventoryOpen;
        public int InventoryInputFrame { get; private set; } = -1;

        private void OnEnable()
        {
            root = GetComponent<UIDocument>().rootVisualElement;
            birdBalance = root.Q<Label>("bird-balance");
            birdReward = root.Q<Label>("bird-reward");
            birds = BirdRegistry.Instance;
            if (birds)
            {
                birds.RewardChanged += BirdRewardChanged;
                birdBalance.text = birds.LocalBalance.ToString();
            }
            seatFeedback = root.Q<Label>("seat-feedback");
            hotbar = root.Q("hotbar");
            inventoryPanel = root.Q("inventory-panel");
            inventoryGrid = root.Q("inventory-grid");
            healthFill = root.Q("health-fill");
            healthText = root.Q<Label>("health-text");
            equippedPreview = root.Q("equipped-preview");
            equippedIcon = root.Q<Image>("equipped-icon");
            equippedLabel = root.Q<Label>("equipped-label");
            crosshair = root.Q("crosshair");
            chargeTrack = root.Q("charge-track");
            chargeFill = root.Q("charge-fill");
            HideCharge();
            session = SessionController.Instance;
            presentation = session.InputPresentation;
            inventoryInput = new InventoryInputHandler(session, ToggleInventory);
            inventoryAction = InputSystem.actions.FindAction("Player/Inventory");
            interactionTooltip = new InteractionTooltip(root, presentation);
            controlsHint = new ControlsHintPanel(root, presentation);
            BuildHotbar();
            BuildInventoryGrid();
            inventoryShortcut = inventoryPanel.Q<Label>("inventory-shortcut");
            if (inventoryShortcut == null)
            {
                inventoryShortcut = new Label { name = "inventory-shortcut" };
                inventoryPanel.Add(inventoryShortcut);
            }
            presentation.Changed += RefreshBindings;
            presentation.Interrupted += CancelDrag;
            navigation = new MenuNavigation(root, presentation, () => inventoryOpen ? inventoryPanel : null,
                () => inventorySlots[focusedSlot < 0 ? 0 : focusedSlot]);
            inventoryPanel.RegisterCallback<NavigationCancelEvent>(InventoryBack);
            RefreshBindings();
        }

        private void BuildHotbar()
        {
            hotbar.Clear();
            hotbarSlots = new VisualElement[PlayerInventory.HotbarSize];
            for (int i = 0; i < PlayerInventory.HotbarSize; i++)
            {
                int idx = i;
                var slot = MakeSlot(i);
                AddShortcut(slot, i + 1);
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
                int index = i;
                var slot = MakeSlot(i);
                slot.focusable = true;
                slot.RegisterCallback<FocusInEvent>(_ => focusedSlot = index);
                slot.RegisterCallback<NavigationSubmitEvent>(evt =>
                {
                    if (InventoryInputFrame != Time.frameCount) SelectDestination(index);
                    evt.StopPropagation();
                });
                slot.RegisterCallback<NavigationMoveEvent>(evt => MoveFocus(evt, index));
                if (i < PlayerInventory.HotbarSize)
                {
                    AddShortcut(slot, i + 1);
                }
                slot.RegisterCallback<PointerDownEvent>(OnSlotPointerDown);
                slot.RegisterCallback<PointerMoveEvent>(OnSlotPointerMove);
                slot.RegisterCallback<PointerUpEvent>(OnSlotPointerUp);
                inventoryGrid.Add(slot);
                inventorySlots[i] = slot;
            }
        }

        private static void AddShortcut(VisualElement slot, int number)
        {
            var key = new Label { text = number.ToString(), pickingMode = PickingMode.Ignore };
            key.AddToClassList("slot-key");
            var glyph = new Image { pickingMode = PickingMode.Ignore };
            glyph.AddToClassList("slot-binding-glyph");
            glyph.style.display = DisplayStyle.None;
            slot.Add(key);
            slot.Add(glyph);
        }

        private void RefreshBindings()
        {
            if (presentation.ActiveDevice is Gamepad) CancelDrag();
            inventoryShortcut.text = $"{presentation.Label("UI/Submit")} {(moveSource < 0 ? "Select item" : "Move item")}   " +
                $"{presentation.Label("UI/Cancel")} {(moveSource < 0 ? "Close" : "Cancel move")}   {presentation.Label("UI/Pause")} Close";
            if (presentation.ActiveDevice is not Gamepad)
                inventoryShortcut.text += $"   {presentation.Resolve(inventoryAction).Text} Close";
        }

        private static VisualElement MakeSlot(int index)
        {
            var slot = new VisualElement();
            slot.AddToClassList("slot");
            slot.userData = index;
            var icon = new Image { name = "slot-icon" };
            icon.AddToClassList("slot-icon");
            slot.Add(icon);
            // Uncomment to show item name (positioned at the bottom of the slot via USS):
            // var label = new Label { name = "slot-label" };
            // label.AddToClassList("slot-label");
            // slot.Add(label);
            var count = new Label { name = "slot-count" };
            count.AddToClassList("slot-count");
            slot.Add(count);
            return slot;
        }

        private void OnHotbarClick(int slot)
        {
            if (inventory && inventory.IsOwner && inventory.CanEquip && !inventoryOpen && session &&
                session.Phase == SessionPhase.InGame && !session.PanelOpen && !session.ConsoleOpen && !presentation.SuppressInput)
                inventory.SelectSlot((sbyte)slot);
        }

        private void OnSlotPointerDown(PointerDownEvent evt)
        {
            if (!CanMove || presentation.SuppressInput) return;
            if (evt.button == 1) { ClearMove(); CancelDrag(); evt.StopPropagation(); return; }
            if (evt.button != 0) return;
            var slot = evt.currentTarget as VisualElement;
            int index = (int)slot.userData;
            slot.Focus();
            if (moveSource >= 0) { SelectDestination(index); evt.StopPropagation(); return; }
            if (inventory.GetSlot(index).IsEmpty) return;
            dragFromSlot = index;
            dragElement = slot;
            dragPointer = evt.pointerId;
            slot.CapturePointer(evt.pointerId);
            EnsureDragGhost();
            dragGhost.style.display = DisplayStyle.Flex;
            var item = inventory.GetSlot(index);
            var def = itemRegistry != null ? itemRegistry.Get(item.ItemId) : null;
            var ghostIcon = dragGhost.Q<Image>("slot-icon");
            if (ghostIcon != null) ghostIcon.sprite = def != null ? def.Icon : null;
            PositionGhost(evt.position);
        }

        private void OnSlotPointerMove(PointerMoveEvent evt)
        {
            if (dragFromSlot >= 0) PositionGhost(evt.position);
        }

        private void OnSlotPointerUp(PointerUpEvent evt)
        {
            if (dragFromSlot < 0) return;
            if (!CanMove || presentation.SuppressInput) { CancelDrag(); return; }
            (evt.currentTarget as VisualElement)?.ReleasePointer(evt.pointerId);
            if (dragGhost != null) dragGhost.style.display = DisplayStyle.None;

            int target = FindSlotAt(evt.position);
            if (target >= 0 && target != dragFromSlot)
                inventory.SwapSlots(dragFromSlot, target);
            else if (target < 0 && !inventoryPanel.worldBound.Contains(evt.position))
                inventory.DropSlot(dragFromSlot);

            dragFromSlot = -1;
            dragElement = null;
        }

        private bool CanMove => inventoryOpen && inventory && inventory.IsOwner && session &&
            session.Phase == SessionPhase.InGame && !session.PanelOpen && !session.ConsoleOpen;

        private void MoveFocus(NavigationMoveEvent evt, int index)
        {
            int columns = PlayerInventory.HotbarSize;
            int destination = evt.direction switch
            {
                NavigationMoveEvent.Direction.Left => index % columns > 0 ? index - 1 : index,
                NavigationMoveEvent.Direction.Right => index % columns < columns - 1 ? index + 1 : index,
                NavigationMoveEvent.Direction.Up => index - columns,
                NavigationMoveEvent.Direction.Down => index + columns,
                _ => index
            };
            if (destination >= 0 && destination < inventorySlots.Length) inventorySlots[destination].Focus();
            evt.StopPropagation();
        }

        private void SelectDestination(int index)
        {
            if (!CanMove || presentation.SuppressInput) return;
            if (moveSource < 0)
            {
                var item = inventory.GetSlot(index);
                if (item.IsEmpty) return;
                moveSource = index;
                moveItem = item.WorldIds[0];
            }
            else
            {
                int source = moveSource;
                ClearMove();
                if (source != index) inventory.SwapSlots(source, index);
            }
            Refresh();
            RefreshBindings();
        }

        private void ClearMove()
        {
            if (moveSource >= 0) inventorySlots[moveSource].RemoveFromClassList("move-source");
            moveSource = -1;
            moveItem = 0;
            if (inventoryShortcut != null) RefreshBindings();
        }

        private void InventoryBack(NavigationCancelEvent evt)
        {
            if (!inventoryOpen || presentation.SuppressInput || InventoryInputFrame == Time.frameCount) return;
            if (moveSource >= 0) ClearMove(); else CloseInventory();
            evt.StopPropagation();
        }

        private void CancelDrag()
        {
            if (dragElement != null) dragElement.ReleasePointer(dragPointer);
            dragElement = null;
            dragFromSlot = -1;
            if (dragGhost != null) dragGhost.style.display = DisplayStyle.None;
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
            if (inventory) inventory.InventoryChanged -= Refresh;
            if (!inv) CloseInventory();
            inventory = inv;
            playerState = state;
            equipment = inv ? inv.Equipment : null;
            interaction = inv ? inv.GetComponent<PlayerInteraction>() : null;
            inputReader = inv ? inv.GetComponent<PlayerInputReader>() : null;
            seating = inv ? inv.GetComponent<PlayerSeating>() : null;
            inventoryInput?.Bind(inv, inputReader);
            if (inventory) inventory.InventoryChanged += Refresh;
            EnsureCartHints();
            EnsurePassengerHints();
            Refresh();
        }

        private void Update()
        {
            var player = SessionController.Instance?.LocalPlayer;
            if (!player)
            {
                if (inventory) Bind(null, null);
                HideCharge();
                return;
            }
            if (!inventory || inventory.gameObject != player.gameObject)
                Bind(player.GetComponent<PlayerInventory>(), player.GetComponent<PlayerNetworkState>());
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
            interactionTooltip.Update(interaction);
            if (seating && seating.Seated && !seating.TransitionPending)
                controlsHint.Show(seating.IsDriver ? cartDriverHints : cartPassengerHints);
            else controlsHint.Hide();
        }

        private void EnsureCartHints()
        {
            if (cartDriverHints != null) return;
            var map = InputSystem.actions?.FindActionMap("Player");
            if (map == null) return;
            cartDriverHints = new[]
            {
                new ControlHint
                {
                    Action = map.FindAction("Move"), Label = "Drive / Steer",
                    TextOverride = group => group == InputBindings.KeyboardMouse ? "WASD" : null
                },
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
            if (InventoryInputFrame == Time.frameCount) return;
            InventoryInputFrame = Time.frameCount;
            inventoryInput?.SuppressInput();
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
            if (focusedSlot < 0) focusedSlot = inventory.SelectedSlot >= 0 ? inventory.SelectedSlot : 0;
            inventoryPanel.schedule.Execute(() => { if (inventoryOpen) inventorySlots[focusedSlot].Focus(); });
        }

        public void CloseInventory()
        {
            if (!inventoryOpen) return;
            InventoryInputFrame = Time.frameCount;
            inventoryInput?.SuppressInput();
            inventoryOpen = false;
            CancelDrag();
            ClearMove();
            inventoryPanel.style.display = DisplayStyle.None;
            navigation?.Repair();
            if (crosshair != null) crosshair.style.display = DisplayStyle.Flex;
            if (inputReader != null) inputReader.InventoryOpen = false;
        }

        private void Refresh()
        {
            if (inventory == null) return;
            if (moveSource >= 0)
            {
                var source = inventory.GetSlot(moveSource);
                if (source.IsEmpty || System.Array.IndexOf(source.WorldIds, moveItem) < 0) ClearMove();
            }
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
                SetClass(inventorySlots[i], "move-source", moveSource == i);
            }

            var equipped = inventory.GetEquipped();
            if (equippedPreview != null)
            {
                var def = !equipped.IsEmpty && itemRegistry != null ? itemRegistry.Get(equipped.ItemId) : null;
                if (equippedIcon != null) equippedIcon.sprite = def != null ? def.Icon : null;
                // Uncomment to show equipped item name:
                // if (equippedLabel != null) equippedLabel.text = def != null ? def.ItemName : "";
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
            var icon = slot.Q<Image>("slot-icon");
            var count = slot.Q<Label>("slot-count");
            if (item.IsEmpty)
            {
                if (icon != null) icon.sprite = null;
                // Uncomment to show item name:
                // var label = slot.Q<Label>("slot-label"); if (label != null) label.text = "";
                count.text = "";
                SetClass(slot, "filled", false);
            }
            else
            {
                var def = itemRegistry != null ? itemRegistry.Get(item.ItemId) : null;
                if (icon != null) icon.sprite = def != null ? def.Icon : null;
                // Uncomment to show item name:
                // var label = slot.Q<Label>("slot-label"); if (label != null) label.text = def != null ? def.ItemName : $"Item {item.ItemId}";
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

        private void BirdRewardChanged(BirdReward reward)
        {
            birdBalance.text = reward.Balance.ToString();
            if (!reward.Notify)
            {
                hideReward?.Pause(); birdReward.text = ""; rewardTotal = rewardBonus = 0;
                return;
            }
            rewardTotal += reward.Reward + reward.Bonus; rewardBonus += reward.Bonus;
            birdReward.text = rewardBonus > 0 ? $"+{rewardTotal}  (+{rewardBonus} multi-kill bonus)" : $"+{rewardTotal}";
            hideReward?.Pause();
            hideReward = birdReward.schedule.Execute(() => { birdReward.text = ""; rewardTotal = rewardBonus = 0; }).StartingIn(2500);
        }

        private void OnDisable()
        {
            if (birds) birds.RewardChanged -= BirdRewardChanged;
            hideReward?.Pause();
            HideCharge();
            interactionTooltip?.Dispose();
            controlsHint?.Dispose();
            if (presentation != null) presentation.Changed -= RefreshBindings;
            if (presentation != null) presentation.Interrupted -= CancelDrag;
            navigation?.Dispose();
            inventoryPanel.UnregisterCallback<NavigationCancelEvent>(InventoryBack);
            inventoryInput?.Dispose();
            inventoryInput = null;
            Bind(null, null);
        }
    }
}
