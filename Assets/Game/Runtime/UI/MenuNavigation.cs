using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

namespace TwoBirds
{
    internal sealed class MenuNavigation : IDisposable
    {
        private readonly VisualElement root;
        private readonly VisualElement panelRoot;
        private readonly InputPresentation presentation;
        private readonly Func<VisualElement> scope;
        private readonly Func<VisualElement> initial;
        private readonly InputAction scroll;
        private readonly IVisualElementScheduledItem scrolling;
        private VisualElement remembered;
        private ScrollView scrollRegion;
        private Vector2 scrollInput;

        public MenuNavigation(VisualElement root, InputPresentation presentation, Func<VisualElement> scope, Func<VisualElement> initial)
        {
            this.root = root;
            panelRoot = root.panel.visualTree;
            this.presentation = presentation;
            this.scope = scope;
            this.initial = initial;
            root.RegisterCallback<FocusInEvent>(Focused);
            root.RegisterCallback<NavigationMoveEvent>(Move);
            root.RegisterCallback<PointerDownEvent>(MouseDown, TrickleDown.TrickleDown);
            root.RegisterCallback<NavigationSubmitEvent>(Guard, TrickleDown.TrickleDown);
            root.RegisterCallback<NavigationCancelEvent>(Guard, TrickleDown.TrickleDown);
            root.RegisterCallback<NavigationMoveEvent>(Guard, TrickleDown.TrickleDown);
            root.RegisterCallback<ClickEvent>(Guard, TrickleDown.TrickleDown);
            root.RegisterCallback<PointerDownEvent>(Guard, TrickleDown.TrickleDown);
            root.RegisterCallback<PointerUpEvent>(Guard, TrickleDown.TrickleDown);
            root.RegisterCallback<KeyDownEvent>(Guard, TrickleDown.TrickleDown);
            panelRoot.RegisterCallback<NavigationSubmitEvent>(GuardPanel, TrickleDown.TrickleDown);
            panelRoot.RegisterCallback<NavigationCancelEvent>(GuardPanel, TrickleDown.TrickleDown);
            panelRoot.RegisterCallback<NavigationMoveEvent>(GuardPanel, TrickleDown.TrickleDown);
            panelRoot.RegisterCallback<PointerDownEvent>(GuardPanel, TrickleDown.TrickleDown);
            panelRoot.RegisterCallback<PointerUpEvent>(GuardPanel, TrickleDown.TrickleDown);
            panelRoot.RegisterCallback<ClickEvent>(GuardPanel, TrickleDown.TrickleDown);
            panelRoot.RegisterCallback<KeyDownEvent>(GuardPanel, TrickleDown.TrickleDown);
            presentation.Changed += PresentationChanged;
            scroll = InputSystem.actions.FindAction("UI/Scroll");
            scroll.performed += ScrollChanged;
            scroll.canceled += ScrollChanged;
            scrolling = root.schedule.Execute(Scroll).Every(16);
            scrolling.Pause();
            PresentationChanged();
        }

        public static bool Eligible(VisualElement element)
        {
            if (element == null || element.panel == null || !element.enabledInHierarchy || !element.focusable) return false;
            for (var parent = element; parent != null; parent = parent.parent)
                if (parent.style.display == DisplayStyle.None || parent.resolvedStyle.display == DisplayStyle.None ||
                    parent.resolvedStyle.visibility == Visibility.Hidden) return false;
            return true;
        }

        private static bool Control(VisualElement element) => element is Button or TextField or DropdownField or Slider || element.ClassListContains("slot");
        private List<VisualElement> Controls(VisualElement area) => area?.Query<VisualElement>().Where(e => Control(e) && Eligible(e)).ToList() ?? new();

        public void Repair(VisualElement preferred = null)
        {
            var area = scope();
            var focused = root.panel?.focusController.focusedElement as VisualElement;
            if (area == null)
            {
                scrolling.Pause();
                if (focused != null && root.Contains(focused)) focused.Blur();
                return;
            }
            if (preferred == null && Eligible(focused) && (area.Contains(focused) || !root.Contains(focused))) return;
            if (!presentation.IsController) return;
            var target = Eligible(preferred) ? preferred : Eligible(remembered) && area.Contains(remembered) ? remembered : initial();
            if (!Eligible(target) || !area.Contains(target)) target = Controls(area).Find(Eligible);
            if (target == null && !Eligible(focused)) focused?.Blur();
            target?.Focus();
        }

        private void Focused(FocusInEvent evt)
        {
            if (evt.target is not VisualElement element) return;
            remembered = element;
            scrollRegion = element.GetFirstAncestorOfType<ScrollView>();
            scrollRegion?.ScrollTo(element);
            if (scrollRegion != null && !(element.layout.height > 0))
                element.schedule.Execute(() => { if (Eligible(element)) element.GetFirstAncestorOfType<ScrollView>()?.ScrollTo(element); });
        }

        private void PresentationChanged()
        {
            bool controller = presentation.IsController;
            root.EnableInClassList("controller-navigation", controller);
            if (!presentation.SuppressInput) Repair();
        }

        private void MouseDown(PointerDownEvent evt)
        {
            if (evt.pointerType != "mouse" || presentation.SuppressInput) return;
            var focused = root.panel?.focusController.focusedElement as VisualElement;
            if (focused != null && root.Contains(focused)) focused.Blur();
        }

        private void Guard(EventBase evt)
        {
            if (presentation.SuppressInput || SessionController.Instance && SessionController.Instance.ConsoleOpen) evt.StopImmediatePropagation();
        }

        private void GuardPanel(EventBase evt)
        {
            if (presentation.SuppressInput) evt.StopImmediatePropagation();
        }

