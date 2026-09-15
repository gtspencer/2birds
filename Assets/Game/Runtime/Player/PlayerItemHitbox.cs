using UnityEngine;

namespace TwoBirds
{
    [RequireComponent(typeof(Rigidbody), typeof(CapsuleCollider))]
    public sealed class PlayerItemHitbox : MonoBehaviour
    {
        private const float MaximumItemVelocityChange = 40f;
        private Rigidbody body;
        private Transform graphics;
        private Vector3 capsuleCenter;
        private float capsuleRadius;
        private float capsuleHalfSegment;
        private double batchX, batchY, batchZ;
        private uint batchGeneration;
        private uint batchId;
        private int batchCount;
        private readonly Vector3[] motionVelocities = new Vector3[128];
        private readonly uint[] motionTicks = new uint[128];
        private uint motionGeneration;
        public Collider Collider { get; private set; }
        public PlayerMotor Motor { get; private set; }
        internal Vector3 IncomingVelocity { get; private set; }
        internal Vector3 PresentedCenter => graphics.TransformPoint(capsuleCenter);
        internal Vector3 PresentedVelocity { get; private set; }
        internal Vector3 PresentationCorrection { get; private set; }
        internal bool Suspended { get; private set; }

        internal void SetSuspended(bool value)
        {
            Suspended = value;
            Collider.enabled = !value;
            ClearBatch();
            System.Array.Clear(motionTicks, 0, motionTicks.Length);
            PresentationCorrection = IncomingVelocity = PresentedVelocity = default;
            body.position = Motor.Body.position;
            body.rotation = Motor.Body.rotation;
        }

