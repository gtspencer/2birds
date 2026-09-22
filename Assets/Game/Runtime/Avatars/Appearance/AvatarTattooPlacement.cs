using System;
using System.Collections.Generic;
using UnityEngine;

namespace TwoBirds
{
    [Serializable]
    public sealed class TattooCustomRegion
    {
        public uint Key;
        [AvatarBonePath] public string BonePath;
    }

    [Serializable]
    public sealed class TattooRegion
    {
        public uint Key;
        public HumanBodyBones Bone;
        public string BonePath;
        public Vector3 Center, Dimensions;
        public Quaternion Frame;
        public Vector3[] Surface;
        public TattooSurfaceIndices[] Meshes;
        public Transform Attachment(AvatarBinding binding) => Bone == HumanBodyBones.LastBone ?
            binding.Animator.transform.Find(BonePath) : binding.GetBone(Bone);
        public Vector3 Normalize(Vector3 bonePoint) => Divide(Quaternion.Inverse(Frame) * bonePoint - Center, Dimensions);
        public Vector3 Denormalize(Vector3 point) => Frame * (Center + Vector3.Scale(point, Dimensions));
        internal static Vector3 Divide(Vector3 a, Vector3 b) => new(a.x / b.x, a.y / b.y, a.z / b.z);
    }

    [Serializable]
    public sealed class TattooSurfaceIndices
    {
        public string RendererPath;
        public int[] Indices;
    }

    public static class AvatarTattooPlacement
    {
        public static uint Key(HumanBodyBones bone) => (uint)bone + 1;
        public static TattooRegion[] FirstPersonRegions(AvatarSettings settings)
        {
            var regions = new List<TattooRegion>();
            foreach (var surface in settings.FirstPersonTattooRegions)
            {
                var source = Array.Find(settings.TattooRegions, r => r.Key == surface.Key);
                if (source == null) continue;
                var points = new Vector3[surface.Surface.Length];
                for (int i = 0; i < points.Length; i++)
                    points[i] = TattooRegion.Divide(surface.Center + Vector3.Scale(surface.Surface[i], surface.Dimensions) -
                        source.Center, source.Dimensions);
                // Both frames express anatomical axes in their own skeleton's bone space.
                regions.Add(new TattooRegion { Key = source.Key, Bone = surface.Bone, BonePath = surface.BonePath,
                    Center = source.Center, Dimensions = source.Dimensions, Frame = surface.Frame,
                    Surface = points, Meshes = surface.Meshes });
            }
            return regions.ToArray();
        }
        public static void Tangents(Vector3 normal, out Vector3 right, out Vector3 up)
        {
            up = Vector3.ProjectOnPlane(Vector3.up, normal);
            if (up.sqrMagnitude < 0.001f) up = Vector3.ProjectOnPlane(Vector3.right, normal);
            up.Normalize(); right = Vector3.Cross(up, normal).normalized;
        }
        public static float PlaneSize(TattooRegion region, Vector3 normal)
        {
            Tangents(normal, out var right, out var up);
            return Mathf.Sqrt(Vector3.Scale(region.Dimensions, right).magnitude *
                Vector3.Scale(region.Dimensions, up).magnitude);
        }
        public static bool Resolve(TattooRegion[] regions, TattooAppearance tattoo, out TattooRegion region, out TattooAppearance resolved,
            float surfaceTolerance = 0.35f)
        {
            resolved = tattoo;
            region = Array.Find(regions ?? Array.Empty<TattooRegion>(), r => r.Key == tattoo.Region);
            if (region == null || !float.IsFinite(tattoo.Size) || tattoo.Size <= 0 || tattoo.Normal.sqrMagnitude < 0.5f) return false;
            Vector3 target = Vector3.Scale(tattoo.Position, region.Dimensions), best = default, normal = default;
            float distance = region.Dimensions.magnitude * surfaceTolerance;
            float bestDistance = distance * distance;
            bool found = false;
            for (int i = 0; i < region.Surface.Length; i += 3)
            {
                var a = Vector3.Scale(region.Surface[i], region.Dimensions);
                var b = Vector3.Scale(region.Surface[i + 1], region.Dimensions);
                var c = Vector3.Scale(region.Surface[i + 2], region.Dimensions);
                var n = Vector3.Cross(b - a, c - a).normalized;
                if (Vector3.Dot(n, tattoo.Normal) < 0.25f) continue;
                var point = Closest(target, a, b, c);
                float d = (point - target).sqrMagnitude;
                if (d > bestDistance) continue;
                bestDistance = d; best = point; normal = n; found = true;
            }
            if (!found) return false;
            resolved.Position = TattooRegion.Divide(best, region.Dimensions); resolved.Normal = normal;
            return true;
        }
        public static AvatarAppearance Transfer(AvatarAppearance appearance, AvatarRegistry.Entry destination)
        {
            var result = appearance.Clone(); result.Avatar = destination.Id;
            var list = new List<TattooAppearance>();
            foreach (var tattoo in result.Tattoos)
                if (Resolve(destination.Settings.TattooRegions, tattoo, out _, out var resolved)) list.Add(resolved);
            result.Tattoos = list.ToArray(); return result;
        }
        public static Vector3 Closest(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
        {
            var ab = b - a; var ac = c - a; var ap = p - a;
            float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
            if (d1 <= 0 && d2 <= 0) return a;
            var bp = p - b;
            float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
            if (d3 >= 0 && d4 <= d3) return b;
            float vc = d1 * d4 - d3 * d2;
            if (vc <= 0 && d1 >= 0 && d3 <= 0) return a + ab * (d1 / (d1 - d3));
            var cp = p - c;
            float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp);
            if (d6 >= 0 && d5 <= d6) return c;
            float vb = d5 * d2 - d1 * d6;
            if (vb <= 0 && d2 >= 0 && d6 <= 0) return a + ac * (d2 / (d2 - d6));
            float va = d3 * d6 - d5 * d4;
            if (va <= 0 && d4 >= d3 && d5 >= d6) return b + (c - b) * ((d4 - d3) / (d4 - d3 + d5 - d6));
            float inv = 1f / (va + vb + vc);
            return a + ab * (vb * inv) + ac * (vc * inv);
        }
        internal static bool RayTriangle(Ray ray, Vector3 a, Vector3 b, Vector3 c, out float distance)
        {
            distance = 0;
            var ab = b - a; var ac = c - a; var h = Vector3.Cross(ray.direction, ac);
            float det = Vector3.Dot(ab, h);
            if (det < 0.0000001f) return false;
            var s = ray.origin - a; float u = Vector3.Dot(s, h) / det;
            if (u < 0 || u > 1) return false;
            var q = Vector3.Cross(s, ab); float v = Vector3.Dot(ray.direction, q) / det;
            if (v < 0 || u + v > 1) return false;
            distance = Vector3.Dot(ac, q) / det; return distance >= 0;
        }
    }

