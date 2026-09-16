using System;
using UnityEngine;

namespace TwoBirds
{
    public enum BirdBehavior : byte { Bush, Soaring, Water, Ground }
    public enum BirdActivity : byte { Idle, Takeoff, Flight, Soar, Landing, GroundMove, Swim }
    public enum BirdInterrupt : byte { Calm, WaitingToFlee, Fleeing }
    public enum BirdHabitatKind : byte { Air, Ground, Water }
    public enum BirdPerchKind : byte { Tree, Ground }
    [Flags] public enum BirdCapabilities : byte { None = 0, Flight = 1, Tree = 2, Ground = 4, Swim = 8, ShortFlight = 16 }

    [Serializable]
    public struct BirdSounds
    {
        public SoundEffect Ambient, Scare, Wings, Hit;
    }

    [Serializable]
    public struct BirdAnimations
    {
        public string Idle, Takeoff, Flight, Soar, Landing, GroundMove, Swim;
        public string State(BirdActivity activity) => activity switch
        {
            BirdActivity.Takeoff => Takeoff, BirdActivity.Flight => Flight, BirdActivity.Soar => Soar,
            BirdActivity.Landing => Landing, BirdActivity.GroundMove => GroundMove, BirdActivity.Swim => Swim, _ => Idle
        };
    }

    [CreateAssetMenu(menuName = "Two Birds/Birds/Species")]
    public sealed class BirdSpecies : ScriptableObject
    {
        [Min(1)] public ushort Id;
        public BirdBehavior Behavior;
        public BirdCapabilities Capabilities = BirdCapabilities.Flight | BirdCapabilities.Tree | BirdCapabilities.Ground;
        public BirdView ViewPrefab;
        public BirdBody BodyPrefab;
        public Vector3 ModelScale = Vector3.one;
        public Vector3 ModelOffset;
        [Min(0.01f)] public float Radius = 0.15f;
        [Min(0f)] public float LandingOffset = 0.16f;
        [Min(0.01f)] public float FlightSpeed = 6f, GroundSpeed = 1.5f, SwimSpeed = 1f;
        [Min(0f)] public float FlightHeight = 2f, ShortFlightDistance = 4f;
        public Vector2 IdleSeconds = new(3f, 8f);
        public Vector2 TravelSeconds = new(4f, 8f);
        [Min(0f)] public float ScareRadius = 5f, ScareDelay = 0.25f;
        [Tooltip("Zero uses the shared threshold.")] [Min(0f)] public float LethalSpeed;
        [Min(0)] public int Reward = 7;
        public BirdAnimations Animations;
        public BirdSounds Sounds;
        public bool Supports(BirdCapabilities capability) => (Capabilities & capability) != 0;
    }
}
