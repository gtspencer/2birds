using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.UIElements;

namespace TwoBirds
{
    internal sealed class ControlsRemapPanel : IDisposable
    {
        private enum CaptureState { Idle, Waiting, Listening, ConflictRelease, Conflict, Finishing }
        private static ControlsRemapPanel capturing;
        private static int suppressThroughFrame = -1;
        public static bool SuppressMenuInput => capturing != null || Time.frameCount <= suppressThroughFrame;
        private readonly VisualElement root, options, feedback, tabs;
        private readonly ScrollView list;
        private readonly DropdownField device;
        private readonly Label prompt;
        private readonly Button cancel, accept, restore, back;
        private readonly InputBindings bindings;
        private readonly InputPresentation presentation;
        private readonly List<(InputBindings.Entry Entry, Button Button, Label Text, Image Glyph)> rows = new();
        private readonly List<InputAction> enabledUi = new();
        private readonly InputActionMap ui;
        private InputActionRebindingExtensions.RebindingOperation operation;
        private IVisualElementScheduledItem tick;
        private InputBindings.Entry target;
        private Button returnFocus;
        private CaptureState state;
        private string group = InputBindings.KeyboardMouse, candidate;
        private string conflictNames;
        private bool targetEnabled, vectorBinding;
        private float deadline;
        private int releaseFrame;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetCaptureState()
        {
            capturing = null;
            suppressThroughFrame = -1;
        }

        public ControlsRemapPanel(VisualElement root, InputBindings bindings, InputPresentation presentation)
        {
            this.root = root;
            this.bindings = bindings;
            this.presentation = presentation;
            options = root.Q("controls-options");
            tabs = root.Q("settings-tabs");
            back = root.Q<Button>("settings-back");
            list = root.Q<ScrollView>("binding-list");
            device = root.Q<DropdownField>("controls-device");
            feedback = root.Q("binding-feedback");
            prompt = root.Q<Label>("binding-prompt");
            cancel = root.Q<Button>("binding-cancel");
            accept = root.Q<Button>("binding-accept");
            restore = root.Q<Button>("bindings-restore");
            device.choices = new List<string> { "Keyboard & Mouse", "Controller" };
            device.SetValueWithoutNotify(device.choices[0]);
            device.RegisterValueChangedCallback(DeviceChanged);
            cancel.clicked += Cancel;
            accept.clicked += Accept;
            restore.clicked += Restore;
            ui = InputSystem.actions.FindActionMap("UI");
            presentation.Changed += Refresh;
            presentation.Interrupted += Interrupted;
            Application.focusChanged += FocusChanged;
            root.RegisterCallback<NavigationSubmitEvent>(GuardSubmit, TrickleDown.TrickleDown);
            root.RegisterCallback<NavigationMoveEvent>(GuardMove, TrickleDown.TrickleDown);
            root.RegisterCallback<NavigationCancelEvent>(GuardCancel, TrickleDown.TrickleDown);
            root.RegisterCallback<KeyDownEvent>(GuardKey, TrickleDown.TrickleDown);
            root.RegisterCallback<PointerDownEvent>(GuardPointer, TrickleDown.TrickleDown);
            root.RegisterCallback<ClickEvent>(GuardClick, TrickleDown.TrickleDown);
            Build();
        }

        private void DeviceChanged(ChangeEvent<string> evt)
        {
            group = device.index == 1 ? InputBindings.Controller : InputBindings.KeyboardMouse;
            Build();
        }

        private void Build()
        {
            rows.Clear();
            list.Clear();
            foreach (var entry in bindings.Entries)
            {
                if (entry.Group != group) continue;
                var row = new VisualElement();
                row.AddToClassList("binding-row");
                var name = new Label(entry.Name);
                name.AddToClassList("binding-name");
                var button = new Button();
                button.AddToClassList("binding-button");
                var glyph = new Image { pickingMode = PickingMode.Ignore };
                glyph.AddToClassList("binding-glyph");
                var text = new Label { pickingMode = PickingMode.Ignore };
                button.Add(glyph);
                button.Add(text);
                button.clicked += () => Begin(entry, button);
                var reset = new Button(() => bindings.Reset(entry)) { text = "Reset" };
                reset.AddToClassList("binding-reset");
                row.Add(name);
                row.Add(button);
                row.Add(reset);
                list.Add(row);
                rows.Add((entry, button, text, glyph));
                button.RegisterCallback<FocusInEvent>(_ => list.ScrollTo(row));
                reset.RegisterCallback<FocusInEvent>(_ => list.ScrollTo(row));
            }
            Refresh();
        }

        private void Refresh()
        {
            cancel.text = $"Cancel · {CancelHint}";
            RefreshPrompt();
            foreach (var row in rows)
            {
                var value = presentation.Resolve(row.Entry.Action, group, row.Entry.Id);
                row.Text.text = value.Text;
                row.Button.tooltip = $"{row.Entry.Name}: {value.Text}";
                row.Glyph.image = value.Glyph;
                row.Glyph.style.display = value.Glyph ? DisplayStyle.Flex : DisplayStyle.None;
            }
        }

