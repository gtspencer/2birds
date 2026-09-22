using UnityEngine;

namespace TwoBirds
{
    [DisallowMultipleComponent]
    public sealed class SlingshotPresentation : MonoBehaviour
    {
        public Transform LeftFork, RightFork, RestCenter, DrawCenter, LoadedPebble;
        public Vector3 PullingPalmEuler, PullingPalmOffset;
        public LineRenderer LeftBand, RightBand;
        private const int BandPoints = 9;
        private const float SettleSeconds = 0.35f;
        private Vector3 left, right, rest, draw;
        private Vector3 bandDown, bandWave;
        private ItemActionState previous;
        private float lastDraw, cancelDraw, slack, wavePhase, returnPullWeight;
        private double cancelledAt, engagedAt = -1d;
        public Vector3 Center { get; private set; }
        public bool Loaded { get; private set; }
        internal bool HasLoadedPose { get; private set; }
        internal Vector3 DepartureCenter { get; private set; }
        internal float LeftWeight { get; private set; }
        internal Vector3 ForkMidpoint => Vector3.Scale((left + right) * 0.5f, transform.lossyScale);
        internal Vector3 Pouch(float amount, Vector3 drawOffset) =>
            Vector3.Scale(Vector3.Lerp(rest, draw + drawOffset, amount), transform.lossyScale);

        private void Awake()
        {
            left = transform.InverseTransformPoint(LeftFork.position);
            right = transform.InverseTransformPoint(RightFork.position);
            rest = transform.InverseTransformPoint(RestCenter.position);
            draw = transform.InverseTransformPoint(DrawCenter.position);
            LeftBand.useWorldSpace = RightBand.useWorldSpace = false;
            LeftBand.positionCount = RightBand.positionCount = BandPoints;
            ResetPose();
        }

