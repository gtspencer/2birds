using System.Collections.Generic;
using UnityEngine;

namespace TwoBirds
{
    public sealed partial class PlayerMotor
    {
        private PlayerHealth health;
        private int supportMask;
        private bool supported, supportContact;
        private float landingSeverity;
        private readonly Collider[] supportQuery = new Collider[32];
        private readonly Dictionary<Rigidbody, (Vector3 velocity, Vector3 angular, Vector3 center)> supportMotion = new();
        private readonly HashSet<int> damagedCarts = new();
        private readonly List<int> separatedDamageCarts = new();
        internal GameSettings Settings => settings;
        internal bool HasLandingSupport => supported;
        internal Vector3 CaptureImpactVelocity
        {
            get
            {
                Vector3 velocity = Body.linearVelocity + pendingVelocityChange + Vector3.up * pendingCartLift;
                foreach (var entry in impacts)
                    if (entry.Impact.Generation == impactGeneration && entry.Impact.RequestId > lastOwnerRequestId)
                        velocity += entry.Impact.VelocityChange;
                return velocity;
            }
        }
        internal void ResetStamina() { stamina = settings.MaximumStamina; staminaRecoveryDelay = 0f; }
        private void ResetLanding()
        {
            supported = supportContact = false;
            landingSeverity = 0f;
            damagedCarts.Clear();
            supportMotion.Clear();
        }
        private void BeforeHealthPhysics(float delta)
        {
            if (!IsOwner || PredictionManager.IsReconciling) return;
            supportContact = supported && Body.IsSleeping();
            landingSeverity = 0f;
            supportMotion.Clear();
            CapsuleEnds(out var bottom, out var top);
            int count = Physics.OverlapCapsuleNonAlloc(bottom, top, capsule.radius +
                Body.linearVelocity.magnitude * delta + 0.1f, supportQuery, supportMask, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < count; i++)
            {
                var body = supportQuery[i].attachedRigidbody;
                if (!body || supportMotion.ContainsKey(body)) continue;
                supportMotion[body] = (body.linearVelocity, body.angularVelocity, body.worldCenterOfMass);
            }
            separatedDamageCarts.Clear();
            foreach (int id in damagedCarts)
                if (!GolfCartNetwork.Carts.TryGetValue(id, out var cart) ||
                    cart.SimulatesPhysics && !TouchesCart(cart, out _)) separatedDamageCarts.Add(id);
            foreach (int id in separatedDamageCarts) damagedCarts.Remove(id);
        }
        private void GatherHealthContact(Collision collision)
        {
            if (!ContactPhysicsActive || !IsOwner || PredictionManager.IsReconciling ||
                (supportMask & (1 << collision.gameObject.layer)) == 0) return;
            Vector3 center = preContactPosition + capsule.center;
            for (int i = 0; i < collision.contactCount; i++)
            {
                var point = collision.GetContact(i);
                if (point.thisCollider != capsule || point.normal.y <= 0.5f ||
                    point.point.y > center.y - capsule.height * 0.5f + capsule.radius) continue;
                supportContact = true;
                Vector3 relative = collision.relativeVelocity;
                if (collision.rigidbody && supportMotion.TryGetValue(collision.rigidbody, out var motion))
                    relative -= Vector3.Cross(motion.angular, point.point - motion.center);
                landingSeverity = Mathf.Max(landingSeverity, -Vector3.Dot(relative, point.normal));
            }
        }
        private void AfterHealthPhysics()
        {
            if (!IsOwner || PredictionManager.IsReconciling) return;
            bool landing = supportContact && !supported;
            supported = supportContact;
            if (!landing) return;
            bool protectedLanding = health.ConsumeLandingProtection();
            if (!protectedLanding && settings.FallDamageEnabled && landingSeverity > settings.LandingDamageSpeed)
                health.ApplyDamage(settings.LandingDamage);
        }
    }
}
