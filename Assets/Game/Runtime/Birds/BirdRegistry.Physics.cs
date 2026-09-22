using System.Collections.Concurrent;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;

namespace TwoBirds
{
    public sealed partial class BirdRegistry
    {
        private sealed class HitShape
        {
            public Rigidbody Body;
            public SphereCollider Collider;
            public Vector3 EndPosition;
            public uint EndTick;
            public bool Rebased;
        }
        internal struct RockContact
        {
            public uint Life;
            public float Speed, Closing;
            public Vector3 Normal, Position;
        }
        private readonly Dictionary<uint, HitShape> hitShapes = new();
        private readonly Dictionary<EntityId, uint> shapeLives = new();
        private readonly HashSet<EntityId> physicalRocks = new();
        private readonly HashSet<(EntityId rock, EntityId bird)> suppressedRockPairs = new();
        private readonly Dictionary<EntityId, HitShape> colliderShapes = new();
        private readonly ConcurrentDictionary<(EntityId rock, EntityId bird), RockContact> rockContacts = new();
        private readonly Stack<HitShape> spareShapes = new();
        private readonly List<uint> retiredShapes = new();
        private float bounceMultiplier, undersideMultiplier;

        internal void PrepareRockPhysics(uint tick)
        {
            physicalRocks.Clear();
            suppressedRockPairs.Clear();
            rockContacts.Clear();
            if (!active || !ready) return;
            if (Replaying)
            {
                foreach (var shape in hitShapes.Values)
                    if (!shape.Body.isKinematic)
                    {
                        shape.Body.collisionDetectionMode = CollisionDetectionMode.Discrete;
                        shape.Body.isKinematic = true;
                    }
                return;
            }
            bounceMultiplier = Mathf.Clamp01(settings.RockBounceMultiplier);
            undersideMultiplier = Mathf.Clamp01(settings.RockUndersideMultiplier);
            retiredShapes.Clear();
            foreach (var pair in hitShapes)
                if (!records.ContainsKey(pair.Key)) retiredShapes.Add(pair.Key);
            foreach (uint life in retiredShapes)
            {
                var shape = hitShapes[life];
                shapeLives.Remove(shape.Collider.GetEntityId());
                colliderShapes.Remove(shape.Collider.GetEntityId());
                shape.Collider.enabled = false;
                shape.Body.collisionDetectionMode = CollisionDetectionMode.Discrete;
                shape.Body.isKinematic = true;
                shape.EndTick = 0;
                spareShapes.Push(shape);
                hitShapes.Remove(life);
            }
            foreach (var record in records.Values)
            {
                if (!hitShapes.TryGetValue(record.Life, out var shape))
                {
                    shape = spareShapes.Count > 0 ? spareShapes.Pop() : CreateHitShape();
                    shape.Collider.radius = species[record.Species].Radius;
                    hitShapes.Add(record.Life, shape);
                    shapeLives.Add(shape.Collider.GetEntityId(), record.Life);
                    colliderShapes.Add(shape.Collider.GetEntityId(), shape);
                }
                bool enabled = !hitReporter.Predicted(record.Life);
                if (shape.Collider.enabled != enabled) shape.Collider.enabled = enabled;
                if (!enabled) continue;
                Vector3 from = BirdMotion.Evaluate(BirdMotion.Current(record, tick), tick, Delta).Position;
                Vector3 to = BirdMotion.Evaluate(BirdMotion.Current(record, tick + 1d), tick + 1d, Delta).Position;
                shape.Rebased = shape.EndTick != tick || (shape.EndPosition - from).sqrMagnitude > 0.000001f;
                shape.EndPosition = to; shape.EndTick = tick + 1;
                if (shape.Body.isKinematic)
                {
                    shape.Body.isKinematic = false;
                    shape.Body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
                }
                if ((shape.Body.position - from).sqrMagnitude > 0.000001f) shape.Body.position = from;
                Vector3 velocity = (to - from) / (float)Delta;
                if (shape.Body.linearVelocity != velocity) shape.Body.linearVelocity = velocity;
            }
        }

        private HitShape CreateHitShape()
        {
            var root = new GameObject("Bird hit shape") { layer = LayerMask.NameToLayer("BirdQuery") };
            root.transform.SetParent(transform, false);
            var body = root.AddComponent<Rigidbody>();
            body.useGravity = false;
            body.linearDamping = 0f;
            body.constraints = RigidbodyConstraints.FreezeRotation;
            body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            var collider = root.AddComponent<SphereCollider>();
            collider.includeLayers = LayerMask.GetMask("ItemWorld");
            collider.excludeLayers = ~LayerMask.GetMask("ItemWorld");
            collider.layerOverridePriority = 1;
            collider.hasModifiableContacts = true;
            return new HitShape { Body = body, Collider = collider };
        }

