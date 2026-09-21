using UnityEngine;

namespace TwoBirds
{
    public enum PotionEffect : byte { Health, Bouncy }
    public enum PotionApplication : byte { Pulse, Zone }

    [CreateAssetMenu(menuName = "Two Birds/Potion Definition")]
    public sealed class PotionDefinition : ItemDefinition
    {
        public PotionEffect Effect;
        public PotionApplication Application = PotionApplication.Zone;
        [Min(0.01f)] public float Radius = 3f;
        [Min(0.01f)] public float ZoneLifetime = 8f;
        [Min(0.01f)] public float BuffDuration = 10f;
        [Min(0f)] public float HealthStrength = 5f;
        [Min(0f)] public float InitialMultiplier = 2f;
        [Range(0f, 0.99f)] public float ReboundRetention = 0.5f;
        [Min(0.01f)] public float MinimumMultiplier = 0.5f;
        public Color Color = Color.red;
        public GameObject ImpactVfx, CloudVfx;

        public PotionDefinition()
        {
            Stackable = true;
            MaxStack = 5;
            DontPushPlayer = true;
        }
    }
}
