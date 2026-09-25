using UnityEngine;
using UnityEngine.UIElements;

namespace TwoBirds
{
    internal sealed class EmoteWheel : VisualElement
    {
        private static readonly Color SliceColor = new(12f / 255f, 16f / 255f, 24f / 255f, 0.82f);
        private static readonly Color HighlightColor = new(58f / 255f, 80f / 255f, 96f / 255f);
        private static readonly Color AccentColor = new(155f / 255f, 218f / 255f, 196f / 255f);
        private readonly VisualElement[] slots = new VisualElement[EmoteCatalog.Capacity];
        private int highlight = -1;
        private Vector2 pointer;
        private bool showPointer;

        internal EmoteWheel(EmoteCatalog catalog)
        {
            pickingMode = PickingMode.Ignore;
            AddToClassList("emote-wheel");
            float r = (1f + EmoteWheelSelection.DeadZone) * 0.25f;
            for (int i = 0; i < EmoteCatalog.Capacity; i++)
            {
                var definition = catalog ? catalog.Get(i) : null;
                if (!definition) continue;
                var slot = slots[i] = new VisualElement { pickingMode = PickingMode.Ignore };
                slot.AddToClassList("emote-wheel-slot");
                if (definition.Icon) slot.Add(new Image { sprite = definition.Icon, pickingMode = PickingMode.Ignore });
                slot.Add(new Label(definition.DisplayName) { pickingMode = PickingMode.Ignore });
                float a = i * 45f * Mathf.Deg2Rad;
                slot.style.left = Length.Percent(50f + Mathf.Sin(a) * r * 100f);
                slot.style.top = Length.Percent(50f - Mathf.Cos(a) * r * 100f);
                slot.style.translate = new Translate(Length.Percent(-50f), Length.Percent(-50f));
                Add(slot);
            }
            generateVisualContent += Draw;
        }

        internal void Set(int value, Vector2 position, bool visible)
        {
            if (value == highlight && position == pointer && visible == showPointer) return;
            if (value != highlight)
            {
                if (highlight >= 0) slots[highlight]?.RemoveFromClassList("highlighted");
                if (value >= 0) slots[value]?.AddToClassList("highlighted");
            }
            highlight = value; pointer = position; showPointer = visible;
            MarkDirtyRepaint();
        }

        private void Draw(MeshGenerationContext context)
        {
            var painter = context.painter2D;
            var center = contentRect.center;
            float outer = Mathf.Min(contentRect.width, contentRect.height) * 0.5f - 2f;
            float inner = outer * EmoteWheelSelection.DeadZone;
            for (int i = 0; i < slots.Length; i++)
            {
                if (slots[i] == null) continue;
                float c = -90f + 45f * i;
                painter.fillColor = i == highlight ? HighlightColor : SliceColor;
                painter.BeginPath();
                painter.Arc(center, outer, c - 21f, c + 21f);
                painter.Arc(center, inner, c + 21f, c - 21f, ArcDirection.CounterClockwise);
                painter.ClosePath();
                painter.Fill();
                if (i != highlight) continue;
                painter.strokeColor = AccentColor;
                painter.lineWidth = 3f;
                painter.Stroke();
            }
            if (!showPointer) return;
            painter.fillColor = AccentColor;
            painter.BeginPath();
            painter.Arc(center + new Vector2(pointer.x, -pointer.y) * outer, 5f, 0f, 360f);
            painter.Fill();
        }
    }
}
