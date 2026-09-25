using UnityEngine;

namespace TwoBirds
{
    public sealed partial class WorldItem
    {
        internal Transform SplatTransform => visualRoot;

        internal void ConsumeSplat()
        {
            var record = Record;
            record.SplatArmed = false;
            Record = record;
        }

        internal void SampleSplatRemoval(PlayerItemHitbox player, Vector3 point, Vector3 normal)
        {
            if (Simulating) return;
            UpdateIgnore();
            Vector3 velocity = incomingSampled ? incomingVelocity : Record.Motion.Velocity;
            playerContact.SampleTerminalContact(player, ContactFrame, point + normal * sphereRadius, velocity);
        }

        private void SplatCollision(Collision collision)
        {
            if (!registry || registry.Replaying || !Simulating || !ReleaseAvailable || !Record.SplatArmed || collision.contactCount == 0) return;
            UpdateIgnore();
            if (collision.collider == ignoredPlayer) return;
            var contact = collision.GetContact(0);
            registry.QueueSplat(this, collision.collider, contact.point, contact.normal);
        }
    }
}
