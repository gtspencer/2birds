using FishNet.Component.Prediction;
using FishNet.Managing.Predicting;
using UnityEngine;

namespace TwoBirds
{
    [RequireComponent(typeof(Rigidbody), typeof(OfflineRigidbody))]
    public sealed class BirdBody : MonoBehaviour
    {
        [SerializeField] private Transform model;
        [SerializeField] private ParticleSystem feathers;
        [SerializeField] private SfxSource hitSound;
        private Rigidbody body;
        private OfflineRigidbody offlineBody;
        private Collider[] colliders;
        private Transform[] bones;
        private Transform[] parts;
        private Quaternion[] restRotations;
        private Vector3 originalScale;
        private float expires, shrinkSeconds;
        private bool sinking;
        private Vector3 previousPosition;
        internal ushort Species { get; private set; }
        internal uint Life { get; private set; }
        private void Awake()
        {
            body = GetComponent<Rigidbody>(); offlineBody = GetComponent<OfflineRigidbody>();
            colliders = GetComponentsInChildren<Collider>(true);
            bones = model ? model.GetComponentsInChildren<Transform>(true) : System.Array.Empty<Transform>();
            parts = GetComponentsInChildren<Transform>(true);
            restRotations = new Quaternion[bones.Length];
            for (int i = 0; i < bones.Length; i++) restRotations[i] = bones[i].localRotation;
            originalScale = transform.localScale;
            foreach (var animator in GetComponentsInChildren<Animator>(true)) animator.enabled = false;
        }
        internal void Rent(uint life, BirdSpecies species, BirdSettings settings, BirdView view, Vector3 position, PredictionManager prediction)
        {
            Life = life; Species = species.Id;
            gameObject.SetActive(true); transform.localScale = originalScale;
            transform.SetPositionAndRotation(position, view ? view.transform.rotation : Quaternion.identity);
            if (model)
            {
                for (int i = 0; i < bones.Length; i++) bones[i].localRotation = restRotations[i];
                model.localScale = species.ModelScale; model.localPosition = species.ModelOffset;
                if (view)
                    foreach (var bone in bones)
                        foreach (var source in view.Bones)
                            if (bone.name == source.name) { bone.localRotation = source.localRotation; break; }
            }
            int layer = LayerMask.NameToLayer("BirdBody");
            foreach (var part in parts) part.gameObject.layer = layer;
            body.excludeLayers = ~settings.SolidMask.value | LayerMask.GetMask("BirdBody", "BirdQuery", "Player", "PlayerItemHitbox", "GolfCart", "CartSeat", "ItemWorld", "ItemHeld");
            foreach (var collider in colliders) collider.enabled = true;
            body.position = position; body.rotation = transform.rotation; body.isKinematic = false;
            body.linearVelocity = Vector3.down * 0.2f; body.angularVelocity = Random.insideUnitSphere * 5f;
            body.WakeUp(); offlineBody.SetPredictionManager(prediction);
            expires = Time.time + settings.BodyLifetime; shrinkSeconds = settings.BodyShrinkSeconds; sinking = false;
            previousPosition = position;
            if (feathers) { feathers.Clear(true); feathers.Play(true); }
            if (hitSound) hitSound.Play(species.Sounds.Hit ? species.Sounds.Hit : settings.Sounds.Hit);
        }
        internal bool Step(BirdRegistry registry)
        {
            if (!sinking && !body.isKinematic && registry.WaterCrossing(previousPosition, body.position, out float surface))
            {
                sinking = true; body.isKinematic = true;
                transform.position = new Vector3(body.position.x, Mathf.Min(body.position.y, surface), body.position.z);
                foreach (var collider in colliders) collider.enabled = false;
            }
            if (sinking) transform.position += Vector3.down * Time.deltaTime;
            else if (!body.isKinematic && body.IsSleeping()) body.isKinematic = true;
            previousPosition = transform.position;
            float remaining = (expires + shrinkSeconds - Time.time) / shrinkSeconds;
            if (remaining < 1f) transform.localScale = originalScale * Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(remaining));
            return remaining <= 0f;
        }
        internal void Return()
        {
            offlineBody.SetPredictionManager(null);
            if (!body.isKinematic) { body.linearVelocity = Vector3.zero; body.angularVelocity = Vector3.zero; }
            body.isKinematic = true;
            if (feathers) { feathers.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear); }
            if (hitSound) hitSound.StopLoop();
            transform.localScale = originalScale; gameObject.SetActive(false);
        }
    }
}
