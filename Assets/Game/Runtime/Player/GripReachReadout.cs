using UnityEngine;

namespace TwoBirds
{
    internal struct GripReachReadout
    {
        internal Pose RequestedRight, RequestedLeft, EvaluatedRight, EvaluatedLeft;
        internal bool HasLeft, ActiveBlend, ClearanceAdjusted, RightUnreachable, LeftUnreachable;
        internal float RightPositionError => Vector3.Distance(RequestedRight.position, EvaluatedRight.position);
        internal float LeftPositionError => Vector3.Distance(RequestedLeft.position, EvaluatedLeft.position);
        internal float RightAngleError => Quaternion.Angle(RequestedRight.rotation, EvaluatedRight.rotation);
        internal float LeftAngleError => Quaternion.Angle(RequestedLeft.rotation, EvaluatedLeft.rotation);
    }
}
