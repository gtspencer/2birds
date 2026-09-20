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
            service.BufferCombiner.ReconstructIfDirty(default).Complete();
            if (service.BufferCombiner.HasBuffer) service.ManualUpdate(deltaTime);
        }

        public static void Release()
        {
            if (--users != 0 || !service) return;
            service.BufferCombiner.ReconstructIfDirty(default).Complete();
            if (ownsService) FastSpringBoneService.Free();
            else service.UpdateType = previousPolicy;
            service = null;
        }
    }
}