        private void Restore() => bindings.Reset(group);

        private void Begin(InputBindings.Entry entry, Button button)
        {
            if (state != CaptureState.Idle) return;
            capturing = this;
            target = entry;
            vectorBinding = !entry.Action.bindings[entry.Index].isPartOfComposite && entry.Action.expectedControlType == "Vector2";
            returnFocus = button;
            targetEnabled = entry.Action.enabled;
            entry.Action.Disable();
            enabledUi.Clear();
            foreach (string name in new[] { "Navigate", "Submit", "Cancel", "Pause", "Scroll" })
            {
                var action = ui.FindAction(name);
                if (action.enabled) enabledUi.Add(action);
                action.Disable();
            }
            options.SetEnabled(false);
            tabs.SetEnabled(false);
            back.SetEnabled(false);
            feedback.style.display = DisplayStyle.Flex;
            accept.style.display = DisplayStyle.None;
            state = CaptureState.Waiting;
            RefreshPrompt();
            deadline = Time.realtimeSinceStartup + 15f;
            releaseFrame = Time.frameCount + 1;
            InputSystem.onEvent += CaptureCancel;
            tick = root.schedule.Execute(Tick).Every(30);
            cancel.Focus();
        }

        private static bool ControlsHeld()
        {
            foreach (var device in InputSystem.devices)
            {
                if (device is not Keyboard && device is not Mouse && !InputPresentation.IsControllerDevice(device)) continue;
                foreach (var control in device.allControls)
                {
                    if (control is ButtonControl button && button.isPressed && control.parent is not StickControl)
                        return true;
                    if (control is StickControl stick && stick.ReadValue().sqrMagnitude > 0.01f)
                        return true;
                }
            }
            return false;
        }

        private void Tick()
        {
            if (state != CaptureState.Finishing && Time.realtimeSinceStartup >= deadline) Cancel();
            if (Time.frameCount <= releaseFrame || ControlsHeld()) return;
            if (state == CaptureState.Waiting) Listen();
            else if (state == CaptureState.ConflictRelease)
            {
                state = CaptureState.Conflict;
                foreach (var action in enabledUi)
                    if (action.name != "Pause") action.Enable();
                accept.SetEnabled(true);
                cancel.Focus();
            }
            else if (state == CaptureState.Finishing) Cleanup(true);
        }

        private void Listen()
        {
            state = CaptureState.Listening;
            RefreshPrompt();
            operation = target.Action.PerformInteractiveRebinding(target.Index)
                .WithExpectedControlType(vectorBinding ? "Stick" : "Button")
                .WithControlsExcluding("<Keyboard>/escape")
                .WithControlsExcluding("<Keyboard>/anyKey")
                .WithControlsExcluding("<Gamepad>/start")
                .WithControlsExcluding("<Gamepad>/*Stick/*")
                .WithControlsExcluding("<Pointer>/position")
                .WithControlsExcluding("<Pointer>/delta")
                .WithControlsExcluding("<Mouse>/scroll")
                .WithControlsExcluding("<Mouse>/scroll/*")
                .WithMatchingEventsBeingSuppressed()
                .WithActionEventNotificationsBeingSuppressed()
                .OnApplyBinding((_, path) => candidate = path)
                .OnComplete(Completed)
                .OnCancel(_ => Cancel());
            if (group == InputBindings.Controller) operation.WithControlsHavingToMatchPath("<Gamepad>");
            else operation.WithControlsHavingToMatchPath("<Keyboard>").WithControlsHavingToMatchPath("<Mouse>");
            operation.Start();
        }

        private void Completed(InputActionRebindingExtensions.RebindingOperation completed)
        {
            var conflicts = bindings.Conflicts(target, candidate, completed.selectedControl);
            operation = null;
            completed.Dispose();
            if (conflicts.Count == 0) { bindings.Apply(target, candidate); Finish(); return; }
            conflictNames = string.Join(", ", conflicts);
            state = CaptureState.ConflictRelease;
            RefreshPrompt();
            releaseFrame = Time.frameCount + 1;
            deadline = Time.realtimeSinceStartup + 30f;
            accept.style.display = DisplayStyle.Flex;
            accept.SetEnabled(false);
        }

        private void Accept()
        {
            if (state != CaptureState.Conflict) return;
            bindings.Apply(target, candidate);
            Finish();
        }

        private string CancelHint => $"Escape / {presentation.Resolve(ui.FindAction("Pause"), InputBindings.Controller).Text}";

        private void RefreshPrompt()
        {
            if (state == CaptureState.Idle) return;
            prompt.text = state switch
            {
                CaptureState.Waiting => $"Release buttons and center sticks, then choose a binding for {target.Name}. {CancelHint} cancels (15 seconds).",
                CaptureState.Listening => $"{(vectorBinding ? "Move a stick" : "Press a button")} for {target.Name}. {CancelHint} cancels (15 seconds).",
                CaptureState.Finishing => "Release buttons and center sticks to continue.",
                _ => $"Already assigned to {conflictNames}. Use this control for both? {CancelHint} cancels."
            };
        }

