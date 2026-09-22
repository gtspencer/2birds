using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace TwoBirds.Editor
{
    public static class AvatarTattooRegionAuthoring
    {
        public const int Version = 1;
        public static TattooRegion[] Generate(GameObject root, AvatarSettings settings)
        {
            var animator = root.GetComponent<Animator>();
            var mapped = new Dictionary<Transform, (uint Key, HumanBodyBones Bone, string Path)>();
            for (int i = 0; i < (int)HumanBodyBones.LastBone; i++)
            {
                var bone = animator.GetBoneTransform((HumanBodyBones)i);
                if (bone) mapped[bone] = (AvatarTattooPlacement.Key((HumanBodyBones)i), (HumanBodyBones)i, "");
            }
            var customKeys = new HashSet<uint>();
            foreach (var custom in settings.CustomRegions)
            {
                var bone = root.transform.Find(custom.BonePath);
                if (!bone || custom.Key <= (uint)HumanBodyBones.LastBone || !customKeys.Add(custom.Key))
                    throw new System.InvalidOperationException("Custom tattoo regions need a valid bone and unique key above the humanoid range.");
                mapped[bone] = (custom.Key, HumanBodyBones.LastBone, custom.BonePath);
            }
            var points = new Dictionary<Transform, List<Vector3>>();
            var sources = new Dictionary<Transform, List<TattooSurfaceIndices>>();
            foreach (var skin in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (!skin.sharedMesh || !skin.enabled) continue;
                var baked = new Mesh(); skin.BakeMesh(baked);
                var vertices = baked.vertices; var triangles = baked.triangles;
                var weights = skin.sharedMesh.GetAllBoneWeights();
                var counts = skin.sharedMesh.GetBonesPerVertex();
                var offsets = new int[counts.Length];
                for (int i = 1; i < offsets.Length; i++) offsets[i] = offsets[i - 1] + counts[i - 1];
                var perBone = new Dictionary<Transform, List<int>>();
                for (int i = 0; i < triangles.Length; i += 3)
                {
                    var scores = new Dictionary<Transform, float>();
                    float unmapped = 0;
                    for (int corner = 0; corner < 3; corner++)
                    {
                        int vertex = triangles[i + corner];
                        for (int w = offsets[vertex]; w < offsets[vertex] + counts[vertex]; w++)
                        {
                            var influence = weights[w]; var bone = skin.bones[influence.boneIndex];
                            if (!bone || !mapped.ContainsKey(bone)) { unmapped += influence.weight; continue; }
                            scores.TryGetValue(bone, out float score); scores[bone] = score + influence.weight;
                        }
                    }
                    Transform winner = null; float maximum = unmapped;
                    foreach (var score in scores.OrderBy(pair => mapped[pair.Key].Key))
                        if (score.Value > maximum) { maximum = score.Value; winner = score.Key; }
                    if (!winner) continue;
                    if (!points.ContainsKey(winner)) { points[winner] = new(); sources[winner] = new(); }
                    if (!perBone.ContainsKey(winner)) perBone[winner] = new();
                    for (int j = 0; j < 3; j++)
                    {
                        int index = triangles[i + j]; perBone[winner].Add(index);
                        points[winner].Add(root.transform.InverseTransformPoint(skin.transform.TransformPoint(vertices[index])));
                    }
                }
                foreach (var source in perBone) sources[source.Key].Add(new TattooSurfaceIndices
                    { RendererPath = AnimationUtility.CalculateTransformPath(skin.transform, root.transform), Indices = source.Value.ToArray() });
                weights.Dispose(); counts.Dispose(); Object.DestroyImmediate(baked);
            }
            var result = new List<TattooRegion>();
            foreach (var pair in points)
            {
                var bone = pair.Key; var identity = mapped[bone];
                // Anatomical axes in the imported reference pose, independent of bone roll.
                var frame = Quaternion.Inverse(bone.rotation) * root.transform.rotation;
                var canonical = pair.Value.Select(p => Quaternion.Inverse(frame) * bone.InverseTransformPoint(root.transform.TransformPoint(p))).ToArray();
                var bounds = new Bounds(canonical[0], Vector3.zero);
                foreach (var p in canonical) bounds.Encapsulate(p);
                var dimensions = Vector3.Max(bounds.size, Vector3.one * 0.001f);
                result.Add(new TattooRegion { Key = identity.Key, Bone = identity.Bone, BonePath = identity.Path,
                    Center = bounds.center, Dimensions = dimensions, Frame = frame, Meshes = sources[bone].ToArray(),
                    Surface = canonical.Select(p => new Vector3((p.x - bounds.center.x) / dimensions.x,
                        (p.y - bounds.center.y) / dimensions.y, (p.z - bounds.center.z) / dimensions.z)).ToArray() });
            }
            return result.ToArray();
        }
    }
}
