using UnityEngine;

namespace TwoBirds
{
    internal static class ItemReleaseVelocity
    {
        internal static Vector3 Movement(PlayerSeating seating, PlayerMotor motor) =>
            seating ? seating.PointVelocity : motor.Body.linearVelocity;
    }
}
