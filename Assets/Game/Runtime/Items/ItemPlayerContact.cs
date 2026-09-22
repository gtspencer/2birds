using System;
using System.Collections.Generic;
using UnityEngine;

namespace TwoBirds
{
    internal struct ItemContactFrame
    {
        internal bool Eligible, Simulating, Sleeping;
        internal Vector3 PresentedCenter, BodyCenter;
        internal float Radius;
        internal Collider IgnoredPlayer;
        internal double PresentedTick;
    }

    internal sealed class ItemPlayerContact
    {
        private struct ContactSegment
        {
            public Vector3 End;
            public Vector3 Velocity;
            public float Seconds;
            public uint Tick;
        }

        private readonly List<ContactSegment> physicsSegments = new();
        private float contactSeconds;
        private bool physicsContactSampled;
        private PlayerItemHitbox contactPlayer;
        private PlayerItemHitbox damagedPlayer;
        private uint damagedGeneration;
        private uint contactGeneration;
        private uint contactReset;
        private Vector3 previousSphere;
        private Vector3 previousCorrectionOffset;
        private Vector3 previousPlayer;
        private Vector3 previousPlayerVelocity;
        private Vector3 previousPlayerCorrection;
        private double previousMotionTick;
        private bool hasContactPose;
        private bool touchingPlayer;
        private bool rebaseContactPose;
        private readonly WorldItemRegistry registry;
        private readonly RigidbodyMotionState motion;
        private readonly Action<PlayerItemHitbox, Vector3, Vector3, Vector3> report;
        private readonly Func<ItemMotion, Vector3> spherePosition;
        private ItemContactFrame current;
        private Vector3 physicsStartSphere;
        private bool ContactEligible => current.Eligible;
        private bool Simulating => current.Simulating;
        private Vector3 PresentedSpherePosition => current.PresentedCenter;
        private Vector3 BodySpherePosition => current.BodyCenter;
        private Collider ignoredPlayer => current.IgnoredPlayer;
        private float sphereRadius => current.Radius;
        private double presentedMotionTick => current.PresentedTick;
        private int sampleCount => motion.Count;
        private ItemMotion[] samples => motion.Samples;
        internal bool Rebase { get => rebaseContactPose; set => rebaseContactPose = value; }

        internal ItemPlayerContact(WorldItemRegistry owner, RigidbodyMotionState state,
            Func<ItemMotion, Vector3> center, Action<PlayerItemHitbox, Vector3, Vector3, Vector3> impact)
        { registry = owner; motion = state; spherePosition = center; report = impact; }

        private ItemMotion PresentedMotionAt(double tick, out Vector3 velocity) =>
            motion.Sample(tick, false, sphereRadius, registry.EnvironmentMask, registry.TickDelta, out velocity);
        private Vector3 SphereAt(double tick) => spherePosition(PresentedMotionAt(tick, out _));
        private void ReportImpact(PlayerItemHitbox player, Vector3 velocity, Vector3 playerVelocity, Vector3 normal) =>
            report(player, velocity, playerVelocity, normal);

        internal void BeforePhysics(in ItemContactFrame frame, bool sample)
        {
            current = frame;
            if (registry.IsHost && damagedPlayer &&
                !damagedPlayer.OverlapsSphere(frame.BodyCenter, frame.Radius + 0.03f, damagedPlayer.PresentedCenter)) damagedPlayer = null;
            if (!frame.Eligible) ResetContactSamples();
            physicsContactSampled = sample && !registry.IsHost;
            if (physicsContactSampled) physicsStartSphere = frame.BodyCenter;
        }

        internal void AfterPhysics(Vector3 end, float seconds)
        {
            if (!physicsContactSampled) return;
            Segment(end, (end - physicsStartSphere) / seconds, seconds, registry.LocalTick);
            physicsContactSampled = false;
        }

        internal void Segment(Vector3 end, Vector3 velocity, float seconds, uint tick)
        {
            physicsSegments.Add(new ContactSegment { End = end, Velocity = velocity, Seconds = seconds, Tick = tick });
            contactSeconds += seconds;
            physicsStartSphere = end;
        }

