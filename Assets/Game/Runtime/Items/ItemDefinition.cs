using UnityEngine;
using UnityEngine.Serialization;

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
        [Tooltip("Display name shown in the inventory HUD and drag ghost.")]
        public string ItemName = "";
        [Tooltip("Sprite shown in inventory slots (set via the Icon Capture Window).")]
        public Sprite Icon;
        [Tooltip("The 3D object spawned when this item exists in the world. Swapping it changes the model and colliders.")]
        public GameObject WorldPrefab;
        [Tooltip("When true, pickups merge into an existing slot of the same type before using an empty one.")]
        public bool Stackable;
        [Tooltip("Max items per inventory slot. Only matters when Stackable is true.")]
        [Min(1)] public int MaxStack = 1;

        [Header("Throw Settings")]
        [Tooltip("Launch speed (m/s) when use is released immediately.")]
        [Min(0f)] public float MinThrowSpeed = 3f;
        [Tooltip("Launch speed (m/s) when use is released at full charge.")]
        [FormerlySerializedAs("ThrowSpeed"), Min(0f)] public float MaxThrowSpeed = 14f;
        [Tooltip("Gameplay seconds to reach full throw speed. Full charge waits for release.")]
        [Min(0.01f)] public float ThrowChargeTime = 1f;

        [Header("Player Impacts")]
        [Tooltip("Minimum incoming contact speed (m/s) that shoves a player.")]
        [Min(0f)] public float MinimumImpactSpeed = 1f;
        [Tooltip("Scales the shove received by a player. Requires a non-trigger sphere collider. Zero disables shove and recovery.")]
        [InspectorName("Impulse Multiplier"), Min(0f)] public float ImpulseMultiplier = 1f;

        [Header("Physics")]
        [Tooltip("Launch speed (m/s) when the player drops (not throws) the item.")]
        [Min(0f)] public float DropSpeed = 1.5f;
        [Tooltip("How much of the player's velocity transfers to the item on release. 0 = none, 1 = full.")]
        [Min(0f)] public float VelocityInheritance = 1f;
        [Tooltip("Rigidbody mass. Affects how much force other objects exert on this item and vice versa.")]
        [Min(0.01f)] public float Mass = 1f;
        [Tooltip("Rigidbody linear drag. Higher = item slows down faster in the air and on the ground.")]
        [Min(0f)] public float LinearDamping = 0.05f;
        [Tooltip("Rigidbody angular drag. Higher = item stops spinning sooner after release.")]
        [Min(0f)] public float AngularDamping = 0.1f;
        [Tooltip("Angular velocity (rad/s per axis) applied on release. Controls the tumble on throw/drop.")]
        public Vector3 InitialSpin = new(3f, 1f, 2f);
        [Tooltip("Applied to every collider on the world item. Controls bounciness and friction.")]
        public PhysicsMaterial PhysicsMaterial;
        [Tooltip("ContinuousDynamic prevents fast items tunneling through walls; Discrete is cheaper but can miss.")]
        public CollisionDetectionMode CollisionDetection = CollisionDetectionMode.ContinuousDynamic;
        [Tooltip("Caps the item's max linear velocity. Prevents runaway speeds from applied forces.")]
        [Min(0f)] public float MaxSpeed = 50f;
        [Tooltip("When true, uses the per-item SleepThreshold instead of the global Physics.sleepThreshold.")]
        public bool OverrideSleepThreshold;
        [Tooltip("Energy below which the rigidbody sleeps (only when OverrideSleepThreshold is on). Lower = simulates longer before resting.")]
        [Min(0f)] public float SleepThreshold = 0.005f;

        private void OnValidate()
        {
            MinThrowSpeed = Mathf.Max(0f, MinThrowSpeed);
            MaxThrowSpeed = Mathf.Max(MinThrowSpeed, MaxThrowSpeed);
            ThrowChargeTime = Mathf.Max(0.01f, ThrowChargeTime);
            MinimumImpactSpeed = Mathf.Max(0f, MinimumImpactSpeed);
            ImpulseMultiplier = Mathf.Max(0f, ImpulseMultiplier);
        }
    }
}
