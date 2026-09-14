using UnityEngine;

namespace TwoBirds
{
    public struct ItemStack
    {
        public byte ItemId;
        public ushort Count;

        public bool IsEmpty => ItemId == 0 || Count == 0;

        public ItemStack(byte itemId, ushort count)
        {
            ItemId = itemId;
            Count = count;
        }
    }

    [CreateAssetMenu(menuName = "Two Birds/Item Definition")]
    public sealed class ItemDefinition : ScriptableObject
    {
        public string ItemName = "";
        public Sprite Icon;
        public GameObject WorldPrefab;
        [Tooltip("Preserve the prefab's layers when held instead of using Pickup/Generic.")]
        public bool UsePrefabLayerWhenHeld;
        public bool Stackable;
        [Min(1)] public int MaxStack = 1;
    }
}
