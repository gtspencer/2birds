using System;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

namespace TwoBirds
{
    public struct ControlHint
    {
        public InputAction Action;
        public string Label;
    }

    internal sealed class ControlsHintPanel : IDisposable
    {
        private readonly VisualElement container;
        private readonly InputPresentation presentation;
        private ControlHint[] hints;
        private bool dirty = true;

        public ControlsHintPanel(VisualElement root, InputPresentation presentation)
        {
            container = root.Q("controls-hint");
            this.presentation = presentation;
            presentation.Changed += PresentationChanged;
        }

        public void Show(ControlHint[] newHints)
        {
            if (hints != newHints) { hints = newHints; dirty = true; }
            if (dirty) Rebuild();
            container.style.display = DisplayStyle.Flex;
        }

        public void Hide() => container.style.display = DisplayStyle.None;

        private void Rebuild()
        {
            dirty = false;
            container.Clear();
            if (hints == null) return;
            for (int i = 0; i < hints.Length; i++)
            {
                var hint = hints[i];
                var binding = presentation.Resolve(hint.Action);
                var row = new VisualElement();
                row.AddToClassList("controls-row");
                row.pickingMode = PickingMode.Ignore;

                var glyph = new Image { pickingMode = PickingMode.Ignore };
                glyph.AddToClassList("controls-glyph");
                row.Add(glyph);

                var keycap = new Label();
                keycap.AddToClassList("controls-keycap");
                keycap.pickingMode = PickingMode.Ignore;
                row.Add(keycap);

                var label = new Label(hint.Label);
                label.AddToClassList("controls-label");
                label.pickingMode = PickingMode.Ignore;
                row.Add(label);

                container.Add(row);
                glyph.image = binding.Glyph;
                glyph.style.display = binding.Glyph ? DisplayStyle.Flex : DisplayStyle.None;
                keycap.text = binding.Text;
                keycap.style.display = binding.Glyph ? DisplayStyle.None : DisplayStyle.Flex;
            }
        }

        private void PresentationChanged() => dirty = true;

        public void Dispose()
        {
            presentation.Changed -= PresentationChanged;
            Hide();
        }
    }
}
