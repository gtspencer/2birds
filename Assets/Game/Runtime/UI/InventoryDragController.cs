using System;
using UnityEngine;
using UnityEngine.UIElements;

namespace TwoBirds
{
    public sealed class InventoryDragController : IDisposable
    {
        private readonly VisualElement root, panel;
        private readonly InventorySlotView[] slots;
        private readonly Label ghost;
        private readonly Func<PlayerInventory> inventory;
        private readonly Action<int> select;
        private InventorySlotView source;
        private Vector2 origin;
        private int pointer;
        private bool dragging;
        private ulong revision;
        public InventoryDragController(VisualElement root, VisualElement panel, InventorySlotView[] slots, Func<PlayerInventory> inventory, Action<int> select)
        {
            this.root = root; this.panel = panel; this.slots = slots; this.inventory = inventory; this.select = select;
            ghost = new Label { pickingMode = PickingMode.Ignore };
            ghost.AddToClassList("inventory-ghost");
            ghost.style.display = DisplayStyle.None;
            root.Add(ghost);
            foreach (var slot in slots) slot.RegisterCallback<PointerDownEvent>(Down);
            root.RegisterCallback<PointerMoveEvent>(Move);
            root.RegisterCallback<PointerUpEvent>(Up);
            root.RegisterCallback<PointerCaptureOutEvent>(CaptureLost);
            root.RegisterCallback<DetachFromPanelEvent>(Detached);
        }
        private void Down(PointerDownEvent evt)
        {
            var owner = inventory();
            if (evt.button != 0 || owner == null || owner.Pending || source != null) return;
            var slot = (InventorySlotView)evt.currentTarget;
            slot.Focus();
            var snapshot = owner.Snapshot;
            if (snapshot.Get(slot.Index).IsEmpty) { select(slot.Index); return; }
            source = slot; pointer = evt.pointerId; origin = evt.position; revision = snapshot.Revision;
            source.CapturePointer(pointer);
            evt.StopPropagation();
        }
        private void Move(PointerMoveEvent evt)
        {
            if (source == null || evt.pointerId != pointer) return;
            var owner = inventory();
            var snapshot = owner != null ? owner.Snapshot : default;
            if (owner == null || snapshot.Revision != revision) { Cancel(); return; }
            if (!dragging && Vector2.Distance(origin, evt.position) < 5f) return;
            dragging = true;
            ghost.text = source.ItemName;
            ghost.style.display = DisplayStyle.Flex;
            var local = root.WorldToLocal(evt.position);
            ghost.style.left = local.x + 12; ghost.style.top = local.y + 12;
            var from = snapshot.Get(source.Index);
            var definition = owner.Catalog.Resolve(from.DefinitionId);
            foreach (var slot in slots)
            {
                bool hit = slot.worldBound.Contains(evt.position);
                var entry = snapshot.Get(slot.Index);
                bool full = definition != null && definition.MaximumStack > 1 && entry.DefinitionId == from.DefinitionId && entry.Quantity == definition.MaximumStack;
                slot.EnableInClassList("valid-target", hit && slot != source && !full);
                slot.EnableInClassList("invalid-target", hit && (slot == source || full));
            }
        }
        private void Up(PointerUpEvent evt)
        {
            if (source == null || evt.pointerId != pointer || evt.button != 0) return;
            var owner = inventory();
            int from = source.Index;
            bool submit = dragging && owner != null && owner.Snapshot.Revision == revision && Application.isFocused && root.worldBound.Contains(evt.position);
            int destination = -1;
            foreach (var slot in slots) if (slot.worldBound.Contains(evt.position)) { destination = slot.Index; break; }
            var picked = root.panel?.Pick(evt.position);
            bool viewport = !panel.worldBound.Contains(evt.position) &&
                (picked == null || picked == root || picked == root.panel.visualTree);
            bool click = !dragging;
            Cancel();
            if (click) select(from);
            else if (submit && (destination >= 0 || viewport)) owner.Request(from, destination, destination < 0);
            evt.StopPropagation();
        }
        private void CaptureLost(PointerCaptureOutEvent evt) { if (source != null && evt.pointerId == pointer) Cancel(); }
        private void Detached(DetachFromPanelEvent evt) => Cancel();
        public bool Cancel()
        {
            if (source == null) return false;
            var captured = source; source = null;
            dragging = false;
            ghost.style.display = DisplayStyle.None;
            if (captured.HasPointerCapture(pointer)) captured.ReleasePointer(pointer);
            foreach (var slot in slots) { slot.RemoveFromClassList("valid-target"); slot.RemoveFromClassList("invalid-target"); }
            return true;
        }
        public void SnapshotChanged() { if (source != null && (inventory() == null || inventory().Snapshot.Revision != revision)) Cancel(); }
        public void Dispose()
        {
            Cancel();
            foreach (var slot in slots) slot.UnregisterCallback<PointerDownEvent>(Down);
            root.UnregisterCallback<PointerMoveEvent>(Move);
            root.UnregisterCallback<PointerUpEvent>(Up);
            root.UnregisterCallback<PointerCaptureOutEvent>(CaptureLost);
            root.UnregisterCallback<DetachFromPanelEvent>(Detached);
            ghost.RemoveFromHierarchy();
        }
    }
}
