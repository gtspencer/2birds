using System.Collections.Generic;
using UnityEngine;

namespace TwoBirds
{
    public sealed class PotionArea : MonoBehaviour
    {
        private WorldItemRegistry registry;
        private PotionActivation activation;
        private PotionDefinition definition;
        private GolfCartNetwork cart;
        private Transform visual;
        private SphereCollider sphere;
        private readonly HashSet<PlayerEffectReceiver> occupants = new();
        private readonly HashSet<(int Player, uint Lifetime, uint Reset)> applied = new();
        private readonly List<PlayerEffectReceiver> stale = new();
        private bool initialized, snapshot, attached;
        private uint cartGeneration;
        private uint nextApplication;

        internal void Initialize(WorldItemRegistry owner, PotionActivation record, PotionDefinition potion, bool baseline, GameObject predictedCloud)
        {
            registry = owner; activation = record; definition = potion; snapshot = baseline;
            if (predictedCloud) visual = predictedCloud.transform;
            gameObject.layer = LayerMask.NameToLayer("PotionEffect");
            GolfCartNetwork.LifetimeChanged += CartChanged;
            if (record.Cart >= 0)
            {
                if (GolfCartNetwork.Carts.TryGetValue(record.Cart, out var resolved)) Attach(resolved);
            }
            else Begin();
        }
        private void CartChanged(GolfCartNetwork changed, bool spawned)
        {
            if (changed.ObjectId != activation.Cart) return;
            if (spawned && !attached) Attach(changed);
            else if (!spawned && cart == changed) Destroy(gameObject);
        }
        private void Attach(GolfCartNetwork resolved)
        {
            if (resolved.EffectLifetime == 0 || resolved.EffectLifetime != activation.CartLifetime) return;
            cart = resolved; attached = true; cartGeneration = cart.ContactGeneration;
            Begin();
        }
        private void Begin()
        {
            if (initialized || activation.Expiry <= registry.ServerTick) return;
            initialized = true;
            RefreshPlacement();
            if (!visual && definition.CloudVfx) visual = Instantiate(definition.CloudVfx, transform.position, Quaternion.identity).transform;
            if (!snapshot && definition.ImpactVfx)
            {
                var impact = Instantiate(definition.ImpactVfx, transform.position, Quaternion.identity);
                Destroy(impact, 3f);
            }
            if (definition.Application == PotionApplication.Zone)
            {
                var body = gameObject.AddComponent<Rigidbody>();
                body.isKinematic = true; body.useGravity = false;
                sphere = gameObject.AddComponent<SphereCollider>();
                sphere.isTrigger = true; sphere.radius = definition.Radius;
            }
            registry.RefreshEffectPoses();
            if (definition.Application == PotionApplication.Zone || !snapshot) Seed();
        }
        internal void RefreshPlacement()
        {
            if (!initialized) return;
            transform.position = cart ? cart.Controller.Body.position + cart.Controller.Body.rotation * activation.Position : activation.Position;
        }
        private void Seed()
        {
            foreach (var collider in Physics.OverlapSphere(transform.position, definition.Radius,
                LayerMask.GetMask("PlayerEffectReceiver"), QueryTriggerInteraction.Collide))
                if (collider.TryGetComponent<PlayerEffectReceiver>(out var receiver)) Enter(receiver);
        }
        internal void ReseedZone()
        {
            if (initialized && definition.Application == PotionApplication.Zone) Seed();
        }
        internal void Reseed(PlayerEffectReceiver receiver)
        {
            if (!initialized || definition.Application != PotionApplication.Zone) return;
            if ((receiver.Collider.ClosestPoint(transform.position) - transform.position).sqrMagnitude <= definition.Radius * definition.Radius)
                Enter(receiver);
        }
        private void OnTriggerEnter(Collider other)
        {
            if (!registry || registry.Replaying) return;
            if (other.TryGetComponent<PlayerEffectReceiver>(out var receiver)) Enter(receiver);
        }
        private void OnTriggerExit(Collider other)
        {
            if (!registry || registry.Replaying) return;
            if (other.TryGetComponent<PlayerEffectReceiver>(out var receiver)) Remove(receiver);
        }
        private void Enter(PlayerEffectReceiver receiver)
        {
            if (!receiver.Eligible || !receiver.Effects.IsOwner || !occupants.Add(receiver)) return;
            if (definition.Application == PotionApplication.Zone && definition.Effect == PotionEffect.Health)
                receiver.Effects.SetHealing(activation.Id, definition.HealthStrength, activation.Expiry);
            else if (applied.Add((receiver.Effects.ObjectId, receiver.Effects.Lifetime, receiver.Effects.Reset)))
                receiver.Effects.Apply(activation, definition);
        }
        internal void Remove(PlayerEffectReceiver receiver)
        {
            if (occupants.Remove(receiver) && receiver && receiver.Effects) receiver.Effects.RemoveHealing(activation.Id);
        }
        internal bool Step()
        {
            if (registry.ServerTick >= activation.Expiry || attached && !cart) return false;
            if (!initialized) return true;
            RefreshPlacement();
            if (visual) visual.position = cart ? cart.VisualPose(new Pose(activation.Position, Quaternion.identity)).position : transform.position;
            if (cart && cartGeneration != cart.ContactGeneration)
            {
                cartGeneration = cart.ContactGeneration;
                registry.RefreshEffectPoses();
                Seed();
            }
            if (occupants.Count == 0 || registry.ServerTick < nextApplication) return true;
            nextApplication = registry.ServerTick + registry.DurationTicks(0.1f);
            stale.Clear();
            foreach (var receiver in occupants)
            {
                if (!receiver || !receiver.Eligible || definition.Application == PotionApplication.Zone &&
                    (receiver.Collider.ClosestPoint(transform.position) - transform.position).sqrMagnitude > definition.Radius * definition.Radius)
                    stale.Add(receiver);
                else receiver.Effects.SettleHealing();
            }
            foreach (var receiver in stale) Remove(receiver);
            return true;
        }
        private void OnDestroy()
        {
            GolfCartNetwork.LifetimeChanged -= CartChanged;
            foreach (var receiver in occupants) if (receiver && receiver.Effects) receiver.Effects.RemoveHealing(activation.Id);
            occupants.Clear();
            if (visual) Destroy(visual.gameObject);
        }
    }
}
