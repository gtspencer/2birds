using UnityEngine;

namespace TwoBirds
{
    [DisallowMultipleComponent]
    public sealed class SlingshotPresentation : MonoBehaviour
    {
        public Transform LeftFork, RightFork, RestCenter, LoadedPebble;
        public LineRenderer LeftBand, RightBand;
        private const int BandPoints = 9;
        private const float SettleSeconds = 0.35f;
        private Vector3 left, right, rest, releaseLocal;
        private Vector3 bandDown, bandWave;
        private ItemActionState previous;
        private float slack, wavePhase, recoilDraw;
        public Vector3 Center { get; private set; }
        public bool Loaded { get; private set; }
        internal bool HasLoadedPose { get; private set; }
        internal Vector3 DepartureCenter { get; private set; }

        private void Awake()
        {
            left = transform.InverseTransformPoint(LeftFork.position);
            right = transform.InverseTransformPoint(RightFork.position);
            rest = transform.InverseTransformPoint(RestCenter.position);
            LeftBand.useWorldSpace = RightBand.useWorldSpace = false;
            LeftBand.positionCount = RightBand.positionCount = BandPoints;
            ResetPose();
        }

        internal void Evaluate(Pose frame, ItemActionState state, double age, float draw, float attach, float recovery,
            Pose? palm, ItemPalmContact contact, Vector3 scale)
        {
            Vector3 restWorld = frame.position + frame.rotation * Vector3.Scale(rest, scale);
            Vector3 held = palm.HasValue
                ? Vector3.Lerp(restWorld, HeldItemPoseCalculation.ItemFromPalm(palm.Value, contact, scale).position, attach) : restWorld;
            if (state == ItemActionState.Recovering && previous != state)
            {
                recoilDraw = draw;
                Vector3 local = Quaternion.Inverse(frame.rotation) * ((previous == ItemActionState.Charging ? Center : held) - frame.position);
                releaseLocal = new Vector3(local.x / scale.x, local.y / scale.y, local.z / scale.z);
            }
            if (state == ItemActionState.Charging && previous != state) HasLoadedPose = false;
            previous = state;
            Loaded = state == ItemActionState.Charging;
            bandDown = frame.rotation * Vector3.down;
            bandWave = Vector3.zero;
            Center = held;
            slack = 1f - draw;
            if (state == ItemActionState.Recovering)
            {
                float elapsed = Mathf.Max(0f, (float)age);
                float envelope = 1f - Mathf.Clamp01(elapsed / SettleSeconds);
                envelope *= envelope;
                wavePhase = elapsed * (Mathf.PI * 8f / SettleSeconds);
                float recoil = 1f - Mathf.Clamp01(elapsed / 0.07f);
                Vector3 local = Vector3.Lerp(rest, releaseLocal, recoil);
                local += Vector3.up * ((releaseLocal - rest).magnitude * 0.08f * Mathf.Lerp(0.2f, 1f, recoilDraw) *
                    envelope * Mathf.Sin(wavePhase));
                bandWave = frame.rotation * Vector3.up * (Vector3.Scale(releaseLocal - rest, scale).magnitude *
                    0.12f * Mathf.Lerp(0.2f, 1f, recoilDraw) * envelope);
                Vector3 loose = frame.position + frame.rotation * Vector3.Scale(local, scale);
                Center = Vector3.Lerp(loose, held, Mathf.SmoothStep(0f, 1f,
                    Mathf.InverseLerp(SettleSeconds, Mathf.Max(SettleSeconds, recovery), elapsed)));
                slack = 1f - recoil * recoilDraw;
            }
            LoadedPebble.position = Center;
            if (Loaded) { HasLoadedPose = true; DepartureCenter = Center; }
            LoadedPebble.gameObject.SetActive(Loaded);
            Band(LeftBand, frame.position + frame.rotation * Vector3.Scale(left, scale), left);
            Band(RightBand, frame.position + frame.rotation * Vector3.Scale(right, scale), right);
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

        internal void Shift(Vector3 offset)
        {
            Center += offset;
            if (Loaded) DepartureCenter = Center;
            LoadedPebble.position = Center;
            Band(LeftBand, LeftFork.position, left); Band(RightBand, RightFork.position, right);
        }

        internal void ResetPose()
        {
            previous = ItemActionState.Idle; Loaded = false; recoilDraw = 0f;
            HasLoadedPose = false;
            bandDown = -transform.up; bandWave = Vector3.zero; slack = 1f; wavePhase = 0f;
            LoadedPebble.gameObject.SetActive(false);
            Center = RestCenter.position;
            DepartureCenter = Center;
            Band(LeftBand, LeftFork.position, left); Band(RightBand, RightFork.position, right);
        }
        private void OnDisable() => ResetPose();
    }
}
