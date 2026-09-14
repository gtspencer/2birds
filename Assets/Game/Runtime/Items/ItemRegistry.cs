using UnityEngine;

namespace TwoBirds
{
    [CreateAssetMenu(menuName = "Two Birds/Item Registry")]
    public sealed class ItemRegistry : ScriptableObject
    {
        public ItemDefinition[] Items = System.Array.Empty<ItemDefinition>();

        public ItemDefinition Get(byte id)
        {
            if (id == 0 || id > Items.Length) return null;
            return Items[id - 1];
        }

        public byte GetId(ItemDefinition definition)
        {
            for (int i = 0; i < Items.Length; i++)
                if (Items[i] == definition) return (byte)(i + 1);
            return 0;
        }
    }
}
