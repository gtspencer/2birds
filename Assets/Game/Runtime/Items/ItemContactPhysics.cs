using System.Collections.Concurrent;
using Unity.Collections;
using UnityEngine;

namespace TwoBirds
{
    internal static class ItemContactPhysics
    {
        internal struct Contact
        {
            internal Vector3 Point, Normal, Velocity;
        }
        private static readonly ConcurrentDictionary<EntityId, float> projectiles = new();
        private static readonly ConcurrentDictionary<(EntityId, EntityId), Contact> contacts = new();

        internal static void Register(Collider collider, float retention) => projectiles[collider.GetEntityId()] = retention;
        internal static void Unregister(Collider collider) => projectiles.TryRemove(collider.GetEntityId(), out _);
        internal static bool NoImpulse(EntityId collider) => projectiles.ContainsKey(collider);
        internal static bool NoImpulse(Collider collider) => collider && NoImpulse(collider.GetEntityId());
        internal static void BeginStep() => contacts.Clear();
        internal static void Clear() { projectiles.Clear(); contacts.Clear(); }
        internal static bool Take(Collider projectile, Collider target, out Contact contact) =>
            contacts.TryRemove((projectile.GetEntityId(), target.GetEntityId()), out contact);

        internal static void SuppressTarget(ref ModifiableContactPair pair, bool targetFirst)
        {
            var mass = pair.massProperties;
            if (targetFirst) { mass.inverseMassScale = 0f; mass.inverseInertiaScale = 0f; }
            else { mass.otherInverseMassScale = 0f; mass.otherInverseInertiaScale = 0f; }
            pair.massProperties = mass;
        }

        internal static Vector3 Normal(ModifiableContactPair pair, int index, bool targetFirst, bool ccd) =>
            pair.GetNormal(index) * (targetFirst ? -1f : 1f) * (ccd ? -1f : 1f);

        internal static void Modify(PhysicsScene scene, NativeArray<ModifiableContactPair> pairs) => Modify(pairs, false);
        internal static void ModifyCcd(PhysicsScene scene, NativeArray<ModifiableContactPair> pairs) => Modify(pairs, true);
        private static void Modify(NativeArray<ModifiableContactPair> pairs, bool ccd)
        {
            for (int p = 0; p < pairs.Length; p++)
            {
                var pair = pairs[p];
                bool first = projectiles.TryGetValue(pair.colliderEntityId, out float retention);
                bool second = projectiles.TryGetValue(pair.otherColliderEntityId, out float otherRetention);
                if (!first && !second) continue;
                if (first && second)
                {
                    for (int i = 0; i < pair.contactCount; i++) pair.IgnoreContact(i);
                    continue;
                }
                if (second) retention = otherRetention;
                SuppressTarget(ref pair, second);
                Vector3 velocity = first ? pair.bodyVelocity : pair.otherBodyVelocity;
                var key = (first ? pair.colliderEntityId : pair.otherColliderEntityId,
                    first ? pair.otherColliderEntityId : pair.colliderEntityId);
                for (int i = 0; i < pair.contactCount; i++)
                {
                    if (!ccd && pair.GetSeparation(i) > 0f) { pair.IgnoreContact(i); continue; }
                    Vector3 normal = Normal(pair, i, second, ccd);
                    pair.SetBounciness(i, retention);
                    pair.SetStaticFriction(i, 0f);
                    pair.SetDynamicFriction(i, 0f);
                    contacts.TryAdd(key, new Contact { Point = pair.GetPoint(i), Normal = normal, Velocity = velocity });
                }
            }
        }
    }
}
