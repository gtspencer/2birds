using UnityEngine;

namespace TwoBirds
{
    [CreateAssetMenu(menuName = "Two Birds/Golf Cart Settings")]
    public sealed class GolfCartSettings : ScriptableObject
    {
        [Header("Body and suspension")]
        public float Mass = 450f;
        public Vector3 CenterOfMass = new(0f, 0.45f, 0.39f);
        public float WheelRadius = 0.3f;
        public float SuspensionTravel = 0.35f;
        [Tooltip("Spring stiffness per wheel (N/m).")]
        public float Spring = 9000f;
        [Tooltip("Damping per wheel (N s/m).")]
        public float Damper = 1300f;
        public float MaximumLoad = 9000f;
        [Header("Driving")]
        public float DriveForce = 2400f;
        public float BrakeForce = 4200f;
        public float MaximumSpeed = 15f;
        public float ReverseSpeed = 6f;
        public float ReverseDeadband = 0.25f;
        public float SteeringAngle = 32f;
        public float FastSteeringAngle = 12f;
        public float TireGrip = 1.3f;
        public float LateralResponse = 8f;
        [Header("Handbrake")]
        public float HandbrakeGrip = 0.2f;
        public float HandbrakeForce = 5000f;
        public float GripRecoverySeconds = 0.35f;
        [Header("Pedestrian impacts (m/s)")]
        public float MinimumHitSpeed = 1.5f;
        public float CollisionMultiplier = 1.2f;
        public float HitLift = 0.3f;
        [Header("Ejection")]
        [Tooltip("Summed collision impulse divided by cart mass (m/s).")]
        public float CrashVelocityChange = 7f;
        public float EjectionSpeed = 3f;
        public float EjectionLift = 4f;
        public float RolloverAngle = 100f;
        public float RolloverSeconds = 0.6f;
        public float SettledSpeed = 0.5f;
        public float SettledAngularSpeed = 0.5f;
        public float SettledSeconds = 0.5f;
        [Header("Recovery")]
        public float StuckSeconds = 2f;
        public float StuckProgress = 0.2f;
        public float LandingGraceSeconds = 0.75f;
        public float RecoveryRadius = 4f;
        public float RecoverySpacing = 0.5f;
        public float RecoveryHeightChange = 2.5f;
        public float SupportSlope = 30f;
    }
}
