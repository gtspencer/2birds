using UnityEngine;

namespace TwoBirds
{
    [CreateAssetMenu(menuName = "Two Birds/Emote Definition")]
    public sealed class EmoteDefinition : ScriptableObject
    {
        public const float BlendDuration = 0.2f;
        public AnimationClip Clip;
        public string DisplayName;
        public bool Loop;
        [Min(0.01f)] public float Speed = 1f;
        public Sprite Icon;
    }
}
