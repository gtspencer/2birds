using UnityEngine;

namespace TwoBirds
{
    [CreateAssetMenu(menuName = "Two Birds/Splat Definition")]
    public sealed class SplatDefinition : ScriptableObject
    {
        public Material SplatMaterial;
        [Min(0.001f)] public float SplatSize = 0.5f;
        [Min(0f)] public float PopDuration = 0.35f;
        [Min(0f)] public float LifetimeBeforeShrinking = 30f;
        [Min(0f)] public float ShrinkDuration = 3f;

        private void OnValidate()
        {
            SplatSize = Mathf.Max(0.001f, SplatSize);
            PopDuration = Mathf.Max(0f, PopDuration);
            LifetimeBeforeShrinking = Mathf.Max(0f, LifetimeBeforeShrinking);
            ShrinkDuration = Mathf.Max(0f, ShrinkDuration);
        }
    }
}
