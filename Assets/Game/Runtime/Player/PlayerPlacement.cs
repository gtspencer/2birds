using System.Collections.Generic;
using UnityEngine;

namespace TwoBirds
{
    public sealed class PlayerPlacement : MonoBehaviour
    {
        private CapsuleCollider capsule;
        private int clearanceMask, groundMask, reviveGroundMask;
        private readonly Collider[] query = new Collider[64];
        private void Awake()
        {
            capsule = GetComponent<CapsuleCollider>();
            clearanceMask = Physics.DefaultRaycastLayers & ~LayerMask.GetMask("CartSeat", "ItemHeld", "PlayerItemHitbox", "BirdBody", "BirdQuery", "PlayerRagdoll", "PlayerEffectReceiver");
            groundMask = clearanceMask & ~LayerMask.GetMask("Player", "GolfCart", "ItemWorld");
            reviveGroundMask = LayerMask.GetMask("Ground", "Environment");
        }
        internal bool TrySpawn(Vector3 spawn, out Vector3 position) => TryExitNear(spawn, null, out position);
        internal bool TryRevive(Vector3 root, Vector3 spawn, out Vector3 position)
        {
            for (int ring = 0; ring <= 4; ring++)
            {
                int steps = ring == 0 ? 1 : 16;
                for (int step = 0; step < steps; step++)
                {
                    float angle = step * Mathf.PI / 8f;
                    position = root + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * (ring * 0.5f);
                    if (!Physics.Raycast(position + Vector3.up * 1.5f, Vector3.down, out var ground, 4f, reviveGroundMask, QueryTriggerInteraction.Ignore)) continue;
                    position.y = ground.point.y + capsule.height * 0.5f - capsule.center.y + 0.05f;
                    if (CapsuleClear(position, null)) return true;
                }
            }
            return TrySpawn(spawn, out position);
        }
        internal bool TryExitNear(Vector3 desired, List<Vector3> reserved, out Vector3 position)
        {
            for (int pass = 0; pass < 5; pass++)
            {
                // Prefer ground, then clear air at increasing heights.
                float lift = pass < 2 ? 0f : Mathf.Pow(2f, pass - 2);
                for (int ring = 0; ring < 6; ring++)
                {
                    float radius = ring == 0 ? 0f : Mathf.Pow(2f, ring - 1);
                    int steps = ring == 0 ? 1 : 16;
                    for (int step = 0; step < steps; step++)
                    {
                        float angle = step * Mathf.PI / 8f;
                        position = desired + new Vector3(Mathf.Cos(angle) * radius, lift, Mathf.Sin(angle) * radius);
                        if (pass == 0)
                        {
                            if (!Physics.Raycast(position + Vector3.up * 1.5f, Vector3.down, out var ground, 6f,
                                groundMask, QueryTriggerInteraction.Ignore)) continue;
                            position.y = ground.point.y + capsule.height * 0.5f - capsule.center.y + 0.05f;
                        }
                        if (CapsuleClear(position, reserved)) return true;
                    }
                }
            }
            position = desired;
            return false;
        }

        internal bool CapsuleClear(Vector3 position, List<Vector3> reserved)
        {
            if (reserved != null)
                foreach (var other in reserved)
                    if ((position - other).sqrMagnitude < capsule.height * capsule.height) return false;
            CapsulePoints(position, out var bottom, out var top);
            int count = Physics.OverlapCapsuleNonAlloc(bottom, top, capsule.radius + 0.03f, query, clearanceMask, QueryTriggerInteraction.Ignore);
            if (count == query.Length) return false;
            for (int i = 0; i < count; i++) if (query[i] != capsule && !query[i].transform.IsChildOf(transform)) return false;
            return true;
        }
        internal void CapsulePoints(Vector3 position, out Vector3 bottom, out Vector3 top)
        {
            Vector3 center = position + capsule.center;
            float half = Mathf.Max(0f, capsule.height * 0.5f - capsule.radius);
            bottom = center - Vector3.up * half;
            top = center + Vector3.up * half;
        }

    }
}
