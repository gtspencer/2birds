using UnityEngine;

namespace TwoBirds
{
    public sealed class BakedPickup : MonoBehaviour
    {
        [HideInInspector] public uint BakedId;
        public ItemDefinition Item;
        public byte ItemId => Item ? Item.ItemId : (byte)0;
    }
}
