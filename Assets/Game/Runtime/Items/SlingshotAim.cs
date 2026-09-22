using UnityEngine;

namespace TwoBirds
{
    internal static class SlingshotAim
    {
        internal static Vector3 Direction(Pose aim, Vector3 origin, PlayerInventory shooter)
        {
            Vector3 forward = aim.rotation * Vector3.forward;
            float nearest = float.PositiveInfinity;
            Vector3 target = default;
            int mask = Physics.DefaultRaycastLayers & ~LayerMask.GetMask("ItemHeld", "PlayerEffectReceiver", "PotionEffect");
            foreach (var hit in Physics.RaycastAll(aim.position, forward, 2000f, mask, QueryTriggerInteraction.Ignore))
            {
                var collider = hit.collider;
                if (hit.distance >= nearest || collider == shooter.Hitbox.Collider ||
                    collider.transform.IsChildOf(shooter.transform) || collider.GetComponentInParent<PebbleProjectile>()) continue;
                if (collider.TryGetComponent<WorldItem>(out var item) && item.Record.State == WorldItemState.Held) continue;
                nearest = hit.distance; target = hit.point;
            }
            return float.IsPositiveInfinity(nearest) ? forward : (target - origin).normalized;
        }
    }
}
