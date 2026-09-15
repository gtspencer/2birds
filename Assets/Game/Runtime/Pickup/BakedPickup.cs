using UnityEngine;

namespace TwoBirds
{
    public sealed class BakedPickup : MonoBehaviour
    {
        [HideInInspector] public uint BakedId;
        public byte ItemId = 1;
        public ItemDefinition Item;
    }
}