        internal void Reposition(Vector3 presented, Vector3 physical)
        {
            rebaseContactPose = true;
            if (!hasContactPose) return;
            previousSphere = presented;
            previousCorrectionOffset = presented - physical;
        }

        internal void Damage(PlayerItemHitbox player, int amount, Vector3 velocityChange)
        {
            if (damagedPlayer == player && damagedGeneration == player.Motor.ImpactGeneration) return;
            damagedPlayer = player;
            damagedGeneration = player.Motor.ImpactGeneration;
            player.Damage(amount, velocityChange);
        }

        internal void HostContact(PlayerItemHitbox player, in ItemContactFrame frame, Vector3 incoming, Vector3 normal)
        {
            if (!registry.IsHost || !frame.Simulating || !frame.Eligible || !player || !player.Motor.IsOwner ||
                frame.IgnoredPlayer == player.Collider) return;
            report(player, incoming, player.IncomingVelocity, normal);
        }

        internal void Reset()
        {
            damagedPlayer = null;
            damagedGeneration = 0;
            ResetContactSamples();
        }

        internal void SamplePlayerContact(PlayerItemHitbox player, in ItemContactFrame frame)
        {
            current = frame;
            Sample(player);
        }

        private void Sample(PlayerItemHitbox player)
        {
            if (!ContactEligible || player == null || player.Suspended || !player.Motor.IsOwner ||
                current.Sleeping)
            {
                ResetContactSamples();
                return;
            }
            if (contactPlayer != player || contactGeneration != player.Motor.ImpactGeneration || contactReset != player.Motor.ResetRevision)
            {
                bool rebase = rebaseContactPose || contactPlayer != null;
                ResetContactSamples();
                contactPlayer = player;
                contactGeneration = player.Motor.ImpactGeneration;
                contactReset = player.Motor.ResetRevision;
                rebaseContactPose = rebase;
            }
            Vector3 sphere = PresentedSpherePosition;
            Vector3 correctionOffset = sphere - BodySpherePosition;
            Vector3 correctionDelta = correctionOffset - previousCorrectionOffset;
            Vector3 from = previousSphere + correctionDelta;
            Vector3 center = player.PresentedCenter;
            Vector3 playerCorrection = player.PresentationCorrection - previousPlayerCorrection;
            Vector3 playerFrom = previousPlayer + playerCorrection;
            if (rebaseContactPose || correctionDelta.sqrMagnitude > 0f || playerCorrection.sqrMagnitude > 0f)
            {
                touchingPlayer = hasContactPose
                    ? player.OverlapsSphere(from, sphereRadius, playerFrom)
                    : player.OverlapsSphere(sphere, sphereRadius, center);
                rebaseContactPose = false;
            }
            if (hasContactPose)
            {
                if (Simulating)
                {
                    float elapsed = 0f;
                    Vector3 startPlayer = playerFrom;
                    foreach (var segment in physicsSegments)
                    {
                        elapsed += segment.Seconds;
                        Vector3 endPlayer = Vector3.Lerp(playerFrom, center, elapsed / contactSeconds);
                        Vector3 end = segment.End + correctionOffset;
                        Vector3 playerVelocity = player.IncomingVelocityAt(segment.Tick);
                        SweepContact(player, from, end, startPlayer, endPlayer, segment.Velocity, playerVelocity, playerVelocity);
                        from = end;
                        startPlayer = endPlayer;
                    }
                    if (physicsSegments.Count == 0)
                        SweepContact(player, from, sphere, playerFrom, center, Vector3.zero, previousPlayerVelocity, player.PresentedVelocity);
                }
                else SweepRemoteContacts(player, from, sphere, correctionOffset, playerFrom, center);
            }
            previousSphere = sphere;
            previousCorrectionOffset = correctionOffset;
            previousPlayer = center;
            previousPlayerVelocity = player.PresentedVelocity;
            previousPlayerCorrection = player.PresentationCorrection;
            previousMotionTick = presentedMotionTick;
            hasContactPose = true;
            physicsSegments.Clear();
            contactSeconds = 0f;
        }