    internal sealed class AvatarTattooSurface
    {
        private readonly List<(TattooRegion Region, Transform Bone, Vector3[] Points)> surfaces = new();
        public AvatarTattooSurface(AvatarBinding binding)
        {
            var cache = new Dictionary<string, Vector3[]>();
            foreach (var region in binding.Settings.TattooRegions)
            {
                var bone = region.Attachment(binding);
                if (!bone) continue;
                foreach (var source in region.Meshes)
                {
                    if (!cache.TryGetValue(source.RendererPath, out var vertices))
                    {
                        var node = string.IsNullOrEmpty(source.RendererPath) ? binding.Animator.transform : binding.Animator.transform.Find(source.RendererPath);
                        var skin = node ? node.GetComponent<SkinnedMeshRenderer>() : null;
                        if (!skin) continue;
                        var mesh = new Mesh(); skin.BakeMesh(mesh);
                        vertices = mesh.vertices;
                        for (int i = 0; i < vertices.Length; i++) vertices[i] = skin.transform.TransformPoint(vertices[i]);
                        UnityEngine.Object.Destroy(mesh); cache.Add(source.RendererPath, vertices);
                    }
                    var points = new Vector3[source.Indices.Length];
                    for (int i = 0; i < points.Length; i++) points[i] = bone.InverseTransformPoint(vertices[source.Indices[i]]);
                    surfaces.Add((region, bone, points));
                }
            }
        }
        public bool Cast(Ray ray, TattooAppearance value, out TattooAppearance result)
        {
            result = value; float nearest = float.PositiveInfinity; bool hit = false;
            foreach (var surface in surfaces)
                for (int i = 0; i < surface.Points.Length; i += 3)
                {
                    var a = surface.Bone.TransformPoint(surface.Points[i]);
                    var b = surface.Bone.TransformPoint(surface.Points[i + 1]);
                    var c = surface.Bone.TransformPoint(surface.Points[i + 2]);
                    if (!AvatarTattooPlacement.RayTriangle(ray, a, b, c, out float d) || d >= nearest) continue;
                    nearest = d; hit = true; result.Region = surface.Region.Key;
                    result.Position = surface.Region.Normalize(surface.Bone.InverseTransformPoint(ray.GetPoint(d)));
                    result.Normal = Quaternion.Inverse(surface.Region.Frame) *
                        surface.Bone.InverseTransformDirection(Vector3.Cross(b - a, c - a).normalized);
                }
            return hit;
        }
    }
}
