using UnityEngine;

namespace TwoBirds
{
    public struct PlayerControlTransition
    {
        public int Player, Cart;
        public sbyte Seat;
        public uint Revision, ControlRevision, Generation, EffectReset;
        public Vector3 Position, Velocity, Ejection;
        public Quaternion Rotation;
        public bool PlacementPending, ContextOnly, CarryPlacement;
        public ItemActionSnapshot ItemAction;
        public CarryRole Role;
        public int Partner;
        public float Immunity, Recovery;
    }
}
