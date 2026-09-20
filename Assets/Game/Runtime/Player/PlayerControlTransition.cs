using UnityEngine;

namespace TwoBirds
{
    public struct PlayerControlTransition
    {
        public int Player, Cart;
        public sbyte Seat;
        public uint Revision, ControlRevision, Generation;
        public Vector3 Position, Velocity, Ejection;
        public Quaternion Rotation;
        public bool PlacementPending, ContextOnly, CarryPlacement, ChargingUse;
        public CarryRole Role;
        public int Partner;
        public float Immunity, Recovery;
    }
}
