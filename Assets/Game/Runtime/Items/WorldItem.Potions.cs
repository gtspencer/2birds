using UnityEngine;

namespace TwoBirds
{
    public sealed partial class WorldItem
    {
        private PotionPresentation potionPresentation;
        private PotionContactSensor potionSensor;
        private Collider potionTrigger;
        private Vector3 potionPhysicsStart;
        private bool potionSampled;

        internal void PotionReceiverContact(PlayerEffectReceiver receiver)
        {
            if (!registry || registry.Replaying || !Record.Armed || !ReleaseAvailable || !Simulating || !receiver.Eligible) return;
            if (receiver.Effects.ObjectId == Record.Releaser && ((long)registry.ServerTick - Record.LaunchTick) * registry.TickDelta < registry.ReleaseGrace) return;
            registry.QueuePotionImpact(this, receiver.Collider.ClosestPoint(BodySpherePosition));
        }
        private void PotionCollision(Collision collision)
        {
            if (!registry || registry.Replaying || !Record.Armed || !ReleaseAvailable || !Simulating) return;
            var receiver = collision.collider.GetComponentInParent<PlayerPotionEffects>();
            if (receiver && receiver.ObjectId == Record.Releaser && ((long)registry.ServerTick - Record.LaunchTick) * registry.TickDelta < registry.ReleaseGrace) return;
            var cart = collision.collider.GetComponentInParent<GolfCartNetwork>();
            registry.QueuePotionImpact(this, collision.GetContact(0).point, cart);
        }
        internal void BeforePotionPhysics()
        {
            potionSampled = Record.Armed && ReleaseAvailable && Simulating;
            if (potionSampled) potionPhysicsStart = BodySpherePosition;
        }
        internal void AfterPotionPhysics()
        {
            if (!potionSampled || !Record.Armed || !ReleaseAvailable || !Simulating) return;
            potionSampled = false;
            foreach (var collider in Physics.OverlapCapsule(potionPhysicsStart, BodySpherePosition, sphereRadius,
                LayerMask.GetMask("PlayerEffectReceiver"), QueryTriggerInteraction.Collide))
                if (collider.TryGetComponent<PlayerEffectReceiver>(out var receiver)) PotionReceiverContact(receiver);
        }
        internal void PresentOutput(Pose pose, bool available)
        {
            if (optimisticPickup || Record.State != WorldItemState.CauldronOutput) return;
            Body.position = pose.position; Body.rotation = pose.rotation;
            transform.SetPositionAndRotation(pose.position, pose.rotation);
            SetVisible(true);
            foreach (var collider in colliders) collider.enabled = available && !collider.isTrigger;
        }
    }
}
