using UnityEngine.UIElements;

namespace TwoBirds
{
    public sealed class InventorySlotView : VisualElement
    {
        public int Index { get; }
        public string ItemName { get; private set; } = "Empty";
        private readonly Label key = new(), item = new(), count = new();
        private readonly Image icon = new();
        public InventorySlotView(int index)
        {
            Index = index;
            focusable = true;
            AddToClassList("inventory-slot");
            key.AddToClassList("inventory-key");
            item.AddToClassList("inventory-item");
            count.AddToClassList("inventory-count");
            icon.AddToClassList("inventory-icon");
            key.text = index < 8 ? (index + 1).ToString() : "";
            foreach (var child in new VisualElement[] { key, icon, item, count }) { child.pickingMode = PickingMode.Ignore; Add(child); }
        }
        public void Render(InventoryEntry entry, ItemCatalog catalog, bool selected, bool pending)
        {
            var definition = entry.IsEmpty ? null : catalog.Resolve(entry.DefinitionId);
            ItemName = entry.IsEmpty ? "Empty" : definition == null ? entry.DefinitionId : definition.DisplayName;
            if (string.IsNullOrWhiteSpace(ItemName)) ItemName = entry.DefinitionId;
            item.text = entry.IsEmpty ? "" : ItemName;
            icon.sprite = definition != null ? definition.Icon : null;
            icon.style.display = icon.sprite != null ? DisplayStyle.Flex : DisplayStyle.None;
            item.style.display = icon.sprite == null ? DisplayStyle.Flex : DisplayStyle.None;
            count.text = entry.Quantity > 1 ? entry.Quantity.ToString() : "";
            EnableInClassList("empty", entry.IsEmpty);
            EnableInClassList("occupied", !entry.IsEmpty);
            EnableInClassList("selected", selected);
            EnableInClassList("pending", pending);
        }
    }
}
