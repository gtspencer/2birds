using System;
using System.Threading.Tasks;
using UniGLTF;
using UniGLTF.SpringBoneJobs.Blittables;
using UniGLTF.SpringBoneJobs.InputPorts;
using UniVRM10;
using UniVRM10.FastSpringBones;
using UnityEngine;

namespace TwoBirds
{
    public sealed class AvatarSpringRuntimeProvider : MonoBehaviour, IVrm10SpringBoneRuntimeProvider
    {
        private Runtime runtime;
        public Task Initialization => runtime.Initialization;
        public IVrm10SpringBoneRuntime CreateSpringBoneRuntime() => runtime = new Runtime();

        public static void ValidateSpringReferences(Vrm10Instance instance)
        {
            foreach (var spring in instance.SpringBone.Springs)
            {
                if (spring == null || spring.Joints == null || spring.ColliderGroups == null)
                    throw new InvalidOperationException("Spring chain data is missing.");
                foreach (var joint in spring.Joints)
                    if (!joint) throw new InvalidOperationException($"Spring {spring.Name} contains a missing joint.");
                foreach (var group in spring.ColliderGroups)
                {
                    if (!group || group.Colliders == null)
                        throw new InvalidOperationException($"Spring {spring.Name} contains a missing collider group.");
                    foreach (var collider in group.Colliders)
                        if (!collider) throw new InvalidOperationException($"Spring {spring.Name}, group {group.name} contains a missing collider.");
                }
            }
        }

        private sealed class Runtime : IVrm10SpringBoneRuntime
        {
            private readonly FastSpringBoneService service = FastSpringBoneService.Instance;
            private Vrm10Instance instance;
            private FastSpringBoneBuffer buffer;
            private bool hasSprings;
            internal Task Initialization { get; private set; }

            public Task InitializeAsync(Vrm10Instance value, IAwaitCaller caller) => Initialization = Initialize(value, caller);

            private async Task Initialize(Vrm10Instance value, IAwaitCaller caller)
            {
                instance = value;
                ValidateSpringReferences(value);
                foreach (var spring in value.SpringBone.Springs) hasSprings |= spring.Joints.Count > 1;
                if (!hasSprings) return;
                buffer = await FastSpringBoneBufferFactory.ConstructSpringBoneAsync(caller, instance);
                service.BufferCombiner.Register(buffer, null);
            }

            public bool ReconstructSpringBone()
            {
                Dispose();
                if (!hasSprings) return true;
                buffer = FastSpringBoneBufferFactory.ConstructSpringBoneAsync(new ImmediateCaller(), instance).GetAwaiter().GetResult();
                service.BufferCombiner.Register(buffer, null);
                return true;
            }

            public void RestoreInitialTransform()
            {
                if (buffer == null) return;
                var pose = instance.Runtime.InitPose;
                foreach (var logic in buffer.Logics)
                {
                    var joint = buffer.Transforms[logic.headTransformIndex];
                    joint.localRotation = pose[joint].LocalRotation;
                }
                service.BufferCombiner.InitializeJointsLocalRotation(buffer);
            }

            public void Dispose()
            {
                if (buffer == null) return;
                AvatarSpringBatch.Retire(buffer);
                buffer = null;
            }

            public void SetJointLevel(Transform joint, BlittableJointMutable settings) => service.BufferCombiner.Combined?.SetJointLevel(joint, settings);
            public void SetModelLevel(Transform root, BlittableModelLevel settings) => service.BufferCombiner.Combined?.SetModelLevel(root, settings);
            public void Process(float deltaTime) { }
            public void DrawGizmos() { }
        }
    }
}
