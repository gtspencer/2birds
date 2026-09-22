using UnityEngine;

namespace TwoBirds
{
    [DisallowMultipleComponent]
    public sealed class SlingshotPresentation : MonoBehaviour
    {
        public Transform LeftFork, RightFork, RestCenter, DrawCenter, LoadedPebble;
        public Vector3 PullingPalmEuler, PullingPalmOffset;
        public LineRenderer LeftBand, RightBand;
        private Vector3 left, right, rest, draw;
        private ItemActionState previous;
        private float lastDraw, cancelDraw;
        private double cancelledAt;
        public Vector3 Center { get; private set; }
        public bool Loaded { get; private set; }
        internal bool HasLoadedPose { get; private set; }
        internal Vector3 DepartureCenter { get; private set; }
        internal float LeftWeight { get; private set; }

        private void Awake()
        {
            left = transform.InverseTransformPoint(LeftFork.position);
            right = transform.InverseTransformPoint(RightFork.position);
            rest = transform.InverseTransformPoint(RestCenter.position);
            draw = transform.InverseTransformPoint(DrawCenter.position);
            LeftBand.useWorldSpace = RightBand.useWorldSpace = false;
            ResetPose();
        }

        internal Pose Evaluate(Pose frame, ItemActionState state, float charge, double age, in HeldItemBodyFrame body)
        {
            if (previous == ItemActionState.Charging && state == ItemActionState.Idle)
            { cancelledAt = Time.unscaledTimeAsDouble; cancelDraw = lastDraw; }
            float amount = state == ItemActionState.Charging ? charge : state == ItemActionState.Idle
                ? cancelDraw * (1f - Mathf.Clamp01((float)(Time.unscaledTimeAsDouble - cancelledAt) / 0.2f)) : 0f;
            previous = state; lastDraw = amount;
            Loaded = state == ItemActionState.Charging;
            LeftWeight = Loaded ? 1f : state == ItemActionState.Idle && cancelDraw > 0f ? amount / cancelDraw :
                state == ItemActionState.Recovering ? 1f - Mathf.Clamp01((float)age / 0.2f) : 0f;
            Quaternion palmRotation = frame.rotation * Quaternion.Euler(PullingPalmEuler);
            Vector3 local = Vector3.Lerp(rest, draw, amount);
            if (state == ItemActionState.Recovering)
                local += Vector3.up * (0.008f * Mathf.Sin((float)age * 85f) * Mathf.Exp(-(float)age * 22f));
            Vector3 scale = transform.lossyScale;
            Center = frame.position + frame.rotation * Vector3.Scale(local, scale);
            Vector3 shoulder = body.Shoulder + body.Rotation *
                ((body.Measurements.LeftShoulder - body.Measurements.RightShoulder) * body.Scale);
            Quaternion wrist = palmRotation * Quaternion.Inverse(body.Measurements.LeftWristToPalmRotation);
            Vector3 wristOffset = wrist * (body.Measurements.LeftWristToPalmPosition * body.Scale);
            Vector3 palmOffset = palmRotation * PullingPalmOffset;
            float reach = (body.Measurements.LeftArm.x + body.Measurements.LeftArm.y) * body.Scale * 0.85f;
            Vector3 wristPosition = Center + palmOffset - wristOffset;
            Center += shoulder + Vector3.ClampMagnitude(wristPosition - shoulder, reach) - wristPosition;
            LoadedPebble.position = Center;
            if (Loaded) { HasLoadedPose = true; DepartureCenter = Center; }
            LoadedPebble.gameObject.SetActive(Loaded);
            Band(LeftBand, frame.position + frame.rotation * Vector3.Scale(left, scale));
            Band(RightBand, frame.position + frame.rotation * Vector3.Scale(right, scale));
            return new Pose(Center + palmOffset, palmRotation);
        }

        private void Band(LineRenderer band, Vector3 fork)
        {
            band.SetPosition(0, band.transform.InverseTransformPoint(fork));
            band.SetPosition(1, band.transform.InverseTransformPoint(Center));
        }

        internal void CommitPalm(Pose palm)
        {
            if (!Loaded) return;
            Center = palm.position - palm.rotation * PullingPalmOffset;
            DepartureCenter = Center;
            LoadedPebble.position = Center;
            Band(LeftBand, LeftFork.position); Band(RightBand, RightFork.position);
        }

        internal void ResetPose()
        {
            previous = ItemActionState.Idle; lastDraw = cancelDraw = 0f; LeftWeight = 0f; Loaded = false;
            HasLoadedPose = false;
            LoadedPebble.gameObject.SetActive(false);
            Center = RestCenter.position;
            Band(LeftBand, LeftFork.position); Band(RightBand, RightFork.position);
        }
        private void OnDisable() => ResetPose();
    }
}
