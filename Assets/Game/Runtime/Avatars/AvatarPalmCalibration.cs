using UnityEngine;

namespace TwoBirds
{
    [System.Serializable]
    public struct PalmCorrection
    {
        public Vector3 Position, Euler;
    }

    public static class AvatarPalmCalibration
    {
        public static AvatarSettings.GeneratedSkeleton Measurements(AvatarSettings settings, bool firstPerson = false)
        {
            var data = firstPerson ? settings.FirstPersonGenerated : settings.Generated;
            data.LeftWristToPalmPosition += data.LeftWristToPalmRotation * settings.LeftPalmCorrection.Position;
            data.LeftWristToPalmRotation *= Quaternion.Euler(settings.LeftPalmCorrection.Euler);
            data.RightWristToPalmPosition += data.RightWristToPalmRotation * settings.RightPalmCorrection.Position;
            data.RightWristToPalmRotation *= Quaternion.Euler(settings.RightPalmCorrection.Euler);
            return data;
        }
    }
}
