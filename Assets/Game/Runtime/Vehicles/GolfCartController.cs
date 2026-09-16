using System.Collections.Generic;
using FishNet.Component.Transforming;
using UnityEngine;

namespace TwoBirds
{
    [RequireComponent(typeof(Rigidbody))]
    public sealed class GolfCartController : MonoBehaviour
    {
        private const float ParkingSpeed = 0.25f, ParkingAngularSpeed = 0.5f;
        [SerializeField] private GolfCartSettings settings;
        [SerializeField] private Transform[] suspension;
        [SerializeField] private BoxCollider[] chassis;
        private readonly Collider[] overlaps = new Collider[64];
        private readonly List<Vector2> recoveryOffsets = new();
        private readonly float[] compression = new float[4];
        private readonly RaycastHit[] wheelHits = new RaycastHit[4];
        private readonly Vector3[] wheelOrigins = new Vector3[4];
        private GolfCartNetwork network;
        private Vector2 drive;
        private bool handbrake, driven, parkingBrake;
        private float rearGrip = 1f, steering, collisionSeverity, rolloverTime, settledTime, stuckTime, landingGrace, clearTime;
        private bool rolloverReported, previouslySupported;
        private Vector3 stuckOrigin, preVelocity, preAngularVelocity, preCenter;
        private Vector3 prePosition;
        private Quaternion preRotation;
        private float heading;
        private int supportMask, clearanceMask;
        public Rigidbody Body { get; private set; }
        public GolfCartSettings Settings => settings;
        internal BoxCollider[] Chassis => chassis;
        public float Heading => heading;

        private void Awake()
        {
            Body = GetComponent<Rigidbody>();
            network = GetComponent<GolfCartNetwork>();
            Body.mass = settings.Mass;
            Body.centerOfMass = settings.CenterOfMass;
            Body.excludeLayers |= LayerMask.GetMask("Player", "PlayerItemHitbox", "CartSeat");
            supportMask = Physics.DefaultRaycastLayers & ~LayerMask.GetMask("GolfCart", "CartSeat", "Player", "PlayerItemHitbox", "ItemHeld", "ItemWorld", "BirdBody", "BirdQuery");
            clearanceMask = Physics.DefaultRaycastLayers & ~LayerMask.GetMask("CartSeat", "ItemHeld", "PlayerItemHitbox", "BirdBody", "BirdQuery");
            heading = transform.eulerAngles.y;
            int steps = Mathf.FloorToInt(settings.RecoveryRadius / settings.RecoverySpacing);
            for (int x = -steps; x <= steps; x++)
                for (int z = -steps; z <= steps; z++)
                {
                    Vector2 offset = new Vector2(x, z) * settings.RecoverySpacing;
                    if (offset.magnitude <= settings.RecoveryRadius) recoveryOffsets.Add(offset);
                }
            recoveryOffsets.Sort((a, b) => a.sqrMagnitude != b.sqrMagnitude ? a.sqrMagnitude.CompareTo(b.sqrMagnitude) :
                a.x != b.x ? a.x.CompareTo(b.x) : a.y.CompareTo(b.y));
        }

        internal void SetInput(Vector2 value, bool brake) { drive = value; handbrake = brake; }
        internal void ClearInput() { drive = default; handbrake = false; }
        internal void ResetMotion(bool isDriven, bool parked)
        {
            ClearInput();
            driven = isDriven;
            parkingBrake = parked;
            Body.linearDamping = driven ? settings.DrivenLinearDamping : settings.UnmannedLinearDamping;
            collisionSeverity = rolloverTime = settledTime = stuckTime = clearTime = 0f;
            landingGrace = settings.LandingGraceSeconds;
            rolloverReported = previouslySupported = false;
            rearGrip = 1f;
            stuckOrigin = Body.position;
            System.Array.Clear(compression, 0, compression.Length);
        }

