using UnityEngine;

namespace TwoBirds
{
    [CreateAssetMenu(menuName = "Two Birds/Game Settings")]
    public sealed class GameSettings : ScriptableObject
    {
        [Min(0)] public float WalkSpeed = 5f;
        [Min(0)] public float SprintSpeed = 8f;
        [Min(0)] public float MaximumStamina = 5f;
        [Min(0)] public float StaminaDrainRate = 1f;
        [Min(0)] public float StaminaRecoveryRate = 1.5f;
        [Min(0)] public float StaminaRecoveryDelay = 1f;
        [Min(0)] public float GroundAcceleration = 25f;
        [Min(0), Tooltip("Acceleration into a touching cart (m/s?).")] public float CartPushAcceleration = 3f;
        [Min(0)] public float Braking = 25f;
        [Min(0)] public float AirAcceleration = 4f;
        [Min(0)] public float JumpSpeed = 4f;
        public Vector3 CarryOffset = new(0f, -0.1f, 0.7f);
        [Min(0.01f)] public float PlayerThrowChargeTime = 1f;
        [Min(0)] public float PlayerThrowMinSpeed = 3f;
        [Min(0)] public float PlayerThrowMaxSpeed = 15f;
        [Min(0)] public float PlayerThrowVelocityInheritance = 0.5f;
        [Min(0)] public float PlayerThrowRecoveryMin = 0.2f;
        [Min(0)] public float PlayerThrowRecoveryMax = 1.5f;
        [Min(0)] public float CarryImmunityDuration = 2f;
        public float FallBoundary = -15f;
        public LayerMask GroundLayers = 1 << 7;
        [Min(1)] public float ConnectTimeout = 10f;
        [Min(1)] public float LoadTimeout = 30f;
    }
}
