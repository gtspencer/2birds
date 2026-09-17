using System.Collections.Generic;
using UnityEngine;

namespace TwoBirds
{
    public sealed partial class WorldItem
    {
        private BirdRegistry birdRegistry;
        private bool birdRock;
        private bool birdRebase;
        private float nextBirdThreat;
        private EntityId birdColliderId;
        private readonly Dictionary<EntityId, uint> touchingBirds = new();
        private readonly Dictionary<EntityId, uint> suppressedBirds = new();
        private readonly List<EntityId> separatedBirds = new();

        private bool BirdEligible => birdRegistry && birdRock && birdRegistry.hitReporter != null && impactSphere && impactSphere.enabled &&
            Record.State == WorldItemState.World && !optimisticPickup && !registry.Replaying && Simulating;

        private void BeforeBirdPhysics()
        {
            if (!BirdEligible || Body.isKinematic) return;
            birdRegistry.RegisterPhysicalRock(birdColliderId, BodySpherePosition, sphereRadius, birdRebase, suppressedBirds, separatedBirds);
            birdRebase = false;
        }

        private void BirdContact(Collision collision)
        {
            if (!BirdEligible || !birdRegistry.TryRockContact(birdColliderId, collision.collider, out var contact)) return;
            EntityId collider = collision.collider.GetEntityId();
            if (touchingBirds.TryGetValue(collider, out uint life) && life == contact.Life) return;
            touchingBirds[collider] = contact.Life;
            Vector3 normal = contact.Normal;
            float weight = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(1f + normal.y));
            Vector3 lift = Vector3.up - normal * Mathf.Min(0f, normal.y);
            Vector3 velocity = Body.linearVelocity + lift * (Mathf.Max(0f, birdRegistry.Settings.RockLiftMultiplier) * contact.Closing * weight);
            if (Definition.MaxSpeed > 0f) velocity = Vector3.ClampMagnitude(velocity, Definition.MaxSpeed);
            Body.linearVelocity = velocity;
            MotionBoundary = true;
            birdRegistry.ReportRockContact(Record, contact, registry.ReleasePending(Record.Motion.Id));
        }

        private void OnCollisionStay(Collision collision) => BirdContact(collision);
        private void OnCollisionExit(Collision collision) => touchingBirds.Remove(collision.collider.GetEntityId());

        private void AfterBirdPhysics(float seconds)
        {
            if (BirdEligible && Definition.MaxSpeed > 0f && !Body.isKinematic && Body.linearVelocity.sqrMagnitude > Definition.MaxSpeed * Definition.MaxSpeed)
                Body.linearVelocity = Vector3.ClampMagnitude(Body.linearVelocity, Definition.MaxSpeed);
        }

        internal void SampleBirdContacts()
        {
            if (!BirdEligible || Record.Sleeping || Body.IsSleeping() || Time.unscaledTime < nextBirdThreat) return;
            nextBirdThreat = Time.unscaledTime + 0.2f;
            if (Body.linearVelocity.sqrMagnitude > 0.01f)
                birdRegistry.hitReporter.Threat(BirdThreatKind.Rock, Record.Motion.Id, BodySpherePosition,
                    birdRegistry.MaximumScareRadius, birdRegistry.Now, false);
        }

        private void ResetBirdContact(bool suppressOverlap = false)
        {
            touchingBirds.Clear();
            suppressedBirds.Clear();
            birdRebase = suppressOverlap;
        }
    }
}