        internal void RegisterPhysicalRock(EntityId collider, Vector3 center, float radius, bool rebase,
            Dictionary<EntityId, uint> suppressed, List<EntityId> separated)
        {
            physicalRocks.Add(collider);
            separated.Clear();
            foreach (var pair in suppressed)
            {
                if (!colliderShapes.TryGetValue(pair.Key, out var shape) || !shape.Collider.enabled || shapeLives[pair.Key] != pair.Value ||
                    (shape.Body.position - center).sqrMagnitude > Mathf.Pow(radius + shape.Collider.radius, 2f))
                    separated.Add(pair.Key);
            }
            foreach (EntityId bird in separated) suppressed.Remove(bird);
            foreach (var pair in hitShapes)
            {
                var shape = pair.Value;
                if (!rebase && !shape.Rebased) continue;
                float combined = radius + shape.Collider.radius;
                if (shape.Collider.enabled && (shape.Body.position - center).sqrMagnitude <= combined * combined)
                    suppressed[shape.Collider.GetEntityId()] = pair.Key;
            }
            foreach (EntityId bird in suppressed.Keys) suppressedRockPairs.Add((collider, bird));
        }
        private void ModifyRockContacts(PhysicsScene scene, NativeArray<ModifiableContactPair> pairs) => ModifyRockContacts(pairs, false);
        private void ModifyRockCcdContacts(PhysicsScene scene, NativeArray<ModifiableContactPair> pairs) => ModifyRockContacts(pairs, true);

        private void ModifyRockContacts(NativeArray<ModifiableContactPair> pairs, bool ccd)
        {
            for (int pairIndex = 0; pairIndex < pairs.Length; pairIndex++)
            {
                var pair = pairs[pairIndex];
                bool birdFirst = shapeLives.TryGetValue(pair.colliderEntityId, out uint life);
                if (!birdFirst && !shapeLives.TryGetValue(pair.otherColliderEntityId, out life)) continue;
                EntityId rock = birdFirst ? pair.otherColliderEntityId : pair.colliderEntityId;
                EntityId bird = birdFirst ? pair.colliderEntityId : pair.otherColliderEntityId;
                if (ItemContactPhysics.NoImpulse(rock)) continue;
                if (!physicalRocks.Contains(rock) || suppressedRockPairs.Contains((rock, bird)))
                {
                    for (int i = 0; i < pair.contactCount; i++) pair.IgnoreContact(i);
                    continue;
                }
                Vector3 velocity = birdFirst ? pair.otherBodyVelocity : pair.bodyVelocity;
                Vector3 relative = velocity - (birdFirst ? pair.bodyVelocity : pair.otherBodyVelocity);
                ItemContactPhysics.SuppressTarget(ref pair, birdFirst);
                for (int i = 0; i < pair.contactCount; i++)
                {
                    if (!ccd && pair.GetSeparation(i) > 0f) { pair.IgnoreContact(i); continue; }
                    Vector3 normal = ItemContactPhysics.Normal(pair, i, birdFirst, ccd);
                    float weight = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(1f + normal.y));
                    pair.SetBounciness(i, bounceMultiplier * Mathf.Lerp(undersideMultiplier, 1f, weight));
                    pair.SetStaticFriction(i, 0f);
                    pair.SetDynamicFriction(i, 0f);
                    rockContacts.TryAdd((rock, bird), new RockContact { Life = life, Speed = velocity.magnitude,
                        Closing = Mathf.Max(0f, -Vector3.Dot(relative, normal)), Normal = normal,
                        Position = birdFirst ? pair.position : pair.otherPosition });
                }
            }
        }

        internal bool TryRockContact(EntityId rock, Collider bird, out RockContact contact) =>
            rockContacts.TryRemove((rock, bird.GetEntityId()), out contact);

        internal void ReportRockContact(ItemRecord rock, RockContact contact, bool waiting)
        {
            if (!records.TryGetValue(contact.Life, out var bird)) return;
            if (Host && rock.Releaser < 0 && rock.Operation == 0) ledger.Accept(rock.Motion.Id, 0, 0);
            hitReporter.RockContact(new BirdHitReport { Source = rock.Motion.Id, Player = rock.BirdPlayer,
                Operation = rock.Operation }, bird, contact.Speed, contact.Position, waiting);
            if (hitReporter.Predicted(contact.Life) && hitShapes.TryGetValue(contact.Life, out var shape)) shape.Collider.enabled = false;
        }

        internal bool ProjectileBirdContact(BirdHitReport source, Collider collider, float speed,
            Vector3 position, out BirdHitReport report)
        {
            report = default;
            if (!shapeLives.TryGetValue(collider.GetEntityId(), out uint life) || !records.TryGetValue(life, out var bird)) return false;
            bool result = hitReporter.TakeContact(source, bird, speed, position, out report);
            if (hitReporter.Predicted(life) && hitShapes.TryGetValue(life, out var shape)) shape.Collider.enabled = false;
            return result;
        }

        private void ClearRockPhysics()
        {
            foreach (var shape in hitShapes.Values) Destroy(shape.Body.gameObject);
            foreach (var shape in spareShapes) Destroy(shape.Body.gameObject);
            hitShapes.Clear(); spareShapes.Clear(); shapeLives.Clear(); colliderShapes.Clear(); physicalRocks.Clear(); suppressedRockPairs.Clear(); rockContacts.Clear();
        }
    }
}
