using UnityEngine;

namespace TwoBirds
{
    [CreateAssetMenu(menuName = "Two Birds/Item Registry")]
    public sealed class ItemRegistry : ScriptableObject
    {
        public event System.Action ContentChanged;
        public void NotifyContentChanged() => ContentChanged?.Invoke();
        private void OnValidate() => NotifyContentChanged();
        public HeldItemSettings HeldItemDefaults;
        public ItemDefinition[] Items = System.Array.Empty<ItemDefinition>();

        public ItemDefinition Get(byte id)
        {
            if (id == 0) return null;
            foreach (var item in Items)
                if (item && item.ItemId == id) return item;
            return null;
        }

        public byte GetId(ItemDefinition definition) => definition ? definition.ItemId : (byte)0;
    }
}
