using UnityEngine;

namespace TwoBirds
{
    public sealed class BirdView : MonoBehaviour
    {
        [SerializeField] private Transform model;
        [SerializeField] private Animator animator;
        [SerializeField] private SfxSource calls, wings;
        private readonly int[] states = new int[7];
        private Renderer[] renderers;
        private Transform[] bones;
        private BirdSounds sounds;
        private BirdActivity activity = (BirdActivity)255;
        private bool visible, animated, audible, wingLoop;
        private float nextCall, correctionUntil, lastAlert = float.NegativeInfinity;
        private Vector3 correction;
        public Transform[] Bones => bones;
        internal Vector3 Correction => correctionUntil > Time.unscaledTime ? correction * ((correctionUntil - Time.unscaledTime) / 0.1f) : Vector3.zero;

        private void Awake()
        {
            renderers = GetComponentsInChildren<Renderer>(true);
            bones = model ? model.GetComponentsInChildren<Transform>(true) : System.Array.Empty<Transform>();
            foreach (var collider in GetComponentsInChildren<Collider>(true)) collider.enabled = false;
            foreach (var body in GetComponentsInChildren<Rigidbody>(true)) body.isKinematic = true;
            if (animator) animator.applyRootMotion = false;
        }
        internal void Rent(BirdSpecies species, BirdSettings settings)
        {
            gameObject.SetActive(true);
            if (model) { model.localScale = species.ModelScale; model.localPosition = species.ModelOffset; }
            sounds = new BirdSounds { Ambient = species.Sounds.Ambient ? species.Sounds.Ambient : settings.Sounds.Ambient,
                Scare = species.Sounds.Scare ? species.Sounds.Scare : settings.Sounds.Scare,
                Wings = species.Sounds.Wings ? species.Sounds.Wings : settings.Sounds.Wings,
                Hit = species.Sounds.Hit ? species.Sounds.Hit : settings.Sounds.Hit };
            for (int i = 0; i < states.Length; i++)
            {
                string state = species.Animations.State((BirdActivity)i);
                if (string.IsNullOrEmpty(state)) state = settings.Animations.State((BirdActivity)i);
                states[i] = string.IsNullOrEmpty(state) ? 0 : Animator.StringToHash(state);
            }
            activity = (BirdActivity)255; correctionUntil = 0f;
            lastAlert = float.NegativeInfinity;
            nextCall = Time.time + Random.Range(3f, 9f);
            if (animator) { animator.enabled = true; animator.Rebind(); animator.applyRootMotion = false; }
            visible = animated = true;
            foreach (var renderer in renderers) renderer.enabled = true;
        }
        internal void Rebase(BirdRecord record, double tick, double delta)
        {
            Vector3 target = BirdMotion.Evaluate(BirdMotion.Current(record, tick), tick, delta).Position;
            correction = transform.position - target;
            correctionUntil = correction.sqrMagnitude <= 0.09f ? Time.unscaledTime + 0.1f : 0f;
        }
        internal void Present(BirdPose pose, bool show, bool animate, bool hear)
        {
            Vector3 offset = Correction;
            transform.SetPositionAndRotation(pose.Position + offset, pose.Rotation);
            if (visible != show)
            {
                visible = show;
                foreach (var renderer in renderers) renderer.enabled = show;
            }
            if (animated != animate)
            {
                animated = animate;
                if (animator) animator.enabled = animate;
                activity = (BirdActivity)255;
            }
            bool flying = pose.Activity is BirdActivity.Flight or BirdActivity.Takeoff or BirdActivity.Landing;
            if (wings && wingLoop != (hear && flying))
            {
                wingLoop = hear && flying;
                if (wingLoop) wings.SetLoop(sounds.Wings); else wings.StopLoop();
            }
            audible = hear;
            if (activity != pose.Activity)
            {
                activity = pose.Activity;
                int state = states[(int)activity];
                if (animator && animate && state != 0 && animator.HasState(0, state)) animator.CrossFade(state, 0.1f);
            }
            if (hear && calls && Time.time >= nextCall)
            {
                calls.Play(sounds.Ambient); nextCall = Time.time + Random.Range(5f, 15f);
            }
        }
        internal void Alert()
        {
            if (!audible || !calls || Time.time - lastAlert < 0.5f) return;
            lastAlert = Time.time; calls.Play(sounds.Scare);
        }
        internal void Return()
        {
            if (calls) calls.StopLoop(); if (wings) wings.StopLoop();
            wingLoop = audible = false; correctionUntil = 0f;
            gameObject.SetActive(false);
        }
    }
}
