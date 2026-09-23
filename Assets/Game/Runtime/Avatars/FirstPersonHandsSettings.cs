using System;
using UnityEngine;

namespace TwoBirds
{
    [CreateAssetMenu(menuName = "Two Birds/First Person Hands Settings")]
    public sealed class FirstPersonHandsSettings : ScriptableObject
    {
        public event Action ContentChanged;
        public void NotifyContentChanged() => ContentChanged?.Invoke();
        private void OnValidate() => NotifyContentChanged();

        [Serializable]
        public sealed class Hand
        {
            [Tooltip("Higher value on X moves the resting hand right; higher Y raises it; higher Z moves it forward. Relative to the shoulder, in arm lengths. Limited by arm reach.")]
            public Vector3 RestPosition;
            [Tooltip("Higher value on X pitches the resting fingers downward; higher Y turns them right; higher Z rolls the palm's upward axis left. Directions describe rotation from neutral, in degrees.")]
            public Vector3 RestEuler;
            [Tooltip("Higher value on X moves the ascending-jump hand right; higher Y raises it; higher Z moves it forward. Relative to the shoulder, in arm lengths. Limited by arm reach.")]
            public Vector3 RisePosition;
            [Tooltip("Higher value on X pitches the ascending-jump fingers downward; higher Y turns them right; higher Z rolls the palm's upward axis left. Directions describe rotation from neutral, in degrees.")]
            public Vector3 RiseEuler;
            [Tooltip("Higher value on X moves the falling hand right; higher Y raises it; higher Z moves it forward. Relative to the shoulder, in arm lengths. Faster or longer falls blend further toward this pose. Limited by arm reach.")]
            public Vector3 FallPosition;
            [Tooltip("Higher value on X pitches the falling fingers downward; higher Y turns them right; higher Z rolls the palm's upward axis left. Directions describe rotation from neutral, in degrees.")]
            public Vector3 FallEuler;
            public Hand(float side)
            {
                RestPosition = new(0.12f * side, -0.6f, 0.5f); RestEuler = new(0f, 0f, 15f * side);
                RisePosition = new(0.15f * side, -0.35f, 0.55f); RiseEuler = new(-15f, 0f, 20f * side);
                FallPosition = new(0.35f * side, 0.05f, 0.65f); FallEuler = new(-25f, 0f, 30f * side);
            }
        }
        [Tooltip("Higher value on X shifts both camera-relative shoulders right; higher Y raises them; higher Z moves them forward. Units are metres. This shifts the arms and held-item targets together.")]
        public Vector3 ShoulderOffset = new(0f, -0.12f, 0.04f);
        [Tooltip("Higher position values move the free left hand right (X), up (Y), or forward (Z). Expand to tune its resting, ascending-jump, and falling poses.")]
        public Hand Left = new(-1f);
        [Tooltip("Higher position values move the free right hand right (X), up (Y), or forward (Z). These poses apply when an item action or contact is not controlling the hand.")]
        public Hand Right = new(1f);
        [Min(0.01f), Tooltip("Higher value makes free-hand positions and rotations settle more slowly and restores free-hand control more gradually after an item action or contact. Units are seconds.")]
        public float BlendTime = 0.12f;
        [Min(0.01f), Tooltip("Higher value makes the shoulders transition more slowly between camera-relative placement and the seated steering-wheel placement. Units are seconds.")]
        public float PlacementBlendTime = 0.18f;
        [Tooltip("Higher value increases the free hands' up-and-down movement while walking or running. Amplitude is measured in arm lengths; zero removes this bounce.")]
        public float Bounce = 0.025f;
        [Tooltip("Higher value increases the free hands' side-to-side and forward/back flailing during descent. Amplitude is measured in arm lengths; zero removes the oscillation while retaining the falling pose.")]
        public float FallStrength = 0.10f;
        [Tooltip("Higher value makes the free hands dip farther downward on landing. Amplitude is measured in arm lengths and scales with impact severity; zero removes the dip.")]
        public float LandingStrength = 0.12f;
        [Min(0.01f), Tooltip("Higher value stretches the landing dip and recovery over more time, making the response slower. Units are seconds; this controls duration, not dip depth.")]
        public float LandingDuration = 0.3f;
    }
}
