using UnityEngine;
using UnityEngine.UIElements;

namespace TwoBirds
{
    internal sealed class ReviveProgressWheel : VisualElement
    {
        private readonly Label seconds;
        private float progress;
        private int displayed = -1;
        internal ReviveProgressWheel()
        {
            pickingMode = PickingMode.Ignore;
            AddToClassList("revive-wheel");
            seconds = new Label();
            seconds.pickingMode = PickingMode.Ignore;
            Add(seconds);
            generateVisualContent += Draw;
        }
        internal void SetProgress(float fraction, float remaining)
        {
            float next = Mathf.Clamp01(fraction);
            if (next != progress) { progress = next; MarkDirtyRepaint(); }
            int second = Mathf.CeilToInt(remaining);
            if (second != displayed) { displayed = second; seconds.text = second.ToString(); }
        }
        private void Draw(MeshGenerationContext context)
        {
            var painter = context.painter2D;
            var center = contentRect.center;
            float radius = Mathf.Min(contentRect.width, contentRect.height) * 0.5f - 5f;
            painter.lineWidth = 6f;
            painter.strokeColor = new Color(0.11f, 0.15f, 0.2f, 0.9f);
            painter.BeginPath();
            painter.Arc(center, radius, 0f, 360f);
            painter.Stroke();
            if (progress <= 0f) return;
            painter.strokeColor = new Color(0.61f, 0.85f, 0.77f);
            painter.BeginPath();
            painter.Arc(center, radius, -90f, -90f + progress * 360f);
            painter.Stroke();
        }
    }
}
