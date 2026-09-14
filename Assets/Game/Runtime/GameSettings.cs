using UnityEngine;

namespace TwoBirds
{
    [CreateAssetMenu(menuName = "Two Birds/Game Settings")]
    public sealed class GameSettings : ScriptableObject
    {
        [Min(0)] public float WalkSpeed = 5f;
        [Min(0)] public float GroundAcceleration = 25f;
        [Min(0)] public float Braking = 25f;
        [Min(0)] public float AirAcceleration = 4f;
        [Min(0)] public float JumpSpeed = 4f;
        public float FallBoundary = -15f;
        public LayerMask GroundLayers = 1 << 7;
        [Min(1)] public float ConnectTimeout = 10f;
        [Min(1)] public float LoadTimeout = 30f;
    }
}
