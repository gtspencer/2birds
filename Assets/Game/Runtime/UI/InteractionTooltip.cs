using System;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

namespace TwoBirds
{
    internal sealed class InteractionTooltip : IDisposable
    {
        private readonly VisualElement root, tooltip, chargeTrack;
        private readonly VisualElement bindingContainer;
        private readonly Label verb, secondaryVerb;
        private readonly VisualElement secondaryRow, secondaryBinding;
        private InputAction displayedSecondary;
        private readonly InputPresentation presentation;
        private InputAction displayedAction;
        private bool bindingDirty = true;

        public InteractionTooltip(VisualElement root, InputPresentation presentation)
        {
            this.root = root;
            this.presentation = presentation;
            tooltip = root.Q("interaction-tooltip");
            chargeTrack = root.Q("charge-track");
            var glyph = root.Q<Image>("interaction-glyph");
            var keycap = root.Q<Label>("interaction-keycap");
            bindingContainer = new VisualElement();
            glyph.parent.Insert(glyph.parent.IndexOf(glyph), bindingContainer);
            glyph.RemoveFromHierarchy();
            keycap.RemoveFromHierarchy();
            verb = root.Q<Label>("interaction-action");
            secondaryRow = root.Q("secondary-interaction");
            secondaryBinding = root.Q("secondary-binding");
            secondaryVerb = root.Q<Label>("secondary-action");
            presentation.Changed += PresentationChanged;
        }

        public void Update(PlayerInteraction interaction)
        {
            if (interaction == null || !interaction.IsOwner || interaction.TargetCollider == null ||
                !interaction.TargetCollider.gameObject.activeInHierarchy || !interaction.Target.CanInteract &&
                !(interaction.Target is PlayerRevival body && body.Displayable))
            {
                Hide();
                return;
            }
            var secondary = interaction.SecondaryAction;
            secondaryRow.style.display = secondary != null && interaction.Target.CanSecondaryInteract ? DisplayStyle.Flex : DisplayStyle.None;
            if (secondary != null && (bindingDirty || displayedSecondary != secondary))
            {
                displayedSecondary = secondary;
                secondaryBinding.Clear();
                secondaryBinding.Add(new InputPrompt(presentation, secondary, 32));
            }
            secondaryVerb.text = interaction.Target.SecondaryActionText;
            var action = interaction.Action;
            if (bindingDirty || displayedAction != action)
            {
                displayedAction = action;
                bindingDirty = false;
                bindingContainer.Clear();
                bindingContainer.Add(new InputPrompt(presentation, action, 32));
            }
            verb.text = interaction.Target.HideTooltipText ? string.Empty :
                string.IsNullOrWhiteSpace(interaction.Target.TooltipTextOverride) ?
                interaction.Target.ActionText : interaction.Target.TooltipTextOverride;
            tooltip.style.display = DisplayStyle.Flex;
            var anchor = interaction.Target.TooltipAnchor;
            if (interaction.Target is PlayerRevival revive)
                PositionAtPoint(interaction.ViewCamera, revive.RootPosition);
            else if (interaction.Target is CartSeat seat)
                PositionAtPoint(interaction.ViewCamera, seat.VisualTooltipPosition);
            else if (anchor)
                PositionAtPoint(interaction.ViewCamera, anchor.position);
            else
                Position(interaction.ViewCamera, interaction.TargetCollider.bounds);
        }

        private void Position(Camera camera, Bounds bounds)
        {
            if (camera == null || root.panel == null) { Hide(); return; }
            var min = new Vector2(float.PositiveInfinity, float.PositiveInfinity);
            var max = new Vector2(float.NegativeInfinity, float.NegativeInfinity);
            for (int i = 0; i < 8; i++)
            {
                var corner = bounds.center + Vector3.Scale(bounds.extents,
                    new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                var screen = camera.WorldToScreenPoint(corner);
                if (screen.z < camera.nearClipPlane) continue;
                var point = root.WorldToLocal(RuntimePanelUtils.ScreenToPanel(root.panel, new Vector2(screen.x, Screen.height - screen.y)));
                min = Vector2.Min(min, point);
                max = Vector2.Max(max, point);
            }
            if (float.IsPositiveInfinity(min.x)) { Hide(); return; }
            var size = tooltip.layout.size;
            if (float.IsNaN(size.x) || size.x == 0) size = new Vector2(180, 44);
            var area = root.contentRect;
            tooltip.style.maxWidth = Mathf.Max(0, area.width - 16);
            float x = max.x + 12;
            if (x + size.x > area.xMax - 8) x = min.x - size.x - 12;
            x = Mathf.Clamp(x, area.xMin + 8, Mathf.Max(area.xMin + 8, area.xMax - size.x - 8));
            float y = Mathf.Clamp((min.y + max.y - size.y) * 0.5f, area.yMin + 8,
                Mathf.Max(area.yMin + 8, area.yMax - size.y - 8));
            if (chargeTrack != null && chargeTrack.style.display.value == DisplayStyle.Flex)
            {
                var chargeBounds = new Rect(area.center.x - 34, area.center.y + 10, 68, 13);
                if (new Rect(x, y, size.x, size.y).Overlaps(chargeBounds)) y = chargeBounds.yMax + 8;
            }
            tooltip.style.left = x;
            tooltip.style.top = y;
        }

        private void PositionAtPoint(Camera camera, Vector3 worldPoint)
        {
            if (camera == null || root.panel == null) { Hide(); return; }
            var screen = camera.WorldToScreenPoint(worldPoint);
            if (screen.z < camera.nearClipPlane) { Hide(); return; }
            var point = root.WorldToLocal(RuntimePanelUtils.ScreenToPanel(root.panel,
                new Vector2(screen.x, Screen.height - screen.y)));
            var size = tooltip.layout.size;
            if (float.IsNaN(size.x) || size.x == 0) size = new Vector2(180, 44);
            var area = root.contentRect;
            tooltip.style.maxWidth = Mathf.Max(0, area.width - 16);
            float x = point.x + 12;
            if (x + size.x > area.xMax - 8) x = point.x - size.x - 12;
            x = Mathf.Clamp(x, area.xMin + 8, Mathf.Max(area.xMin + 8, area.xMax - size.x - 8));
            float y = Mathf.Clamp(point.y - size.y * 0.5f, area.yMin + 8,
                Mathf.Max(area.yMin + 8, area.yMax - size.y - 8));
            if (chargeTrack != null && chargeTrack.style.display.value == DisplayStyle.Flex)
            {
                var chargeBounds = new Rect(area.center.x - 34, area.center.y + 10, 68, 13);
                if (new Rect(x, y, size.x, size.y).Overlaps(chargeBounds)) y = chargeBounds.yMax + 8;
            }
            tooltip.style.left = x;
            tooltip.style.top = y;
        }

        public void Hide() => tooltip.style.display = DisplayStyle.None;

        private void PresentationChanged() => bindingDirty = true;

        public void Dispose()
        {
            presentation.Changed -= PresentationChanged;
            Hide();
        }
    }
}
