using UnityEngine;

namespace TwoBirds
{
    [CreateAssetMenu(menuName = "Two Birds/Birds/Settings")]
    public sealed class BirdSettings : ScriptableObject
    {
        [Min(0.01f)] public float LethalSpeed = 4f;
        [Min(0)] public int MultiKillBonus = 5;
        [Min(0f)] public float HornRadius = 20f;
        [Min(0.1f)] public float BodyLifetime = 8f, BodyShrinkSeconds = 1f;
        [Min(1)] public int BodyLimit = 32;
        [Min(1f)] public float ViewDistance = 150f, AnimationDistance = 40f, AudioDistance = 25f;
        [Min(1)] public int AnimatedLimit = 60, SoundLimit = 12;
        [Min(0.1f)] public float RetrySeconds = 2f;
        [Range(0f, 80f)] public float GroundSlope = 35f;
        [Min(0.01f)] public float GroundStep = 0.3f;
        public LayerMask SolidMask = 1;
        public LayerMask GroundMask = 1;
        public BirdAnimations Animations;
        public BirdSounds Sounds;
        public ItemDefinition[] Rocks = System.Array.Empty<ItemDefinition>();
    }
}
