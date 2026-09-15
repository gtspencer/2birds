using System;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

namespace TwoBirds
{
    internal sealed class InteractionTooltip : IDisposable
    {
        private readonly VisualElement root, tooltip, chargeTrack;
        private readonly Image glyph;
        private readonly Label keycap, verb;
        private readonly SteamInputGlyphs glyphs = new();
        private InputAction displayedAction;
        private InputControl displayedControl;
        private bool bindingDirty = true;

        public InteractionTooltip(VisualElement root)
        {
            this.root = root;
            tooltip = root.Q("interaction-tooltip");
            chargeTrack = root.Q("charge-track");
            glyph = root.Q<Image>("interaction-glyph");
            keycap = root.Q<Label>("interaction-keycap");
            verb = root.Q<Label>("interaction-action");
            InputSystem.onActionChange += OnActionChange;
        }

        public void Update(PlayerInteraction interaction, InputDevice device)
        {
            glyphs.Update();
            if (interaction == null || !interaction.IsOwner || interaction.TargetCollider == null ||
                !interaction.TargetCollider.gameObject.activeInHierarchy || !interaction.Target.CanInteract)
            {
                Hide();
                return;
            }
            var action = interaction.Action;
            var control = FindControl(action, device);
            if (control == null) { Hide(); return; }
            if (bindingDirty || displayedAction != action || displayedControl != control)
            {
                displayedAction = action;
                displayedControl = control;
                bindingDirty = false;
                keycap.text = string.IsNullOrEmpty(control.shortDisplayName) ? control.displayName : control.shortDisplayName;
            }
            var texture = glyphs.Get(control);
            glyph.image = texture;
            glyph.style.display = texture != null ? DisplayStyle.Flex : DisplayStyle.None;
            keycap.style.display = texture == null ? DisplayStyle.Flex : DisplayStyle.None;
            verb.text = interaction.Target.ActionText;
            tooltip.style.display = DisplayStyle.Flex;
            var anchor = interaction.Target.TooltipAnchor;
            if (anchor != null)
                PositionAtPoint(interaction.ViewCamera, anchor.position);
            else
                Position(interaction.ViewCamera, interaction.TargetCollider.bounds);
        }

        private static InputControl FindControl(InputAction action, InputDevice device)
        {
            if (action == null || !action.enabled) return null;
            foreach (var control in action.controls)
            {
                if (device is Gamepad ? control.device == device : control.device is Keyboard || control.device is Mouse)
                    return control;
            }
            return null;
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

        private void OnActionChange(object obj, InputActionChange change)
        {
            if (change == InputActionChange.BoundControlsChanged) bindingDirty = true;
        }

        public void Dispose()
        {
            InputSystem.onActionChange -= OnActionChange;
            Hide();
            glyphs.Dispose();
        }
    }
}