        private void SweepRemoteContacts(PlayerItemHitbox player, Vector3 from, Vector3 sphere, Vector3 offset,
            Vector3 playerFrom, Vector3 center)
        {
            double start = previousMotionTick;
            if (sampleCount == 0 || presentedMotionTick <= start)
            {
                SweepContact(player, from, sphere, playerFrom, center, Vector3.zero, previousPlayerVelocity, player.PresentedVelocity);
                return;
            }
            if (start < samples[0].Tick)
            {
                start = samples[0].Tick;
                from = SphereAt(start) + offset;
                touchingPlayer = player.OverlapsSphere(from, sphereRadius, playerFrom);
            }
            double duration = presentedMotionTick - start;
            if (duration <= 0d)
            {
                SweepContact(player, from, sphere, playerFrom, center, Vector3.zero, previousPlayerVelocity, player.PresentedVelocity);
                return;
            }
            double cursor = start;
            Vector3 startPlayer = playerFrom;
            Vector3 startVelocity = previousPlayerVelocity;
            while (cursor < presentedMotionTick)
            {
                double endTick = presentedMotionTick;
                for (int i = 0; i < sampleCount; i++)
                    if (samples[i].Tick > cursor) { endTick = System.Math.Min(endTick, samples[i].Tick); break; }
                double extrapolationEnd = samples[sampleCount - 1].Tick + 0.1d / registry.TickDelta;
                if (extrapolationEnd > cursor) endTick = System.Math.Min(endTick, extrapolationEnd);
                float amount = (float)((endTick - start) / duration);
                Vector3 end = endTick == presentedMotionTick ? sphere : SphereAt(endTick) + offset;
                Vector3 endPlayer = Vector3.Lerp(playerFrom, center, amount);
                Vector3 endVelocity = Vector3.Lerp(previousPlayerVelocity, player.PresentedVelocity, amount);
                PresentedMotionAt((cursor + endTick) * 0.5d, out Vector3 rockVelocity);
                if ((end - from).sqrMagnitude == 0f) rockVelocity = Vector3.zero;
                SweepContact(player, from, end, startPlayer, endPlayer, rockVelocity, startVelocity, endVelocity);
                from = end;
                startPlayer = endPlayer;
                startVelocity = endVelocity;
                cursor = endTick;
            }
        }

        private void SweepContact(PlayerItemHitbox player, Vector3 from, Vector3 to, Vector3 playerFrom,
            Vector3 playerTo, Vector3 rockVelocity, Vector3 playerVelocityFrom, Vector3 playerVelocityTo)
        {
            if (damagedPlayer == player && (to - from - (playerTo - playerFrom)).sqrMagnitude > 0.000001f &&
                !player.OverlapsSphere(from, sphereRadius + 0.03f, playerFrom)) damagedPlayer = null;
            if (ignoredPlayer != player.Collider && !touchingPlayer &&
                player.SweepSphere(from, to, sphereRadius, playerFrom, playerTo, out Vector3 intoPlayer, out float fraction))
                ReportImpact(player, rockVelocity, Vector3.Lerp(playerVelocityFrom, playerVelocityTo, fraction), intoPlayer);
            touchingPlayer = player.OverlapsSphere(to, sphereRadius, playerTo);
        }

        internal void ResetIncomingMotion()
        {
            physicsStartSphere = default;
            physicsSegments.Clear();
            contactSeconds = 0f;
            physicsContactSampled = false;
        }

        internal void ResetContactSamples()
        {
            contactPlayer = null;
            contactGeneration = 0;
            contactReset = 0;
            previousSphere = previousPlayer = previousCorrectionOffset = default;
            previousPlayerVelocity = previousPlayerCorrection = default;
            previousMotionTick = presentedMotionTick;
            hasContactPose = touchingPlayer = false;
            rebaseContactPose = false;
            ResetIncomingMotion();
        }

    }
}
