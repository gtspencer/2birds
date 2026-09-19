using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace TwoBirds
{
    internal sealed class SettingsPanel : IDisposable
    {
        private readonly SessionController session;
        private readonly VisualElement page, graphics, controlsPage;
        private readonly Button graphicsTab, controlsTab, back;
        private readonly DropdownField displayMode, frameCap;
        private readonly ControlsRemapPanel controls;
        private readonly Action goBack;
        public bool IsOpen { get; private set; }
        public event Action Changed;
        private bool controlsSelected;
        public Button SelectedTab => controlsSelected ? controlsTab : graphicsTab;

        public SettingsPanel(VisualElement root, SessionController session, Action goBack)
        {
            this.session = session;
            this.goBack = goBack;
            page = root.Q("settings-page");
            graphics = page.Q("graphics-settings");
            controlsPage = page.Q("controls-settings");
            graphicsTab = page.Q<Button>("graphics-tab");
            controlsTab = page.Q<Button>("controls-tab");
            back = page.Q<Button>("settings-back");
            displayMode = page.Q<DropdownField>("display-mode");
            frameCap = page.Q<DropdownField>("frame-cap");
            displayMode.choices = new List<string> { "Fullscreen", "Windowed", "Windowed Fullscreen" };
            displayMode.RegisterValueChangedCallback(DisplayModeChanged);
            frameCap.choices = new List<string> { "60 FPS", "90 FPS", "120 FPS" };
            frameCap.RegisterValueChangedCallback(FrameCapChanged);
            graphicsTab.clicked += ShowGraphics;
            controlsTab.clicked += ShowControls;
            back.clicked += goBack;
            controls = new ControlsRemapPanel(root, session.Bindings, session.InputPresentation);
            ShowGraphics();
        }

        public void SetVisible(bool visible)
        {
            if (IsOpen == visible) return;
            IsOpen = visible;
            if (!visible) controls.Close();
            else
            {
                displayMode.SetValueWithoutNotify(SessionController.DisplayModeLabel(session.DisplayMode));
                frameCap.SetValueWithoutNotify($"{PlayerPrefs.GetInt("RenderingFrameCap", 60)} FPS");
            }
            page.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
            if (visible) SelectedTab.Focus();
        }

        private void ShowGraphics() => SelectTab(false);
        private void ShowControls() => SelectTab(true);
        private void SelectTab(bool showControls)
        {
            controlsSelected = showControls;
            if (!showControls) controls.Close();
            graphics.style.display = showControls ? DisplayStyle.None : DisplayStyle.Flex;
            controlsPage.style.display = showControls ? DisplayStyle.Flex : DisplayStyle.None;
            graphicsTab.EnableInClassList("selected-tab", !showControls);
            controlsTab.EnableInClassList("selected-tab", showControls);
            Changed?.Invoke();
        }

        private void FrameCapChanged(ChangeEvent<string> evt) =>
            session.SetFrameCap(frameCap.index switch { 1 => 90, 2 => 120, _ => 60 });

        private void DisplayModeChanged(ChangeEvent<string> evt) => session.SetDisplayMode(displayMode.index);

        public void Dispose()
        {
            controls.Dispose();
            page.style.display = DisplayStyle.None;
            IsOpen = false;
            frameCap.UnregisterValueChangedCallback(FrameCapChanged);
            displayMode.UnregisterValueChangedCallback(DisplayModeChanged);
            graphicsTab.clicked -= ShowGraphics;
            controlsTab.clicked -= ShowControls;
            back.clicked -= goBack;
        }
    }
}
