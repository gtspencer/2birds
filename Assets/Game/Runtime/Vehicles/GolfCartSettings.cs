using UnityEngine;

namespace TwoBirds
{
    [CreateAssetMenu(menuName = "Two Birds/Golf Cart Settings")]
    public sealed class GolfCartSettings : ScriptableObject
    {
        [Header("Body and suspension")]
        [Tooltip("Rigidbody mass in kg. Increasing this makes the cart harder to push around and changes how collisions transfer energy. Also affects braking feel since braking force is capped relative to mass.")]
        public float Mass = 450f;
        [Tooltip("Local-space center of mass. Raising Y makes the cart top-heavy and easier to roll over. Moving Z forward shifts weight to the front wheels, improving front grip but making the rear looser in turns.")]
        public Vector3 CenterOfMass = new(0f, 0.45f, 0.39f);
        [Tooltip("Linear drag while a player is driving. Kept very low so the cart coasts freely. Increasing this makes the cart decelerate faster when not accelerating.")]
        [Min(0f)] public float DrivenLinearDamping = 0.02f;
        [Tooltip("Linear drag when no player is in the driver seat. Higher than driven damping so unmanned carts slow down and stop on their own. Increasing this makes abandoned carts stop sooner.")]
        [Min(0f)] public float UnmannedLinearDamping = 0.5f;
        [Tooltip("Radius of each wheel in meters. Affects ground-contact raycasts and how fast the visual wheels spin. Increasing this raises the cart body and changes the spin rate of the wheel meshes.")]
        public float WheelRadius = 0.3f;
        [Tooltip("Maximum suspension travel in meters. Also extends each wheel's ground-contact ray. Increasing this allows more travel while keeping the same spring stiffness.")]
        public float SuspensionTravel = 0.35f;
        [Tooltip("Spring stiffness per wheel (N/m). Higher values make the suspension stiffer, reducing body roll in turns and bottoming out less, but the ride becomes harsher over bumps.")]
        public float Spring = 9000f;
        [Tooltip("Damping per wheel (N·s/m). Controls how quickly suspension oscillations settle. Increasing this reduces bounciness and makes the cart feel more planted, but too much makes it stiff and jittery over rough ground.")]
        public float Damper = 1300f;
        [Tooltip("Maximum force (N) a single wheel's suspension can exert. Caps the spring to prevent extreme forces on hard landings. Increasing this lets the suspension handle bigger drops without bottoming out.")]
        public float MaximumLoad = 9000f;
        [Header("Driving")]
        [Tooltip("Total requested driving force (N), split across four wheels and limited by available tire grip. Increasing this improves acceleration until the grip limit is reached.")]
        public float DriveForce = 2400f;
        [Tooltip("Maximum braking force (N), split across four wheels. Increasing this strengthens braking within the available tire grip and each wheel's per-step stopping limit.")]
        public float BrakeForce = 4200f;
        [Tooltip("Top forward speed in m/s. The engine stops adding force once this speed is reached. Also affects steering: at this speed, steering angle is reduced to FastSteeringAngle.")]
        public float MaximumSpeed = 15f;
        [Tooltip("Top reverse speed in m/s. The engine stops adding reverse force once this speed is reached. Increasing this lets the cart back up faster.")]
        public float ReverseSpeed = 6f;
        [Tooltip("Speed threshold (m/s) below which changing input direction switches between forward and reverse instead of braking. Increasing this makes direction changes feel more responsive at low speeds.")]
        public float ReverseDeadband = 0.25f;
        [Tooltip("Maximum front-wheel steering angle in degrees at low speed. Increasing this gives a tighter turning radius at slow speeds but can feel twitchy.")]
        public float SteeringAngle = 32f;
        [Tooltip("Maximum front-wheel steering angle in degrees at top speed. The steering angle blends from SteeringAngle to this value as speed increases. Lowering this makes high-speed steering more stable but less responsive.")]
        public float FastSteeringAngle = 12f;
        [Tooltip("Tire friction multiplier. Total tire force per wheel is clamped to suspension load times this value. Increasing this gives more grip, making the cart harder to slide. Decreasing it makes the cart drift and slide more easily.")]
        public float TireGrip = 1.3f;
        [Tooltip("How aggressively tires resist lateral sliding. Higher values make the cart snap back to its line more quickly, giving a responsive, go-kart feel. Lower values make the cart feel floaty and prone to sliding sideways in turns.")]
        public float LateralResponse = 8f;
        [Header("Handbrake")]
        [Tooltip("Rear wheel grip multiplier when the handbrake is held (0 = no grip, 1 = full grip). Lower values make the rear end slide out more during handbrake turns, creating stronger drifts.")]
        public float HandbrakeGrip = 0.2f;
        [Tooltip("Additional braking force (N), split between the rear wheels while the handbrake is held. Increasing this strengthens braking within the available rear grip and per-wheel force limits.")]
        public float HandbrakeForce = 5000f;
        [Tooltip("Time in seconds to recover from zero rear grip to full grip. After releasing the handbrake, recovery takes (1 - HandbrakeGrip) times this value. Increasing it restores grip more slowly.")]
        public float GripRecoverySeconds = 0.35f;
        [Header("Pedestrian impacts")]
        [Tooltip("Minimum relative closing speed (m/s) along the contact normal required to hit a pedestrian. Accounts for cart rotation and pedestrian velocity. Increasing this excludes slower contacts.")]
        public float MinimumHitSpeed = 1.5f;
        [Tooltip("Multiplier for the pedestrian's closing-speed-based velocity impulse, including lift. Increasing this launches hit players farther and higher.")]
        public float CollisionMultiplier = 1.2f;
        [Tooltip("Dimensionless upward-lift multiplier. Added vertical velocity is HitLift times closing speed times CollisionMultiplier. Increasing this launches hit players higher.")]
        public float HitLift = 0.3f;
        [Header("Ejection")]
        [Tooltip("Collision severity threshold (summed impulse / mass, in m/s) that triggers rider ejection. Lowering this makes riders eject from weaker crashes. Increasing it means only harder impacts throw riders out.")]
        public float CrashVelocityChange = 7f;
        [Tooltip("Outward speed (m/s) applied to ejected riders away from the cart's center. Increasing this flings riders farther outward on ejection.")]
        public float EjectionSpeed = 3f;
        [Tooltip("Upward speed (m/s) applied to ejected riders. Increasing this launches riders higher into the air when ejected.")]
        public float EjectionLift = 4f;
        [Tooltip("Angle in degrees from upright beyond which the cart is considered overturned. 100 degrees is 10 degrees past sideways; fully upside-down is 180. Lowering this detects rollovers earlier.")]
        public float RolloverAngle = 100f;
        [Tooltip("How long (seconds) the cart must stay past RolloverAngle to trigger rollover ejection. Increasing this gives it more time to self-right. Crashes or settled-flip recovery can eject riders sooner.")]
        public float RolloverSeconds = 0.6f;
        [Tooltip("Linear speed (m/s) below which an overturned cart is considered settled. Used together with SettledAngularSpeed and SettledSeconds to detect when a flipped cart has come to rest.")]
        public float SettledSpeed = 0.5f;
        [Tooltip("Angular speed (rad/s) below which an overturned cart is considered settled. Works with SettledSpeed and SettledSeconds to detect when a flipped cart has stopped moving.")]
        public float SettledAngularSpeed = 0.5f;
        [Tooltip("How long (seconds) an overturned cart must stay below SettledSpeed and SettledAngularSpeed before it enters the recovery state. Increasing this makes the system wait longer before offering recovery.")]
        public float SettledSeconds = 0.5f;
        [Header("Recovery")]
        [Tooltip("How long (seconds) the cart must be driven without making progress before it is flagged as stuck. Increasing this gives the player more time to free themselves before the stuck state triggers.")]
        public float StuckSeconds = 2f;
        [Tooltip("Distance (meters) the cart must travel horizontally to reset the stuck timer. If the cart hasn't moved this far, it is considered stuck. Increasing this requires more movement to count as progress.")]
        public float StuckProgress = 0.2f;
        [Tooltip("Grace period (seconds) after landing or a motion reset. The stuck timer stays reset during this period. Increasing this delays stuck detection after landings and driving-authority changes.")]
        public float LandingGraceSeconds = 0.75f;
        [Tooltip("Search radius (meters) around the cart's current position when looking for a valid recovery spot. Increasing this searches a wider area, making it more likely to find a spot but potentially placing the cart farther away.")]
        public float RecoveryRadius = 4f;
        [Tooltip("Grid spacing (meters) between candidate recovery positions within the search radius. Decreasing this tests more positions for a tighter fit, but costs more raycasts.")]
        public float RecoverySpacing = 0.5f;
        [Tooltip("Maximum vertical change (meters) in the cart's root position during recovery. Also controls ground-search ray reach. Ground more than 0.15 m above the current root position is still rejected.")]
        public float RecoveryHeightChange = 2.5f;
        [Tooltip("Maximum ground slope (degrees) considered valid for a recovery position. Surfaces steeper than this are rejected. Increasing this allows the cart to recover on steeper hillsides.")]
        public float SupportSlope = 30f;
    }
}
