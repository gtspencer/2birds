using UnityEngine;

namespace TwoBirds
{
    internal readonly struct ItemReleaseSphere
    {
        internal readonly Vector3 Center;
        internal readonly float Radius;
        internal ItemReleaseSphere(Vector3 center, float radius) { Center = center; Radius = radius; }
    }

    internal readonly struct ItemReleaseReach
    {
        private readonly Vector3 first, second;
        private readonly float firstRadius, secondRadius;
        private readonly bool dual, valid;
        internal ItemReleaseReach(Vector3 center, float radius)
        { first = second = center; firstRadius = secondRadius = radius; dual = false; valid = true; }
        internal ItemReleaseReach(Vector3 first, float firstRadius, Vector3 second, float secondRadius, bool valid)
        { this.first = first; this.second = second; this.firstRadius = firstRadius; this.secondRadius = secondRadius; dual = true; this.valid = valid; }
        internal bool Contains(Vector3 point) => valid &&
            (point - first).sqrMagnitude <= firstRadius * firstRadius * (1f + 1e-4f) &&
            (!dual || (point - second).sqrMagnitude <= secondRadius * secondRadius * (1f + 1e-4f));
        internal bool Dual => dual;

        internal bool TryProject(Vector3 point, Vector3 forward, out Vector3 result)
        {
            result = point;
            if (!valid) return false;
            if (Contains(point)) return true;
            result = first + Vector3.ClampMagnitude(point - first, firstRadius);
            if (Contains(result)) return true;
            result = second + Vector3.ClampMagnitude(point - second, secondRadius);
            if (Contains(result)) return true;
            Vector3 axis = second - first;
            float distance = axis.magnitude;
            if (!dual || distance < 0.000001f || distance > firstRadius + secondRadius) return false;
            axis /= distance;
            float along = (firstRadius * firstRadius - secondRadius * secondRadius + distance * distance) / (2f * distance);
            float radius = Mathf.Sqrt(Mathf.Max(0f, firstRadius * firstRadius - along * along));
            Vector3 center = first + axis * along;
            Vector3 radial = Vector3.ProjectOnPlane(point - center, axis);
            if (radial.sqrMagnitude < 0.000001f) radial = Vector3.ProjectOnPlane(forward, axis);
            if (radial.sqrMagnitude < 0.000001f) radial = Vector3.ProjectOnPlane(Vector3.up, axis);
            result = center + radial.normalized * radius;
            return Contains(result);
        }
    }

    internal static class ItemReleaseClearance
    {
        private const float Padding = 0.01f, MaximumCorrection = 0.20f;

        internal static ItemReleaseSphere Sphere(Transform root, Vector3 scale, Collider[] colliders, float envelope)
        {
            SphereCollider single = null;
            foreach (var collider in colliders)
            {
                if (collider.isTrigger) continue;
                if (single || collider is not SphereCollider) return new(Vector3.zero, envelope);
                single = (SphereCollider)collider;
            }
            if (!single) return new(Vector3.zero, envelope);
            Matrix4x4 matrix = Matrix4x4.Scale(scale) * root.worldToLocalMatrix * single.transform.localToWorldMatrix;
            float largest = Mathf.Max(matrix.MultiplyVector(Vector3.right).magnitude,
                matrix.MultiplyVector(Vector3.up).magnitude, matrix.MultiplyVector(Vector3.forward).magnitude);
            return new(matrix.MultiplyPoint3x4(single.center), single.radius * largest);
        }

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
            => TryResolve(desired, reference, new ItemReleaseSphere(Vector3.zero, envelopeRadius), body,
                environmentMask, new ItemReleaseReach(reachCenter, reachRadius), out allowed);

        internal static bool TryResolve(Pose desired, Vector3 reference, ItemReleaseSphere sphere, Quaternion body,
            int environmentMask, ItemReleaseReach? reach, out Pose allowed)
        {
            allowed = desired;
            Vector3 centerOffset = desired.rotation * sphere.Center;
            desired.position += centerOffset;
            float radius = sphere.Radius + Padding;
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
            if (reach.HasValue && reach.Value.TryProject(desired.position - centerOffset, forward, out var projected)) Consider(projected + centerOffset);
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
            allowed.position = best - centerOffset;
            return true;

            void Consider(Vector3 candidate)
            {
                if (reach.HasValue && reach.Value.Dual && !Reachable(candidate))
                {
                    if (!reach.Value.TryProject(candidate - centerOffset, forward, out var position)) return;
                    candidate = position + centerOffset;
                }
                float distance = (candidate - desired.position).sqrMagnitude;
                if (distance > MaximumCorrection * MaximumCorrection + 0.000001f || distance >= nearest || !Reachable(candidate) ||
                    !Accessible(anchor, candidate, radius, environmentMask)) return;
                nearest = distance;
                best = candidate;
            }
            bool Reachable(Vector3 candidate) => !reach.HasValue || reach.Value.Contains(candidate - centerOffset);
        }

        internal static bool TryDrop(Pose desired, Vector3 origin, ItemReleaseSphere sphere, Quaternion body, int mask, out Pose allowed)
        {
            sphere = new ItemReleaseSphere(sphere.Center, Mathf.Max(0.12f, sphere.Radius));
            Vector3 offset = desired.rotation * sphere.Center;
            Vector3 anchor = origin + offset;
            Vector3 route = desired.position - origin;
            if (Clear(anchor, sphere.Radius + Padding, mask) && route.sqrMagnitude > 0f &&
                Physics.SphereCast(anchor, sphere.Radius + Padding, route.normalized, out var hit, route.magnitude, mask, QueryTriggerInteraction.Ignore))
                desired.position = origin + route.normalized * Mathf.Max(0f, hit.distance - Padding);
            return TryResolve(desired, anchor, sphere, body, mask, null, out allowed);
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