        private void Move(NavigationMoveEvent evt)
        {
            var area = scope();
            if (area == null) return;
            var from = evt.target as VisualElement;
            while (from != null && !Control(from)) from = from.parent;
            if (from == null) { Repair(); evt.StopPropagation(); return; }
            var direction = evt.direction switch
            {
                NavigationMoveEvent.Direction.Up => Vector2.up * -1,
                NavigationMoveEvent.Direction.Down => Vector2.up,
                NavigationMoveEvent.Direction.Left => Vector2.left,
                NavigationMoveEvent.Direction.Right => Vector2.right,
                _ => Vector2.zero
            };
            if (direction == Vector2.zero) return;
            if (from is Slider slider && direction.x != 0)
            {
                slider.value = Mathf.Clamp(slider.value + direction.x * (slider.highValue - slider.lowValue) / 100f, slider.lowValue, slider.highValue);
                evt.StopPropagation(); return;
            }
            VisualElement nearest = null;
            float score = float.PositiveInfinity;
            foreach (var candidate in Controls(area))
            {
                if (candidate == from) continue;
                Vector2 delta = candidate.worldBound.center - from.worldBound.center;
                float forward = Vector2.Dot(delta, direction);
                if (forward <= 1f) continue;
                float side = Mathf.Abs(direction.x == 0 ? delta.x : delta.y);
                if (direction.x != 0 && side > Mathf.Max(from.worldBound.height, candidate.worldBound.height) * 0.5f) continue;
                float distance = forward + side * 4f;
                if (distance >= score) continue;
                nearest = candidate;
                score = distance;
            }
            nearest?.Focus();
            evt.StopPropagation();
        }

        private void ScrollChanged(InputAction.CallbackContext context)
        {
            scrollInput = context.ReadValue<Vector2>();
            if (scrollInput.sqrMagnitude < 0.01f || scope() == null || presentation.SuppressInput || ControlsRemapPanel.SuppressMenuInput)
                scrolling.Pause();
            else scrolling.Resume();
        }

        private void Scroll()
        {
            var area = scope();
            if (presentation.SuppressInput || ControlsRemapPanel.SuppressMenuInput || SessionController.Instance.ConsoleOpen || area == null ||
                scrollRegion == null || !area.Contains(scrollRegion)) { scrolling.Pause(); return; }
            float offset = Mathf.Clamp(scrollRegion.scrollOffset.y - scrollInput.y * 8f,
                scrollRegion.verticalScroller.lowValue, scrollRegion.verticalScroller.highValue);
            if (Mathf.Approximately(offset, scrollRegion.scrollOffset.y)) { scrolling.Pause(); return; }
            float delta = offset - scrollRegion.scrollOffset.y;
            var viewport = scrollRegion.contentViewport.worldBound;
            var focused = root.panel.focusController.focusedElement as VisualElement;
            scrollRegion.scrollOffset = new Vector2(scrollRegion.scrollOffset.x, offset);
            if (focused != null && viewport.Contains(focused.worldBound.center - new Vector2(0, delta))) return;
            VisualElement nearest = null;
            float distance = float.PositiveInfinity;
            foreach (var candidate in Controls(scrollRegion))
            {
                Vector2 position = candidate.worldBound.center - new Vector2(0, delta);
                if (!viewport.Contains(position)) continue;
                float next = Mathf.Abs(position.y - (delta > 0 ? viewport.yMin : viewport.yMax));
                if (next < distance) { nearest = candidate; distance = next; }
            }
            nearest?.Focus();
        }

        public void Dispose()
        {
            scrolling.Pause();
            scroll.performed -= ScrollChanged;
            scroll.canceled -= ScrollChanged;
            presentation.Changed -= PresentationChanged;
            root.UnregisterCallback<FocusInEvent>(Focused);
            root.UnregisterCallback<NavigationMoveEvent>(Move);
            root.UnregisterCallback<PointerDownEvent>(MouseDown, TrickleDown.TrickleDown);
            root.UnregisterCallback<NavigationSubmitEvent>(Guard, TrickleDown.TrickleDown);
            root.UnregisterCallback<NavigationCancelEvent>(Guard, TrickleDown.TrickleDown);
            root.UnregisterCallback<NavigationMoveEvent>(Guard, TrickleDown.TrickleDown);
            root.UnregisterCallback<ClickEvent>(Guard, TrickleDown.TrickleDown);
            root.UnregisterCallback<PointerDownEvent>(Guard, TrickleDown.TrickleDown);
            root.UnregisterCallback<PointerUpEvent>(Guard, TrickleDown.TrickleDown);
            root.UnregisterCallback<KeyDownEvent>(Guard, TrickleDown.TrickleDown);
            panelRoot.UnregisterCallback<NavigationSubmitEvent>(GuardPanel, TrickleDown.TrickleDown);
            panelRoot.UnregisterCallback<NavigationCancelEvent>(GuardPanel, TrickleDown.TrickleDown);
            panelRoot.UnregisterCallback<NavigationMoveEvent>(GuardPanel, TrickleDown.TrickleDown);
            panelRoot.UnregisterCallback<PointerDownEvent>(GuardPanel, TrickleDown.TrickleDown);
            panelRoot.UnregisterCallback<PointerUpEvent>(GuardPanel, TrickleDown.TrickleDown);
            panelRoot.UnregisterCallback<ClickEvent>(GuardPanel, TrickleDown.TrickleDown);
            panelRoot.UnregisterCallback<KeyDownEvent>(GuardPanel, TrickleDown.TrickleDown);
        }
    }
}
