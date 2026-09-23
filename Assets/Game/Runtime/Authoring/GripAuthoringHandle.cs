#if UNITY_EDITOR || DEVELOPMENT_BUILD
using UnityEngine;

namespace TwoBirds
{
    public struct GripAuthoringHandle
    {
        public string PositionField, EulerField;
        public Pose Basis, Local;
        public Vector3 Scale;
        public Pose World => new(Basis.position + Basis.rotation * Vector3.Scale(Scale, Local.position), Basis.rotation * Local.rotation);
        public void Apply(GripAuthoringDraft record, Pose world)
        {
            Vector3 point = Quaternion.Inverse(Basis.rotation) * (world.position - Basis.position);
            GripAuthoringFields.Set(record.Values, PositionField, new Vector3(point.x / Scale.x, point.y / Scale.y, point.z / Scale.z));
            if (!string.IsNullOrEmpty(EulerField)) GripAuthoringFields.Set(record.Values, EulerField, (Quaternion.Inverse(Basis.rotation) * world.rotation).eulerAngles);
        }
    }
}
#endif
