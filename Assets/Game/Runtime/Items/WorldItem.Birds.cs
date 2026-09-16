using System;
using System.Collections.Generic;
using UnityEngine;

namespace TwoBirds
{
    public sealed partial class WorldItem
    {
        private struct BirdTravel
        {
            public Vector3 From, To;
            public float Speed;
            public double Start, End;
        }
        private BirdRegistry birdRegistry;
        private bool birdRock, birdSampling, birdHistory, birdPrimary;
        private bool birdSuppressOverlap, birdNewRelease;
        private Vector3 birdStart, birdVelocity;
        private double birdPhysicsTick, birdPreviousTick, birdPreviousMotion;
        private float birdCollisionFraction, nextBirdThreat;
        private readonly List<BirdTravel> birdTravel = new(8);

        private bool BirdEligible => birdRegistry && birdRock && birdRegistry.hitReporter != null && impactSphere && impactSphere.enabled &&
            Record.State == WorldItemState.World && !Record.Sleeping && !optimisticPickup && !registry.Replaying;

        private void BeforeBirdPhysics()
        {
            birdSampling = BirdEligible && birdRegistry.PrimaryRock(Record) && !Body.isKinematic && !Body.IsSleeping();
            if (!birdSampling) return;
            InstallBirdDetector(BodySpherePosition);
            birdStart = BodySpherePosition; birdVelocity = Body.linearVelocity;
            birdPhysicsTick = birdRegistry.Now; birdCollisionFraction = 0f;
        }

        private void BirdSceneryContact(Collision collision)
        {
            if (!birdSampling || registry.Replaying || (registry.EnvironmentMask & (1 << collision.gameObject.layer)) == 0) return;
            for (int i = 0; i < collision.contactCount; i++)
            {
                var contact = collision.GetContact(i);
                if (contact.thisCollider != impactSphere) continue;
                Vector3 center = contact.point + contact.normal * sphereRadius;
                float duration = (float)registry.TickDelta;
                float portion = Mathf.Clamp((center - birdStart).magnitude / Mathf.Max(0.001f, birdVelocity.magnitude * duration), 0f, 1f - birdCollisionFraction);
                float speed = birdCollisionFraction == 0f ? birdVelocity.magnitude : collision.relativeVelocity.magnitude;
                birdTravel.Add(new BirdTravel { From = birdStart, To = center, Speed = speed,
                    Start = birdPhysicsTick + birdCollisionFraction, End = birdPhysicsTick + birdCollisionFraction + portion });
                birdCollisionFraction += portion; birdStart = center; birdVelocity = Body.linearVelocity;
                break;
            }
        }
        private void OnCollisionStay(Collision collision) => BirdSceneryContact(collision);

        private void AfterBirdPhysics(float seconds)
        {
            if (!birdSampling) return;
            birdTravel.Add(new BirdTravel { From = birdStart, To = BodySpherePosition, Speed = birdVelocity.magnitude,
                Start = birdPhysicsTick + birdCollisionFraction, End = birdPhysicsTick + 1d });
            birdSampling = false;
            var source = new BirdHitReport { Source = Record.Motion.Id, Player = Record.BirdPlayer, Operation = Record.Operation };
            Vector3 offset = PresentedSpherePosition - BodySpherePosition;
            foreach (var segment in birdTravel)
                SweepBirds(source, segment.From + offset, segment.To + offset, segment.Speed, segment.Start, segment.End);
            birdTravel.Clear();
        }

        internal void SampleBirdContacts()
        {
            bool primary = BirdEligible && birdRegistry.PrimaryRock(Record);
            if (!primary) { if (birdPrimary) ResetBirdContact(); birdPrimary = false; return; }
            var source = new BirdHitReport { Source = Record.Motion.Id, Player = Record.BirdPlayer, Operation = Record.Operation };
            Vector3 sphere = PresentedSpherePosition;
            InstallBirdDetector(sphere);
            Vector3 offset = sphere - BodySpherePosition;
            double now = birdRegistry.Now;
            if (registry.IsHost || Predicted)
            {
                foreach (var segment in birdTravel)
                    SweepBirds(source, segment.From + offset, segment.To + offset, segment.Speed, segment.Start, segment.End);
            }
            else if (sampleCount > 0 && birdHistory && presentedMotionTick > birdPreviousMotion)
            {
                double cursor = Math.Max(birdPreviousMotion, samples[0].Tick);
                double duration = presentedMotionTick - cursor;
                double first = cursor;
                Vector3 from = SphereAt(cursor) + offset;
                while (cursor < presentedMotionTick)
                {
                    double endTick = presentedMotionTick;
                    for (int i = 0; i < sampleCount; i++) if (samples[i].Tick > cursor) { endTick = Math.Min(endTick, samples[i].Tick); break; }
                    Vector3 to = SphereAt(endTick) + offset;
                    Vector3 physicalVelocity = samples[sampleCount - 1].Velocity;
                    for (int i = 0; i < sampleCount; i++) if (samples[i].Tick >= cursor) { physicalVelocity = samples[i].Velocity; break; }
                    double startBird = birdPreviousTick + (now - birdPreviousTick) * ((cursor - first) / duration);
                    double endBird = birdPreviousTick + (now - birdPreviousTick) * ((endTick - first) / duration);
                    SweepBirds(source, from, to, physicalVelocity.magnitude, startBird, endBird);
                    cursor = endTick; from = to;
                }
            }
            else if (!birdHistory)
            {
                float speed = Body.isKinematic ? Record.Motion.Velocity.magnitude : Body.linearVelocity.magnitude;
                SweepBirds(source, sphere, sphere, speed, now, now);
            }
            if (Time.unscaledTime >= nextBirdThreat)
            {
                nextBirdThreat = Time.unscaledTime + 0.2f;
                Vector3 velocity = Body.isKinematic ? Record.Motion.Velocity : Body.linearVelocity;
                if (velocity.sqrMagnitude > 0.01f)
                    birdRegistry.hitReporter.Threat(BirdThreatKind.Rock, Record.Motion.Id, sphere, birdRegistry.MaximumScareRadius, now, false);
            }
            birdTravel.Clear(); birdPreviousTick = now; birdPreviousMotion = presentedMotionTick; birdHistory = true;
        }

        private void SweepBirds(BirdHitReport source, Vector3 from, Vector3 to, float speed, double start, double end)
        {
            Vector3 travel = to - from;
            if (travel.sqrMagnitude > 0f && Physics.SphereCast(from, sphereRadius, travel.normalized, out var obstacle,
                travel.magnitude, registry.EnvironmentMask, QueryTriggerInteraction.Ignore))
            {
                float portion = Mathf.Clamp01(obstacle.distance / travel.magnitude);
                to = Vector3.Lerp(from, to, portion); end = start + (end - start) * portion;
            }
            birdRegistry.hitReporter.Sweep(source, from, to, sphereRadius, speed, start, end, Predicted);
        }

        private void InstallBirdDetector(Vector3 sphere)
        {
            if (birdSuppressOverlap || !birdPrimary && !birdNewRelease)
                birdRegistry.hitReporter.RebaseRock(Record.Motion.Id, sphere, sphereRadius);
            if (!birdPrimary) birdHistory = false;
            birdSuppressOverlap = birdNewRelease = false; birdPrimary = true;
        }

        private void ResetBirdContact(bool suppressOverlap = false)
        {
            birdTravel.Clear(); birdHistory = birdSampling = false;
            birdSuppressOverlap = suppressOverlap;
            if (birdRegistry && birdRegistry.hitReporter != null) birdRegistry.hitReporter.ResetRock(Record.Motion.Id);
        }
    }
}
