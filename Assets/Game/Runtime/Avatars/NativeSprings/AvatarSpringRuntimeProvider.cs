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
        public IVrm10SpringBoneRuntime CreateSpringBoneRuntime() => new Runtime();

        private sealed class Runtime : IVrm10SpringBoneRuntime
        {
            private readonly FastSpringBoneService service = FastSpringBoneService.Instance;
            private Vrm10Instance instance;
            private FastSpringBoneBuffer buffer;
            private bool hasSprings;

            public async Task InitializeAsync(Vrm10Instance value, IAwaitCaller caller)
            {
                instance = value;
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
                if (service)
                {
                    service.BufferCombiner.Register(null, buffer);
                    // UniVRM backs up departing buffers during removal; keep them alive until then.
                    service.BufferCombiner.ReconstructIfDirty(default).Complete();
                }
                buffer.Dispose();
                buffer = null;
            }

            public void SetJointLevel(Transform joint, BlittableJointMutable settings) => service.BufferCombiner.Combined?.SetJointLevel(joint, settings);
            public void SetModelLevel(Transform root, BlittableModelLevel settings) => service.BufferCombiner.Combined?.SetModelLevel(root, settings);
            public void Process(float deltaTime) { }
            public void DrawGizmos() { }
        }
    }
}
