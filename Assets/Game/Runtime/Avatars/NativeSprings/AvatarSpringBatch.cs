using System.Collections.Generic;
using UniGLTF.SpringBoneJobs.InputPorts;
using UnityEngine;
using UniVRM10.FastSpringBones;

namespace TwoBirds
{
    public static class AvatarSpringBatch
    {
        private static FastSpringBoneService service;
        private static FastSpringBoneService.UpdateTypes previousPolicy;
        private static bool ownsService;
        private static int users;
        private static readonly List<FastSpringBoneBuffer> retired = new();

        public static void Retain()
        {
            if (users++ != 0) return;
            service = Object.FindFirstObjectByType<FastSpringBoneService>();
            ownsService = !service;
            service = FastSpringBoneService.Instance;
            previousPolicy = service.UpdateType;
            service.UpdateType = FastSpringBoneService.UpdateTypes.Manual;
        }

        public static void Process(float deltaTime)
        {
            Flush();
            if (service.BufferCombiner.HasBuffer) service.ManualUpdate(deltaTime);
        }

        internal static void Retire(FastSpringBoneBuffer buffer)
        {
            if (!service) { buffer.Dispose(); return; }
            service.BufferCombiner.Register(null, buffer);
            retired.Add(buffer);
        }

        public static void Flush()
        {
            if (service) service.BufferCombiner.ReconstructIfDirty(default).Complete();
            // Reconstruction backs up departing buffers before removing them.
            foreach (var buffer in retired) buffer.Dispose();
            retired.Clear();
        }

        public static void Release()
        {
            if (--users != 0) return;
            Flush();
            if (!service) return;
            if (ownsService) FastSpringBoneService.Free();
            else service.UpdateType = previousPolicy;
            service = null;
        }
    }
}
