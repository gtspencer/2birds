using UnityEngine;

namespace TwoBirds
{
    internal static class ItemReleaseClearance
    {
        private const float Padding = 0.01f, MaximumCorrection = 0.20f;

        internal static float EnvelopeRadius(Transform root, Vector3 worldScale, Collider[] colliders)
        {
            float radius = 0f;
            foreach (var collider in colliders)
            {
                if (collider.isTrigger) continue;
                Matrix4x4 matrix = Matrix4x4.Scale(worldScale) * root.worldToLocalMatrix * collider.transform.localToWorldMatrix;
                if (collider is SphereCollider sphere)
                {
                    float scale = Mathf.Max(matrix.MultiplyVector(Vector3.right).magnitude,
                        matrix.MultiplyVector(Vector3.up).magnitude, matrix.MultiplyVector(Vector3.forward).magnitude);
                    radius = Mathf.Max(radius, matrix.MultiplyPoint3x4(sphere.center).magnitude + sphere.radius * scale);
                }
                else if (collider is BoxCollider box)
                {
                    for (int corner = 0; corner < 8; corner++)
                    {
                        Vector3 sign = new((corner & 1) == 0 ? -1f : 1f, (corner & 2) == 0 ? -1f : 1f, (corner & 4) == 0 ? -1f : 1f);
                        radius = Mathf.Max(radius, matrix.MultiplyPoint3x4(box.center + Vector3.Scale(box.size * 0.5f, sign)).magnitude);
                    }
                }
                else radius = Mathf.Max(radius, (collider.bounds.center - root.position).magnitude + collider.bounds.extents.magnitude);
            }
            return radius;
        }

        internal static bool TryResolve(Pose desired, Vector3 reference, float envelopeRadius, Quaternion body,
            int environmentMask, Vector3 reachCenter, float reachRadius, out Pose allowed)
        {
            allowed = desired;
            float radius = envelopeRadius + Padding;
            Vector3 up = body * Vector3.up, forward = body * Vector3.forward, right = body * Vector3.right;
            Vector3 anchor = reference;
            bool clearAnchor = Clear(anchor, radius, environmentMask);
            for (int i = 0; !clearAnchor && i < 6; i++)
            {
                Vector3 axis = i % 3 == 0 ? up : i % 3 == 1 ? -up : -forward;
                anchor = reference + axis * (i < 3 ? 0.10f : 0.20f);
                clearAnchor = Clear(anchor, radius, environmentMask) &&
                    !Physics.Linecast(reference, anchor, environmentMask, QueryTriggerInteraction.Ignore);
            }
            if (!clearAnchor) return false;
            if (Reachable(desired.position) && Accessible(anchor, desired.position, radius, environmentMask)) return true;

            float nearest = float.PositiveInfinity;
            Vector3 best = default, route = desired.position - anchor;
            Consider(reachCenter + Vector3.ClampMagnitude(desired.position - reachCenter, reachRadius));
            if (route.sqrMagnitude > 0f && Physics.SphereCast(anchor, radius, route.normalized, out var hit,
                route.magnitude, environmentMask, QueryTriggerInteraction.Ignore))
                Consider(anchor + route.normalized * Mathf.Max(0f, hit.distance - Padding));
            for (int step = 0; step < 3; step++)
            {
                float distance = step == 0 ? 0.05f : step == 1 ? 0.10f : MaximumCorrection;
                for (int axis = 0; axis < 7; axis++)
                {
                    Vector3 direction = axis switch
                    {
                        0 => (reference - desired.position).normalized, 1 => up, 2 => -up,
                        3 => right, 4 => -right, 5 => forward, _ => -forward
                    };
                    Consider(desired.position + direction * distance);
                }
            }
            if (float.IsPositiveInfinity(nearest)) return false;
            allowed.position = best;
            return true;

            void Consider(Vector3 candidate)
            {
                float distance = (candidate - desired.position).sqrMagnitude;
                if (distance > MaximumCorrection * MaximumCorrection + 0.000001f || distance >= nearest || !Reachable(candidate) ||
                    !Accessible(anchor, candidate, radius, environmentMask)) return;
                nearest = distance;
                best = candidate;
            }
            bool Reachable(Vector3 candidate) => (candidate - reachCenter).sqrMagnitude <= reachRadius * reachRadius + 0.000001f;
        }

        private static bool Clear(Vector3 position, float radius, int mask) =>
            !Physics.CheckSphere(position, radius, mask, QueryTriggerInteraction.Ignore);

        private static bool Accessible(Vector3 anchor, Vector3 position, float radius, int mask)
        {
            if (!Clear(position, radius, mask)) return false;
            Vector3 delta = position - anchor;
            return delta.sqrMagnitude < 0.000001f ||
                !Physics.SphereCast(anchor, radius, delta.normalized, out _, delta.magnitude, mask, QueryTriggerInteraction.Ignore);
        }
    }
}
