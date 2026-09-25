using System;
using UnityEngine;

namespace TwoBirds
{
    [Serializable]
    public struct GripOffset
    {
        public Vector3 Position, Euler;
        public Pose Pose => new(Position, Quaternion.Euler(Euler));
        public bool IsZero => Position == Vector3.zero && Euler == Vector3.zero;
    }

    [Serializable]
    public struct AvatarGripOffset
    {
        public AvatarId Avatar;
        public GripOffset ThirdPerson, FirstPerson;
    }
}
