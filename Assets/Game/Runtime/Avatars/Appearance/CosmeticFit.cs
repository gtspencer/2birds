using System;
using UnityEngine;

namespace TwoBirds
{
    [Serializable]
    public struct CosmeticFit
    {
        public Vector3 Position, Rotation, Scale;
        public static CosmeticFit Identity => new() { Scale = Vector3.one };
        public void Apply(Transform target)
        {
            target.localPosition = Position;
            target.localRotation = Quaternion.Euler(Rotation);
            target.localScale = Scale;
        }
    }
}
