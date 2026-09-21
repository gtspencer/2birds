using UnityEngine;

namespace TwoBirds
{
    public sealed class AvatarHandContact : MonoBehaviour
    {
        public AvatarIKGoal Hand;
        public AnimationClip Fingers;
        [Range(0.1f, 0.98f)] public float MaximumReach = 0.98f;
        [Min(0f)] public float BlendTime = 0.15f;
    }
}
