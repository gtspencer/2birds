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
        internal SplatEvent Event { get; private set; }
        internal event System.Action<SplatPresentation> Disposed;

        internal void Initialize(SplatDefinition style)
        {
            definition = style;
            projector = gameObject.AddComponent<DecalProjector>();
            projector.material = style.SplatMaterial;
            projector.scaleMode = DecalScaleMode.ScaleInvariant;
            projector.pivot = Vector3.zero;
            projector.enabled = false;
            SetSize(style.PopDuration > 0f ? 0.0001f : style.SplatSize);
            gameObject.SetActive(true);
            if (style.PopDuration > 0f)
                pop = LeanTween.value(gameObject, 0.0001f, style.SplatSize, style.PopDuration)
                    .setEase(LeanTweenType.easeOutElastic).setOnUpdate(SetSize).id;
            if (style.LifetimeBeforeShrinking <= 0f) Shrink();
            else delay = LeanTween.delayedCall(gameObject, style.LifetimeBeforeShrinking, Shrink).id;
        }

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

        private void Shrink()
        {
            delay = -1;
            if (disposed) return;
            if (pop >= 0) { LeanTween.cancel(pop); pop = -1; }
            if (definition.ShrinkDuration <= 0f) { Dispose(); return; }
            shrink = LeanTween.value(gameObject, projector.size.x, 0f, definition.ShrinkDuration)
                .setEase(LeanTweenType.easeInOutQuad).setOnUpdate(SetSize).setOnComplete(Dispose).id;
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
