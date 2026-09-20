using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

namespace TwoBirds
{
    internal sealed class InputPrompt : VisualElement
    {
        private readonly InputPresentation presentation;
        private readonly InputAction action;
        private readonly float size;
        private readonly bool hideUnassigned;
        private List<(string Text, Texture2D Glyph)> tokens;

        public InputPrompt(InputPresentation presentation, InputAction action, float size = 24,
            bool hideUnassigned = false)
        {
            this.presentation = presentation;
            this.action = action;
            this.size = size;
            this.hideUnassigned = hideUnassigned;
            pickingMode = PickingMode.Ignore;
            style.flexDirection = FlexDirection.Row;
            style.alignItems = Align.Center;
            style.flexShrink = 0;
            RegisterCallback<AttachToPanelEvent>(_ => { presentation.Changed += Refresh; Refresh(); });
            RegisterCallback<DetachFromPanelEvent>(_ => presentation.Changed -= Refresh);
            Refresh();
        }

        private void Refresh()
        {
            var next = presentation.ResolveTokens(action);
            bool equal = tokens != null && tokens.Count == next.Count;
            for (int i = 0; equal && i < next.Count; i++) equal &= tokens[i] == next[i];
            if (equal) return;
            tokens = next;
            Clear();
            bool unassigned = tokens.Count == 1 && tokens[0].Text == "Unassigned";
            style.display = hideUnassigned && unassigned ? DisplayStyle.None : DisplayStyle.Flex;
            tooltip = string.Join(" / ", tokens.ConvertAll(token => token.Text));
            for (int i = 0; i < tokens.Count; i++)
            {
                if (i > 0) Add(new Label(" / ") { pickingMode = PickingMode.Ignore });
                var token = tokens[i];
                if (token.Glyph)
                {
                    var image = new Image { image = token.Glyph, scaleMode = ScaleMode.ScaleToFit, pickingMode = PickingMode.Ignore };
                    image.style.height = size;
                    image.style.width = Mathf.Clamp(size * token.Glyph.width / token.Glyph.height, size, size * 2.5f);
                    image.style.flexShrink = 0;
                    Add(image);
                }
                else Add(new Label(token.Text) { pickingMode = PickingMode.Ignore });
            }
        }

        public static void Begin(VisualElement container)
        {
            container.Clear();
            container.style.flexDirection = FlexDirection.Row;
            container.style.flexWrap = Wrap.Wrap;
            container.style.alignItems = Align.Center;
        }

        public static void AddAction(VisualElement container, InputPresentation presentation, string action, string label)
        {
            var row = new VisualElement { pickingMode = PickingMode.Ignore };
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.style.marginRight = 16;
            row.Add(new InputPrompt(presentation, InputSystem.actions.FindAction(action)));
            var text = new Label(label) { pickingMode = PickingMode.Ignore };
            text.style.marginLeft = 5;
            row.Add(text);
            container.Add(row);
        }
    }
}
