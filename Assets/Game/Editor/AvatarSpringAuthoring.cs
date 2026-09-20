using System;
using System.Collections.Generic;
using UniVRM10;
using UnityEditor;
using UnityEngine;

namespace TwoBirds.Editor
{
    internal static class AvatarSpringAuthoring
    {
        internal static Transform Resolve(Transform root, string path)
        {
            if (string.IsNullOrEmpty(path)) throw new InvalidOperationException("Assign a bone.");
            var current = root;
            foreach (string name in path.Split('/'))
            {
                Transform found = null;
                foreach (Transform child in current)
                {
                    if (child.name != name) continue;
                    if (found) throw new InvalidOperationException($"Ambiguous bone path '{path}'; sibling names must be unique.");
                    found = child;
                }
                if (!found) throw new InvalidOperationException($"Bone '{path}' is missing. Reassign it if the skeleton was renamed.");
                current = found;
            }
            return current;
        }

        internal static string PathFor(AvatarSettings settings, Transform bone)
        {
            var source = settings.Generated.Source;
            var owner = bone.GetComponentInParent<Vrm10Instance>();
            if (!source || !owner || bone == owner.transform)
                throw new InvalidOperationException("Drag a bone from this avatar's source or processed prefab hierarchy.");
            var animator = owner.GetComponent<Animator>();
            var sourceAnimator = source.GetComponent<Animator>();
            if (!animator || !sourceAnimator || animator.avatar != sourceAnimator.avatar)
                throw new InvalidOperationException("The dropped bone belongs to a different avatar skeleton.");
            string path = AnimationUtility.CalculateTransformPath(bone, owner.transform);
            if (Resolve(owner.transform, path) != bone)
                throw new InvalidOperationException("Bone names must form an unambiguous hierarchy path.");
            Resolve(source.transform, path);
            return path;
        }

        internal static void Apply(Vrm10Instance vrm, AvatarSettings settings)
        {
            if (settings.AdditionalSprings.Count == 0) return;
            AvatarSpringRuntimeProvider.ValidateSpringReferences(vrm);
            var occupied = new HashSet<Transform>();
            foreach (var spring in vrm.SpringBone.Springs)
                foreach (var joint in spring.Joints) occupied.Add(joint.transform);

            foreach (var chain in settings.AdditionalSprings)
            {
                try
                {
                    if (!Nonnegative(chain.Stiffness) || !Nonnegative(chain.Drag) || chain.Drag > 1f ||
                        !Nonnegative(chain.GravityPower) || !Nonnegative(chain.JointRadius) || !Finite(chain.GravityDirection) ||
                        chain.GravityPower > 0f && chain.GravityDirection.sqrMagnitude < 0.000001f)
                        throw new InvalidOperationException("Spring tuning must be finite, nonnegative, and use drag between 0 and 1; gravity needs a direction.");
                    var root = Resolve(vrm.transform, chain.Root);
                    var tip = Resolve(vrm.transform, chain.Tip);
                    if (root == tip || !tip.IsChildOf(root))
                        throw new InvalidOperationException("Tip must be a descendant of Root, with at least two joints in the chain.");
                    var bones = new List<Transform>();
                    for (var bone = tip; bone != root; bone = bone.parent) bones.Add(bone);
                    bones.Add(root);
                    bones.Reverse();
                    foreach (var bone in bones)
                        if (!occupied.Add(bone))
                            throw new InvalidOperationException($"Bone '{bone.name}' already belongs to a source or additional spring. Remove the overlapping chain.");

                    var spring = new Vrm10InstanceSpringBone.Spring(chain.Name);
                    foreach (var bone in bones)
                    {
                        var joint = bone.GetComponent<VRM10SpringBoneJoint>();
                        if (!joint) joint = bone.gameObject.AddComponent<VRM10SpringBoneJoint>();
                        joint.m_stiffnessForce = chain.Stiffness;
                        joint.m_dragForce = chain.Drag;
                        joint.m_gravityPower = chain.GravityPower;
                        joint.m_gravityDir = chain.GravityDirection.normalized;
                        joint.m_jointRadius = chain.JointRadius;
                        spring.Joints.Add(joint);
                    }
                    if (chain.Colliders.Count > 0)
                    {
                        var group = vrm.gameObject.AddComponent<VRM10SpringBoneColliderGroup>();
                        group.Name = chain.Name;
                        foreach (var sphere in chain.Colliders)
                        {
                            if (!Nonnegative(sphere.Radius) || !Finite(sphere.Offset))
                                throw new InvalidOperationException("Collider radius must be finite and nonnegative, and its offset must be finite.");
                            var bone = Resolve(vrm.transform, sphere.Bone);
                            var collider = bone.gameObject.AddComponent<VRM10SpringBoneCollider>();
                            collider.ColliderType = VRM10SpringBoneColliderTypes.Sphere;
                            collider.Offset = sphere.Offset;
                            collider.Radius = sphere.Radius;
                            group.Colliders.Add(collider);
                        }
                        vrm.SpringBone.ColliderGroups.Add(group);
                        spring.ColliderGroups.Add(group);
                    }
                    vrm.SpringBone.Springs.Add(spring);
                }
                catch (InvalidOperationException exception)
                {
                    throw new InvalidOperationException($"Additional spring '{chain.Name}': {exception.Message}", exception);
                }
            }
        }

        private static bool Nonnegative(float value) => float.IsFinite(value) && value >= 0f;
        private static bool Finite(Vector3 value) => float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z);
    }
}
