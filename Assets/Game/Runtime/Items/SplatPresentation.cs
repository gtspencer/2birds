using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace TwoBirds
{
    public sealed class SplatPresentation : MonoBehaviour
    {
        private DecalProjector projector;
        private SplatDefinition definition;
        private SplatTargetLifetime owner;
        private int pop = -1, delay = -1, shrink = -1;
        private bool disposed;
        internal bool IsDisposed => disposed;
        internal SplatEvent Event { get; private set; }
        internal event System.Action<SplatPresentation> Disposed;

        internal void Initialize(SplatDefinition style, float age)
        {
            definition = style;
            projector = gameObject.AddComponent<DecalProjector>();
            projector.material = style.SplatMaterial;
            projector.scaleMode = DecalScaleMode.ScaleInvariant;
            projector.pivot = Vector3.zero;
            projector.enabled = false;
            SetSize(PopSize(Mathf.Min(age, style.LifetimeBeforeShrinking)));
            gameObject.SetActive(true);
            if (age >= style.LifetimeBeforeShrinking)
            {
                Shrink(age - style.LifetimeBeforeShrinking);
                return;
            }
            if (age < style.PopDuration)
                pop = LeanTween.value(gameObject, 0.0001f, style.SplatSize, style.PopDuration)
                    .setEase(LeanTweenType.easeOutElastic).setPassed(age).setOnUpdate(SetSize).id;
            delay = LeanTween.delayedCall(gameObject, style.LifetimeBeforeShrinking - age, Shrink).id;
        }

        private float PopSize(float age) => definition.PopDuration > 0f
            ? LeanTween.easeOutElastic(0.0001f, definition.SplatSize, Mathf.Clamp01(age / definition.PopDuration))
            : definition.SplatSize;

        internal void Place(SplatEvent value, SplatTargetLifetime lifetime, Transform target)
        {
            if (disposed) return;
            if (owner) owner.Remove(this);
            owner = lifetime;
            Event = value;
            projector.enabled = false;
            transform.SetParent(lifetime.transform, false);
            owner.Add(this);
            if (target) Attach(target);
        }

        internal void Attach(Transform target)
        {
            if (disposed || !target) return;
            transform.SetParent(target, false);
            transform.localPosition = Event.Point;
            transform.localRotation = Event.Rotation;
            transform.localScale = Vector3.one;
            gameObject.layer = target.gameObject.layer;
            projector.enabled = true;
        }

        private void SetSize(float size)
        {
            if (!disposed) projector.size = new Vector3(Mathf.Max(0.0001f, size), Mathf.Max(0.0001f, size), 0.05f);
        }

        private void Shrink() => Shrink(0f);

        private void Shrink(float elapsed)
        {
            delay = -1;
            if (disposed) return;
            if (pop >= 0) { LeanTween.cancel(pop); pop = -1; }
            if (definition.ShrinkDuration <= 0f) { Dispose(); return; }
            float size = projector.size.x;
            SetSize(LeanTween.easeInOutQuad(size, 0f, Mathf.Clamp01(elapsed / definition.ShrinkDuration)));
            shrink = LeanTween.value(gameObject, size, 0f, definition.ShrinkDuration)
                .setEase(LeanTweenType.easeInOutQuad).setPassed(elapsed).setOnUpdate(SetSize).setOnComplete(Dispose).id;
        }

        internal void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (pop >= 0) LeanTween.cancel(pop);
            if (delay >= 0) LeanTween.cancel(delay);
            if (shrink >= 0) LeanTween.cancel(shrink);
            if (projector) projector.enabled = false;
            if (owner) owner.Remove(this);
            Disposed?.Invoke(this);
            Destroy(gameObject);
        }

        private void OnDisable() => Dispose();
        private void OnDestroy() => Dispose();
    }
}