        internal Pose Evaluate(Pose frame, ItemActionState state, float charge, double age, in HeldItemBodyFrame body, Vector3 drawOffset)
        {
            if (previous == ItemActionState.Charging && state != ItemActionState.Charging)
                returnPullWeight = LeftWeight;
            if (previous == ItemActionState.Charging && state == ItemActionState.Idle)
            { cancelledAt = Time.unscaledTimeAsDouble; cancelDraw = lastDraw; }
            if (state == ItemActionState.Charging && previous != state)
            { cancelDraw = 0f; engagedAt = -1d; HasLoadedPose = false; }
            float amount = state == ItemActionState.Charging ? charge : state == ItemActionState.Idle
                ? cancelDraw * (1f - Mathf.Clamp01((float)(Time.unscaledTimeAsDouble - cancelledAt) / 0.2f)) : 0f;
            previous = state; lastDraw = amount;
            Loaded = state == ItemActionState.Charging;
            LeftWeight = state == ItemActionState.Idle && cancelDraw > 0f ? returnPullWeight * amount / cancelDraw :
                state == ItemActionState.Recovering ? returnPullWeight * (1f - Mathf.Clamp01((float)age / 0.2f)) : 0f;
            Quaternion palmRotation = frame.rotation * Quaternion.Euler(PullingPalmEuler);
            Vector3 drawPosition = draw + drawOffset;
            Vector3 local = Vector3.Lerp(rest, drawPosition, amount);
            Vector3 scale = transform.lossyScale;
            bandDown = frame.rotation * Vector3.down;
            bandWave = Vector3.zero;
            slack = 1f - amount;
            if (state == ItemActionState.Recovering)
            {
                float elapsed = Mathf.Max(0f, (float)age);
                float envelope = 1f - Mathf.Clamp01(elapsed / SettleSeconds);
                envelope *= envelope;
                wavePhase = elapsed * (Mathf.PI * 8f / SettleSeconds);
                float recoil = charge * (1f - Mathf.Clamp01(elapsed / 0.07f));
                local = Vector3.Lerp(rest, drawPosition, recoil);
                local += Vector3.up * ((drawPosition - rest).magnitude * 0.08f * Mathf.Lerp(0.2f, 1f, charge) *
                    envelope * Mathf.Sin(wavePhase));
                bandWave = frame.rotation * Vector3.up * (Vector3.Scale(drawPosition - rest, scale).magnitude *
                    0.12f * Mathf.Lerp(0.2f, 1f, charge) * envelope);
                slack = 1f - recoil;
            }
            Center = frame.position + frame.rotation * Vector3.Scale(local, scale);
            Vector3 shoulder = body.Shoulder + body.Rotation *
                ((body.Measurements.LeftShoulder - body.Measurements.RightShoulder) * body.Scale);
            Quaternion wrist = palmRotation * Quaternion.Inverse(body.Measurements.LeftWristToPalmRotation);
            Vector3 wristOffset = wrist * (body.Measurements.LeftWristToPalmPosition * body.Scale);
            Vector3 palmOffset = palmRotation * PullingPalmOffset;
            float reach = (body.Measurements.LeftArm.x + body.Measurements.LeftArm.y) * body.Scale * 0.85f;
            Vector3 wristPosition = Center + palmOffset - wristOffset;
            if (Loaded)
            {
                if (Vector3.Distance(wristPosition, shoulder) > reach + 0.001f) engagedAt = -1d;
                else if (engagedAt < 0d) engagedAt = age;
                LeftWeight = engagedAt < 0d ? 0f : Mathf.SmoothStep(0f, 1f, (float)(age - engagedAt) / 0.1f);
            }
            if (!Loaded && LeftWeight > 0f)
                Center += (shoulder + Vector3.ClampMagnitude(wristPosition - shoulder, reach) - wristPosition) * LeftWeight;
            LoadedPebble.position = Center;
            if (Loaded) { HasLoadedPose = true; DepartureCenter = Center; }
            LoadedPebble.gameObject.SetActive(Loaded);
            Band(LeftBand, frame.position + frame.rotation * Vector3.Scale(left, scale), left);
            Band(RightBand, frame.position + frame.rotation * Vector3.Scale(right, scale), right);
            return new Pose(Center + palmOffset, palmRotation);
        }

        private void Band(LineRenderer band, Vector3 fork, Vector3 anchor)
        {
            Vector3 sag = bandDown * (Vector3.Scale(anchor - rest, transform.lossyScale).magnitude * 0.18f * slack);
            for (int i = 0; i < BandPoints; i++)
            {
                float t = i / (float)(BandPoints - 1);
                Vector3 point = Vector3.Lerp(fork, Center, t);
                if (i > 0 && i < BandPoints - 1)
                    point += 4f * t * (1f - t) * sag +
                        bandWave * (Mathf.Sin(Mathf.PI * t) * Mathf.Sin(wavePhase - t * Mathf.PI * 2f));
                band.SetPosition(i, band.transform.InverseTransformPoint(point));
            }
        }

        internal void CommitPalm(Pose palm)
        {
            if (!Loaded || LeftWeight < 1f) return;
            Center = palm.position - palm.rotation * PullingPalmOffset;
            DepartureCenter = Center;
            LoadedPebble.position = Center;
            Band(LeftBand, LeftFork.position, left); Band(RightBand, RightFork.position, right);
        }

        internal void ResetPose()
        {
            previous = ItemActionState.Idle; lastDraw = cancelDraw = 0f; LeftWeight = 0f; Loaded = false;
            HasLoadedPose = false;
            engagedAt = -1d; returnPullWeight = 0f;
            bandDown = -transform.up; bandWave = Vector3.zero; slack = 1f; wavePhase = 0f;
            LoadedPebble.gameObject.SetActive(false);
            Center = RestCenter.position;
            DepartureCenter = Center;
            Band(LeftBand, LeftFork.position, left); Band(RightBand, RightFork.position, right);
        }
        private void OnDisable() => ResetPose();
    }
}