        internal void BeforePhysics(float delta)
        {
            collisionSeverity = 0f;
            Vector3 planar = Vector3.ProjectOnPlane(Body.rotation * Vector3.forward, Vector3.up);
            if (planar.sqrMagnitude > 0.01f) heading = Quaternion.LookRotation(planar).eulerAngles.y;
            Vector3 forward = Body.rotation * Vector3.forward;
            Vector3 up = Body.rotation * Vector3.up;
            float speed = Vector3.Dot(Body.linearVelocity, forward);
            steering = drive.x * Mathf.Lerp(settings.SteeringAngle, settings.FastSteeringAngle,
                Mathf.Abs(speed) / settings.MaximumSpeed);
            int supported = 0;
            for (int i = 0; i < 4; i++)
            {
                wheelOrigins[i] = Body.position + Body.rotation * suspension[i].localPosition;
                compression[i] = 0f;
                if (!Physics.Raycast(wheelOrigins[i], -up, out wheelHits[i], settings.SuspensionTravel + settings.WheelRadius,
                    supportMask, QueryTriggerInteraction.Ignore)) continue;
                supported++;
                compression[i] = Mathf.Clamp01((settings.SuspensionTravel + settings.WheelRadius - wheelHits[i].distance) / settings.SuspensionTravel);
            }
            bool stable = supported >= 3;
            bool canPark = stable && Vector3.Dot(up, Vector3.up) > 0.5f &&
                Body.linearVelocity.sqrMagnitude < ParkingSpeed * ParkingSpeed &&
                Body.angularVelocity.sqrMagnitude < ParkingAngularSpeed * ParkingAngularSpeed;
            if (parkingBrake && driven && Mathf.Abs(drive.y) > 0.1f)
            {
                parkingBrake = false;
                Body.WakeUp();
            }
            if (!driven && canPark) parkingBrake = true;
            bool holding = parkingBrake && canPark;
            if (holding)
            {
                Body.linearVelocity = Body.angularVelocity = Vector3.zero;
                Body.Sleep();
            }
            else if (parkingBrake) Body.WakeUp();
            rearGrip = parkingBrake ? 1f : handbrake ? settings.HandbrakeGrip : Mathf.MoveTowards(rearGrip, 1f, delta / settings.GripRecoverySeconds);
            bool braking = drive.y * speed < 0f && Mathf.Abs(speed) > settings.ReverseDeadband;
            for (int i = 0; i < 4 && !holding; i++)
            {
                var hit = wheelHits[i];
                if (hit.collider == null) continue;
                Vector3 origin = wheelOrigins[i];
                float depth = settings.SuspensionTravel + settings.WheelRadius - hit.distance;
                Vector3 velocity = Body.GetPointVelocity(origin);
                float load = Mathf.Clamp(depth * settings.Spring - Vector3.Dot(velocity, up) * settings.Damper, 0f, settings.MaximumLoad);
                Body.AddForceAtPosition(up * load, origin);
                Vector3 wheelForward = Quaternion.AngleAxis(i < 2 ? steering : 0f, up) * forward;
                wheelForward = Vector3.ProjectOnPlane(wheelForward, hit.normal).normalized;
                Vector3 right = Vector3.Cross(hit.normal, wheelForward);
                float along = Vector3.Dot(velocity, wheelForward);
                float longitudinal = 0f;
                if (parkingBrake || braking) longitudinal = -Mathf.Sign(along) * Mathf.Min(settings.BrakeForce * (parkingBrake ? 1f : Mathf.Abs(drive.y)) / 4f,
                    Mathf.Abs(along) * Body.mass / (4f * delta));
                else if (Mathf.Abs(speed) < (drive.y < 0f ? settings.ReverseSpeed : settings.MaximumSpeed) || drive.y * speed < 0f)
                    longitudinal = drive.y * settings.DriveForce / 4f;
                if (handbrake && !parkingBrake && i >= 2)
                    longitudinal -= Mathf.Sign(along) * Mathf.Min(settings.HandbrakeForce / 2f, Mathf.Abs(along) * Body.mass / (4f * delta));
                float grip = settings.TireGrip * (i < 2 ? 1f : rearGrip);
                float lateral = -Vector3.Dot(velocity, right) * Body.mass * settings.LateralResponse / 4f;
                Vector3 tireForce = Vector3.ClampMagnitude(wheelForward * longitudinal + right * lateral, load * grip);
                Body.AddForceAtPosition(tireForce, hit.point);
            }
            if (stable && !previouslySupported) landingGrace = settings.LandingGraceSeconds;
            previouslySupported = stable;
            landingGrace = Mathf.Max(0f, landingGrace - delta);
            float progress = Vector3.ProjectOnPlane(Body.position - stuckOrigin, Vector3.up).magnitude;
            if (progress >= settings.StuckProgress || !stable || handbrake || Mathf.Abs(drive.y) < 0.5f || landingGrace > 0f)
            {
                stuckTime = 0f;
                stuckOrigin = Body.position;
            }
            else stuckTime += delta;
            preVelocity = Body.linearVelocity;
            preAngularVelocity = Body.angularVelocity;
            preCenter = Body.worldCenterOfMass;
            prePosition = Body.position;
            preRotation = Body.rotation;
        }

        internal void ReassessIncident() => rolloverReported = false;