        private void CaptureCancel(InputEventPtr evt, InputDevice device)
        {
            if (state == CaptureState.Finishing || evt.type != StateEvent.Type && evt.type != DeltaStateEvent.Type) return;
            ButtonControl button = device is Keyboard keyboard ? keyboard.escapeKey :
                device is Gamepad pad ? pad.startButton : null;
            bool pressed = button != null && button.ReadValueFromEvent(evt, out float value) && value >= button.pressPointOrDefault;
            if (device is Mouse mouse && root.panel != null &&
                mouse.leftButton.ReadValueFromEvent(evt, out float click) && click >= mouse.leftButton.pressPointOrDefault)
            {
                if (!mouse.position.ReadValueFromEvent(evt, out var position)) position = mouse.position.ReadValue();
                var point = RuntimePanelUtils.ScreenToPanel(root.panel, new Vector2(position.x, Screen.height - position.y));
                pressed |= cancel.worldBound.Contains(point);
            }
            if (!pressed) return;
            evt.handled = true;
            Cancel();
        }

        private void Cancel()
        {
            if (state == CaptureState.Idle || state == CaptureState.Finishing) return;
            Finish();
        }

        private void Finish()
        {
            operation?.Dispose();
            operation = null;
            foreach (var action in enabledUi) action.Disable();
            state = CaptureState.Finishing;
            releaseFrame = Time.frameCount + 1;
            RefreshPrompt();
            accept.SetEnabled(false);
        }

        private void Cleanup(bool focus)
        {
            if (state == CaptureState.Idle) return;
            operation?.Dispose();
            operation = null;
            tick?.Pause();
            tick = null;
            InputSystem.onEvent -= CaptureCancel;
            if (targetEnabled) target.Action.Enable();
            foreach (var action in enabledUi) action.Enable();
            enabledUi.Clear();
            options.SetEnabled(true);
            tabs.SetEnabled(true);
            back.SetEnabled(true);
            feedback.style.display = DisplayStyle.None;
            state = CaptureState.Idle;
            capturing = null;
            suppressThroughFrame = Time.frameCount + 1;
            if (focus) returnFocus?.Focus();
            target = null;
            candidate = null;
        }

        public void Close() => Cleanup(false);
        private void Interrupted() => Cleanup(true);
        private void FocusChanged(bool focused) { if (!focused) Cleanup(true); }
        private bool BlockNavigation => state != CaptureState.Idle && state != CaptureState.Conflict ||
            Time.frameCount <= suppressThroughFrame;
        private void GuardSubmit(NavigationSubmitEvent evt) { if (BlockNavigation) evt.StopImmediatePropagation(); }
        private void GuardMove(NavigationMoveEvent evt) { if (BlockNavigation) evt.StopImmediatePropagation(); }
        private void GuardCancel(NavigationCancelEvent evt)
        {
            if (state == CaptureState.Conflict) Cancel();
            if (SuppressMenuInput) evt.StopImmediatePropagation();
        }
        private void GuardKey(KeyDownEvent evt) { if (BlockNavigation) evt.StopImmediatePropagation(); }
        private bool AllowedTarget(IEventHandler target) => target is VisualElement element &&
            (element == cancel || cancel.Contains(element) || state == CaptureState.Conflict && (element == accept || accept.Contains(element)));
        private void GuardPointer(PointerDownEvent evt)
        { if (SuppressMenuInput && !AllowedTarget(evt.target)) evt.StopImmediatePropagation(); }
        private void GuardClick(ClickEvent evt)
        { if (SuppressMenuInput && !AllowedTarget(evt.target)) evt.StopImmediatePropagation(); }

        public void Dispose()
        {
            Close();
            device.UnregisterValueChangedCallback(DeviceChanged);
            cancel.clicked -= Cancel;
            accept.clicked -= Accept;
            restore.clicked -= Restore;
            presentation.Changed -= Refresh;
            presentation.Interrupted -= Interrupted;
            Application.focusChanged -= FocusChanged;
            root.UnregisterCallback<NavigationSubmitEvent>(GuardSubmit, TrickleDown.TrickleDown);
            root.UnregisterCallback<NavigationMoveEvent>(GuardMove, TrickleDown.TrickleDown);
            root.UnregisterCallback<NavigationCancelEvent>(GuardCancel, TrickleDown.TrickleDown);
            root.UnregisterCallback<KeyDownEvent>(GuardKey, TrickleDown.TrickleDown);
            root.UnregisterCallback<PointerDownEvent>(GuardPointer, TrickleDown.TrickleDown);
            root.UnregisterCallback<ClickEvent>(GuardClick, TrickleDown.TrickleDown);
        }
    }
}