        private void Awake()
        {
            body = GetComponent<Rigidbody>();
            var capsule = GetComponent<CapsuleCollider>();
            Collider = capsule;
            Motor = GetComponentInParent<PlayerMotor>();
            graphics = Motor.GetComponent<PlayerPresentation>().Graphics;
            capsuleCenter = capsule.center;
            Vector3 scale = transform.lossyScale;
            capsuleRadius = capsule.radius * Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.z));
            capsuleHalfSegment = Mathf.Max(0f, capsule.height * Mathf.Abs(scale.y) * 0.5f - capsuleRadius);
        }

        private void OnEnable()
        {
            Motor.BeforeOwnerMove += FlushItemImpacts;
            Motor.PresentationCorrected += CorrectPresentation;
        }

        private void OnDisable()
        {
            Motor.BeforeOwnerMove -= FlushItemImpacts;
            Motor.PresentationCorrected -= CorrectPresentation;
            ClearBatch();
            PresentationCorrection = default;
        }

        private void CorrectPresentation(Vector3 correction) => PresentationCorrection += correction;
        internal void SamplePresentation() => PresentedVelocity = Motor.Body.linearVelocity;

        internal void QueueItemImpact(uint source, int releaser, uint release, string detector,
            Vector3 rockVelocity, Vector3 playerVelocity, Vector3 normal, Vector3 change)
        {
            if (Suspended || !Motor.IsOwner || Motor.PredictionManager.IsReconciling) return;
            if (batchCount > 0 && batchGeneration != Motor.ImpactGeneration) ClearBatch();
            if (batchCount == 0) { batchGeneration = Motor.ImpactGeneration; batchId++; }
            Motor.TraceImpact($"contact batch={batchId} source={source} releaser={releaser} release={release} detector={detector} rockVelocity={rockVelocity:F6} playerVelocity={playerVelocity:F6} normal={normal:F6} delta={change:F6}");
            if (!WorldItemRegistry.Finite(change))
            {
                Motor.TraceImpact($"invalid-contact batch={batchId} source={source}");
                Motor.DumpImpactTrace();
                return;
            }
            batchX += change.x;
            batchY += change.y;
            batchZ += change.z;
            batchCount++;
        }

        private void FlushItemImpacts()
        {
            if (Suspended) { ClearBatch(); return; }
            if (batchCount == 0) return;
            if (batchGeneration != Motor.ImpactGeneration) { ClearBatch(); return; }
            double magnitude = System.Math.Sqrt(batchX * batchX + batchY * batchY + batchZ * batchZ);
            double scale = magnitude > MaximumItemVelocityChange ? MaximumItemVelocityChange / magnitude : 1d;
            Vector3 change = new((float)(batchX * scale), (float)(batchY * scale), (float)(batchZ * scale));
            uint request = Motor.SubmitWorldImpact(change, 0.2f);
            Motor.TraceImpact($"batch id={batchId} count={batchCount} raw=({batchX:F6}, {batchY:F6}, {batchZ:F6}) capped={scale < 1d} delta={change:F6} request={request}");
            if (scale < 1d) Motor.DumpImpactTrace();
            ClearBatch();
        }

        private void ClearBatch()
        {
            batchX = batchY = batchZ = 0d;
            batchCount = 0;
        }

        internal void FollowMotor()
        {
            if (Suspended) return;
            IncomingVelocity = Motor.Body.linearVelocity;
            if (motionGeneration != Motor.ImpactGeneration)
            {
                System.Array.Clear(motionTicks, 0, motionTicks.Length);
                motionGeneration = Motor.ImpactGeneration;
            }
            uint tick = Motor.TimeManager.LocalTick;
            motionTicks[tick % (uint)motionTicks.Length] = tick;
            motionVelocities[tick % (uint)motionVelocities.Length] = IncomingVelocity;
            body.MovePosition(Motor.Body.position);
            body.MoveRotation(Motor.Body.rotation);
        }

        internal Vector3 IncomingVelocityAt(uint tick) => motionGeneration == Motor.ImpactGeneration &&
            motionTicks[tick % (uint)motionTicks.Length] == tick
            ? motionVelocities[tick % (uint)motionVelocities.Length] : PresentedVelocity;

        internal bool OverlapsSphere(Vector3 point, float radius, Vector3 center)
        {
            Vector3 relative = point - center;
            Vector3 closest = Vector3.up * Mathf.Clamp(relative.y, -capsuleHalfSegment, capsuleHalfSegment);
            float combinedRadius = radius + capsuleRadius;
            return (relative - closest).sqrMagnitude <= combinedRadius * combinedRadius;
        }

        internal bool SweepSphere(Vector3 from, Vector3 to, float radius, Vector3 previousCenter,
            Vector3 center, out Vector3 intoPlayer, out float fraction)
        {
            Vector3 start = from - previousCenter;
            Vector3 travel = to - center - start;
            float combinedRadius = radius + capsuleRadius;
            float first = float.PositiveInfinity;
            if (OverlapsSphere(from, radius, previousCenter)) first = 0f;
            else
            {
                float a = travel.x * travel.x + travel.z * travel.z;
                float b = start.x * travel.x + start.z * travel.z;
                float c = start.x * start.x + start.z * start.z - combinedRadius * combinedRadius;
                if (EntryTime(a, b, c, out float cylinder))
                {
                    float height = start.y + travel.y * cylinder;
                    if (Mathf.Abs(height) <= capsuleHalfSegment) first = cylinder;
                }
                Vector3 bottom = start + Vector3.up * capsuleHalfSegment;
                Vector3 top = start - Vector3.up * capsuleHalfSegment;
                if (EntryTime(travel.sqrMagnitude, Vector3.Dot(bottom, travel), bottom.sqrMagnitude - combinedRadius * combinedRadius, out float lower))
                    first = Mathf.Min(first, lower);
                if (EntryTime(travel.sqrMagnitude, Vector3.Dot(top, travel), top.sqrMagnitude - combinedRadius * combinedRadius, out float upper))
                    first = Mathf.Min(first, upper);
            }
            intoPlayer = default;
            fraction = first;
            if (float.IsPositiveInfinity(first)) return false;
            Vector3 contact = start + travel * first;
            Vector3 axis = Vector3.up * Mathf.Clamp(contact.y, -capsuleHalfSegment, capsuleHalfSegment);
            intoPlayer = (axis - contact).normalized;
            if (intoPlayer == Vector3.zero) intoPlayer = travel.normalized;
            return true;
        }

        private static bool EntryTime(float a, float b, float c, out float time)
        {
            time = 0f;
            if (a <= 0f) return false;
            float discriminant = b * b - a * c;
            if (discriminant < 0f) return false;
            time = (-b - Mathf.Sqrt(discriminant)) / a;
            return time >= 0f && time <= 1f;
        }
    }
}
