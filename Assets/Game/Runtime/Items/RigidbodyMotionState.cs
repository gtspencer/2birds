using UnityEngine;

namespace TwoBirds
{
    internal sealed class RigidbodyMotionState
    {
        internal readonly ItemMotion[] History = new ItemMotion[128];
        internal readonly ItemMotion[] Samples = new ItemMotion[16];
        internal int Count;
        internal float ReceivedAt;
        private Vector3 offset;
        private float remaining;

        internal void Add(ItemMotion motion)
        {
            if (Count == Samples.Length)
            {
                System.Array.Copy(Samples, 1, Samples, 0, Samples.Length - 1);
                Count--;
            }
            Samples[Count++] = motion;
            ReceivedAt = Time.unscaledTime;
        }

        internal void Reset()
        {
            System.Array.Clear(History, 0, History.Length);
            System.Array.Clear(Samples, 0, Samples.Length);
            Count = 0; ReceivedAt = remaining = 0f; offset = default;
        }

        internal static ItemMotion Capture(Rigidbody body, ItemMotion previous, uint tick, bool centered, Vector3 center)
        {
            return new ItemMotion { Id = previous.Id, Revision = previous.Revision, Tick = tick,
                Sequence = previous.Sequence, Path = previous.Path, Sleeping = !body.isKinematic && body.IsSleeping(),
                Position = centered ? center : body.position, Rotation = body.rotation, PositionIsSphereCenter = centered,
                Velocity = body.isKinematic ? Vector3.zero : centered ? body.GetPointVelocity(center) : body.linearVelocity,
                AngularVelocity = body.isKinematic ? Vector3.zero : body.angularVelocity };
        }

        internal static void Apply(Rigidbody body, ItemMotion motion, Vector3 sphereCenter)
        {
            if (!motion.RotationOmitted) body.rotation = motion.Rotation;
            body.position = motion.PositionIsSphereCenter ? motion.Position - body.rotation * sphereCenter : motion.Position;
            body.transform.SetPositionAndRotation(body.position, body.rotation);
            if (body.isKinematic) return;
            if (!motion.RotationOmitted) body.angularVelocity = motion.AngularVelocity;
            body.linearVelocity = motion.PositionIsSphereCenter
                ? motion.Velocity - Vector3.Cross(body.angularVelocity, body.position + body.rotation * sphereCenter - body.worldCenterOfMass)
                : motion.Velocity;
        }

        internal void Departure(Vector3 delta, float duration) { offset = delta; remaining = duration; }
        internal void PresentOffset(Transform visual, float duration)
        {
            remaining = Mathf.Max(0f, remaining - Time.deltaTime);
            visual.localPosition = offset * (remaining / duration);
        }

        internal ItemMotion Sample(double tick, bool sleeping, float sphereRadius, int environmentMask, double tickDelta, out Vector3 velocity)
        {
            ItemMotion from = Samples[0], to = from;
            for (int i = 1; i < Count; i++)
            {
                to = Samples[i];
                if (to.Tick >= tick) break;
                from = to;
            }
            float amount = to.Tick == from.Tick ? 1f : Mathf.Clamp01((float)((tick - from.Tick) / (to.Tick - from.Tick)));
            var motion = new ItemMotion { Position = Vector3.Lerp(from.Position, to.Position, amount),
                Rotation = to.RotationOmitted ? Quaternion.identity : from.RotationOmitted ? to.Rotation :
                    Quaternion.Slerp(from.Rotation, to.Rotation, amount), RotationOmitted = to.RotationOmitted,
                PositionIsSphereCenter = to.PositionIsSphereCenter };
            velocity = to.Tick > from.Tick && tick >= from.Tick
                ? (to.Position - from.Position) / (float)((to.Tick - from.Tick) * tickDelta) : Vector3.zero;
            ItemMotion latest = Samples[Count - 1];
            if (!sleeping && tick > latest.Tick)
            {
                float seconds = Mathf.Min((float)((tick - latest.Tick) * tickDelta), 0.1f);
                Vector3 travel = latest.Velocity * seconds;
                velocity = seconds < 0.1f ? latest.Velocity : Vector3.zero;
                RaycastHit hit;
                bool blocked = motion.PositionIsSphereCenter
                    ? Physics.SphereCast(latest.Position, sphereRadius, travel.normalized, out hit, travel.magnitude,
                        environmentMask, QueryTriggerInteraction.Ignore)
                    : Physics.Raycast(latest.Position, travel.normalized, out hit, travel.magnitude,
                        environmentMask, QueryTriggerInteraction.Ignore);
                if (!blocked) motion.Position += travel;
                else
                {
                    motion.Position = latest.Position + travel.normalized * Mathf.Max(0f, hit.distance - 0.01f);
                    velocity = Vector3.zero;
                }
            }
            return motion;
        }

    }
}
