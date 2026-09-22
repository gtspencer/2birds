using UnityEngine;

namespace TwoBirds
{
    [CreateAssetMenu(menuName = "Two Birds/Slingshot Definition")]
    public sealed class SlingshotDefinition : ItemDefinition
    {
        public override bool CanBeIngredient => false;
        [Min(0)] public int PebbleDamage = 10;
        [Min(0f)] public float RecoverySeconds = 0.5f;
        [Range(0f, 1f)] public float ReboundRetention = 0.75f;
        public PebbleProjectile PebblePrefab;
        public ParticleSystem DirtPrefab;

        protected override void OnValidate()
        {
            Stackable = false;
            MaxStack = 1;
            base.OnValidate();
        }
    }
}