        internal void AfterPhysics(float delta)
        {
            bool overturned = Vector3.Angle(Body.rotation * Vector3.up, Vector3.up) > settings.RolloverAngle;
            rolloverTime = overturned ? rolloverTime + delta : 0f;
            bool settled = Body.linearVelocity.magnitude < settings.SettledSpeed && Body.angularVelocity.magnitude < settings.SettledAngularSpeed;
            settledTime = overturned && settled ? settledTime + delta : 0f;
            if (!overturned) rolloverReported = false;
            CartRecovery recovery = settledTime >= settings.SettledSeconds ? CartRecovery.Flipped :
                stuckTime >= settings.StuckSeconds ? CartRecovery.Stuck : CartRecovery.None;
            bool eject = collisionSeverity >= settings.CrashVelocityChange || rolloverTime >= settings.RolloverSeconds && !rolloverReported;
            if (eject || recovery != CartRecovery.None && recovery != network.Recovery)
            {
                rolloverReported |= rolloverTime >= settings.RolloverSeconds;
                network.ReportIncident(recovery);
            }
            bool progressed = Vector3.ProjectOnPlane(Body.position - network.RecoveryOrigin, Vector3.up).magnitude > settings.StuckProgress;
            clearTime = !overturned && previouslySupported && progressed ? clearTime + delta : 0f;
            if (network.Recovery != CartRecovery.None && clearTime >= 1f) network.ClearRecovery();
        }

        private void OnCollisionEnter(Collision collision) => AccumulateCollision(collision);
        internal Vector3 PreImpactVelocity(Vector3 localPoint) =>
            preVelocity + Vector3.Cross(preAngularVelocity, prePosition + preRotation * localPoint - preCenter);
        private void OnCollisionStay(Collision collision) => AccumulateCollision(collision);
        private void AccumulateCollision(Collision collision)
        {
            if (!network.Simulating || network.PredictionManager.IsReconciling) return;
            // Sum contact-pair impulse magnitudes within this physics step.
            collisionSeverity += collision.impulse.magnitude / Body.mass;
        }

        internal NetworkTransform.MotionFrame Capture(uint epoch, uint tick) => new()
        {
            Epoch = epoch, Tick = tick, Position = Body.position, Rotation = Body.rotation,
            Velocity = Body.linearVelocity, AngularVelocity = Body.angularVelocity,
            Steering = (sbyte)Mathf.RoundToInt(steering / settings.SteeringAngle * 127f), Handbrake = handbrake, ParkingBrake = parkingBrake,
            FrontLeft = Pack(compression[0]), FrontRight = Pack(compression[1]), RearLeft = Pack(compression[2]), RearRight = Pack(compression[3])
        };
        private static byte Pack(float value) => (byte)Mathf.RoundToInt(value * 255f);

        internal bool TryRecovery(out Vector3 position, out Quaternion rotation)
        {
            position = Body.position;
            rotation = Quaternion.Euler(0f, heading, 0f);
            foreach (Vector2 offset in recoveryOffsets)
            {
                Vector3 candidate = Body.position + new Vector3(offset.x, 0f, offset.y);
                float highest = float.NegativeInfinity, lowest = float.PositiveInfinity;
                bool supported = true;
                for (int i = 0; i < 4; i++)
                {
                    Vector3 wheel = candidate + rotation * suspension[i].localPosition;
                    Vector3 ray = wheel + Vector3.up * settings.RecoveryHeightChange;
                    if (!Physics.Raycast(ray, Vector3.down, out var hit, settings.RecoveryHeightChange * 2f + settings.SuspensionTravel,
                        supportMask, QueryTriggerInteraction.Ignore) || Vector3.Angle(hit.normal, Vector3.up) > settings.SupportSlope ||
                        hit.point.y > Body.position.y + 0.15f) { supported = false; break; }
                    float rootHeight = hit.point.y + settings.WheelRadius + settings.SuspensionTravel * 0.65f - suspension[i].localPosition.y;
                    highest = Mathf.Max(highest, rootHeight);
                    lowest = Mathf.Min(lowest, rootHeight);
                }
                if (!supported || highest - lowest > settings.SuspensionTravel * 0.65f ||
                    Mathf.Abs(highest - Body.position.y) > settings.RecoveryHeightChange) continue;
                candidate.y = highest;
                if (!ChassisClear(candidate, rotation)) continue;
                position = candidate;
                return true;
            }
            return false;
        }

        private bool ChassisClear(Vector3 position, Quaternion rotation)
        {
            foreach (var box in chassis)
            {
                Vector3 center = position + rotation * transform.InverseTransformPoint(box.transform.TransformPoint(box.center));
                Quaternion orientation = rotation * Quaternion.Inverse(transform.rotation) * box.transform.rotation;
                Vector3 half = Vector3.Scale(box.size, box.transform.lossyScale) * 0.5f;
                int count = Physics.OverlapBoxNonAlloc(center, half + Vector3.one * 0.03f, overlaps, orientation, clearanceMask, QueryTriggerInteraction.Ignore);
                if (count == overlaps.Length) return false;
                for (int i = 0; i < count; i++)
                    if (overlaps[i].attachedRigidbody != Body) return false;
            }
            return true;
        }
    }
}
