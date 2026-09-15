using UnityEngine;

namespace TwoBirds
{
    public struct ItemStack
    {
        public byte ItemId;
        public uint[] WorldIds;

        public ushort Count => (ushort)(WorldIds?.Length ?? 0);

        public bool IsEmpty => ItemId == 0 || Count == 0;

        public ItemStack(byte itemId, uint[] worldIds)
        {
            ItemId = itemId;
            WorldIds = worldIds;
        }
    }

    [CreateAssetMenu(menuName = "Two Birds/Item Definition")]
    public sealed class ItemDefinition : ScriptableObject
    {
        public string ItemName = "";
        public Sprite Icon;
        public GameObject WorldPrefab;
        public bool Stackable;
        [Min(1)] public int MaxStack = 1;

        [Header("Physics")]
        [Min(0f)] public float ThrowSpeed = 14f;
        [Min(0f)] public float DropSpeed = 1.5f;
        [Min(0f)] public float VelocityInheritance = 1f;
        [Min(0.01f)] public float Mass = 1f;
        [Min(0f)] public float LinearDamping = 0.05f;
        [Min(0f)] public float AngularDamping = 0.1f;
        public Vector3 InitialSpin = new(3f, 1f, 2f);
        public PhysicsMaterial PhysicsMaterial;
        public CollisionDetectionMode CollisionDetection = CollisionDetectionMode.ContinuousDynamic;
        [Min(0f)] public float MaxSpeed = 50f;
        public bool OverrideSleepThreshold;
        [Min(0f)] public float SleepThreshold = 0.005f;
    }
}
