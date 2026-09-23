using UnityEngine;

namespace TwoBirds
{
    [System.Serializable]
    public struct ItemPalmContact
    {
        public Vector3 Position;
        public Vector3 Euler;
        public Quaternion Rotation => Quaternion.Euler(Euler);
    }
}
