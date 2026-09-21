using System;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace TwoBirds.Editor
{
    internal static class AvatarArmMeshExtraction
    {
        internal static HashSet<Transform> ArmBones(Animator animator)
        {
            var bones = new HashSet<Transform>();
            foreach (var arm in new[] { HumanBodyBones.LeftUpperArm, HumanBodyBones.RightUpperArm })
                foreach (var bone in animator.GetBoneTransform(arm).GetComponentsInChildren<Transform>(true)) bones.Add(bone);
            return bones;
        }

        internal static Mesh Extract(SkinnedMeshRenderer renderer, HashSet<Transform> arms)
        {
            var source = renderer.sharedMesh;
            if (!source) return null;
            var bones = renderer.bones;
            using var counts = source.GetBonesPerVertex();
            using var weights = source.GetAllBoneWeights();
            var keep = new bool[source.vertexCount];
            var starts = new int[source.vertexCount];
            int cursor = 0;
            for (int vertex = 0; vertex < keep.Length; vertex++)
            {
                starts[vertex] = cursor;
                float armWeight = 0f, total = 0f;
                int count = counts.Length > vertex ? counts[vertex] : 0;
                for (int i = 0; i < count; i++)
                {
                    var weight = weights[cursor++];
                    total += weight.weight;
                    if (weight.boneIndex < bones.Length && arms.Contains(bones[weight.boneIndex])) armWeight += weight.weight;
                }
                keep[vertex] = total > 0f && armWeight > total * 0.5f;
            }
            var vertices = new List<int>();
            var remap = new int[source.vertexCount];
            Array.Fill(remap, -1);
            var triangles = new List<int>[source.subMeshCount];
            int Map(int old)
            {
                if (remap[old] >= 0) return remap[old];
                remap[old] = vertices.Count; vertices.Add(old); return remap[old];
            }
            for (int sub = 0; sub < triangles.Length; sub++)
            {
                var output = triangles[sub] = new List<int>();
                if (source.GetTopology(sub) != MeshTopology.Triangles) continue;
                var input = source.GetTriangles(sub);
                for (int i = 0; i < input.Length; i += 3)
                {
                    int a = input[i], b = input[i + 1], c = input[i + 2];
                    if (!keep[a] || !keep[b] || !keep[c]) continue;
                    output.Add(Map(a)); output.Add(Map(b)); output.Add(Map(c));
                }
            }
            if (vertices.Count == 0) return null;
            var mesh = new Mesh { name = source.name + " Arms", indexFormat = vertices.Count > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
            try
            {
                mesh.SetVertexBufferParams(vertices.Count, source.GetVertexAttributes());
                using (var data = Mesh.AcquireReadOnlyMeshData(source))
                {
                    for (int stream = 0; stream < source.vertexBufferCount; stream++)
                    {
                        int stride = source.GetVertexBufferStride(stream);
                        var input = data[0].GetVertexData<byte>(stream);
                        using var output = new NativeArray<byte>(vertices.Count * stride, Allocator.Temp);
                        for (int i = 0; i < vertices.Count; i++)
                            NativeArray<byte>.Copy(input, vertices[i] * stride, output, i * stride, stride);
                        mesh.SetVertexBufferData(output, 0, 0, output.Length, stream);
                    }
                }
                var countValues = new byte[vertices.Count];
                int weightCount = 0;
                foreach (int old in vertices) weightCount += counts[old];
                using var newWeights = new NativeArray<BoneWeight1>(weightCount, Allocator.Temp);
                cursor = 0;
                for (int i = 0; i < vertices.Count; i++)
                {
                    int old = vertices[i]; countValues[i] = counts[old];
                    NativeArray<BoneWeight1>.Copy(weights, starts[old], newWeights, cursor, counts[old]);
                    cursor += counts[old];
                }
                mesh.bindposes = source.bindposes;
                using var newCounts = new NativeArray<byte>(countValues, Allocator.Temp);
                mesh.SetBoneWeights(newCounts, newWeights);
                mesh.subMeshCount = triangles.Length;
                for (int i = 0; i < triangles.Length; i++) mesh.SetTriangles(triangles[i], i, false);
                var positions = new Vector3[source.vertexCount];
                var normals = new Vector3[source.vertexCount];
                var tangents = new Vector3[source.vertexCount];
                var compactPositions = new Vector3[vertices.Count];
                var compactNormals = new Vector3[vertices.Count];
                var compactTangents = new Vector3[vertices.Count];
                for (int shape = 0; shape < source.blendShapeCount; shape++)
                    for (int frame = 0; frame < source.GetBlendShapeFrameCount(shape); frame++)
                    {
                        source.GetBlendShapeFrameVertices(shape, frame, positions, normals, tangents);
                        for (int i = 0; i < vertices.Count; i++)
                        {
                            compactPositions[i] = positions[vertices[i]];
                            compactNormals[i] = normals[vertices[i]]; compactTangents[i] = tangents[vertices[i]];
                        }
                        mesh.AddBlendShapeFrame(source.GetBlendShapeName(shape), source.GetBlendShapeFrameWeight(shape, frame),
                            compactPositions, compactNormals, compactTangents);
                    }
                mesh.RecalculateBounds();
                return mesh;
            }
            catch { UnityEngine.Object.DestroyImmediate(mesh); throw; }
        }
    }
}
