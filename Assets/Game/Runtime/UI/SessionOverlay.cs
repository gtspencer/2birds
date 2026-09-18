using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

namespace TwoBirds
{
    public sealed class SessionOverlay : MonoBehaviour
    {
        private SessionController session;
        private HudController hud;
        private VisualElement root;
        private VisualElement panel, pausePage;
        private Button settingsButton;
        private SettingsPanel settings;
        private InputAction pause, cancel;
        private int handledFrame = -1;
        private void OnEnable()
        {
            session = SessionController.Instance;
            if (session == null) return;
            hud = FindAnyObjectByType<HudController>();
            root = GetComponent<UIDocument>().rootVisualElement;
            panel = root.Q("session-panel");
            pausePage = root.Q("pause-page");
            settingsButton = root.Q<Button>("settings");
            settings = new SettingsPanel(root, session, CloseSettings);
            settingsButton.clicked += OpenSettings;
            root.Q<Button>("resume").clicked += Resume;
            root.Q<Button>("leave").clicked += Leave;
            root.Q<Button>("invite").clicked += Invite;
            session.Changed += Render;
            pause = InputSystem.actions.FindAction("UI/Pause");
            pause.performed += Pause;
            cancel = InputSystem.actions.FindAction("UI/Cancel");
            cancel.performed += Cancel;
            Render();
        }
        private void Pause(InputAction.CallbackContext context)
        {
            if (ControlsRemapPanel.SuppressMenuInput || session.ConsoleOpen || handledFrame == Time.frameCount) return;
            handledFrame = Time.frameCount;
            if (settings.IsOpen) { CloseSettings(); return; }
            if (hud && hud.InventoryOpen) { hud.CloseInventory(); return; }
            if (session.Phase == SessionPhase.InGame) session.SetPanel(!session.PanelOpen);
            else session.Leave();
        }
        private void Cancel(InputAction.CallbackContext context)
        {
            if (context.control == Keyboard.current?.escapeKey || ControlsRemapPanel.SuppressMenuInput ||
                session.ConsoleOpen || handledFrame == Time.frameCount) return;
            if (settings.IsOpen)
            {
                handledFrame = Time.frameCount;
                CloseSettings();
                return;
            }
            if (hud && hud.InventoryOpen)
            {
                if (hud.InventoryInputFrame == Time.frameCount) return;
                handledFrame = Time.frameCount;
                hud.CloseInventory();
            }
            else if (session.PanelOpen && session.Phase == SessionPhase.InGame)
            {
                handledFrame = Time.frameCount;
                session.SetPanel(false);
            }
        }
        private void Resume() => session.SetPanel(false);
        private void Leave() => session.Leave();
        private void Invite() => session.Lobby.InviteFriends();
        private void OpenSettings()
        {
            if (!session.PanelOpen || session.Phase != SessionPhase.InGame) return;
            settings.SetVisible(true);
            Render();
        }
        private void CloseSettings()
        {
            settings.SetVisible(false);
            Render();
            settingsButton.Focus();
        }
        private void Render()
        {
            bool visible = session.PanelOpen || session.Phase != SessionPhase.InGame;
            bool wasVisible = panel.style.display.value == DisplayStyle.Flex;
            bool wasSettings = settings.IsOpen;
            if (!session.PanelOpen || session.Phase != SessionPhase.InGame) settings.SetVisible(false);
            panel.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
            panel.EnableInClassList("settings-card", settings.IsOpen);
            pausePage.style.display = settings.IsOpen ? DisplayStyle.None : DisplayStyle.Flex;
            settingsButton.SetEnabled(session.Phase == SessionPhase.InGame);
            root.Q<Button>("resume").SetEnabled(session.Phase == SessionPhase.InGame);
            root.Q<Button>("leave").SetEnabled(session.Phase != SessionPhase.Stopping);
            root.Q<Button>("invite").style.display = !session.LocalNetworking && session.Mode != SessionMode.Solo ? DisplayStyle.Flex : DisplayStyle.None;
            root.Q<Button>("invite").SetEnabled(session.Phase == SessionPhase.InGame);
            root.Q<Label>("status").text = session.Status;
            if (visible && !settings.IsOpen && (!wasVisible || wasSettings)) root.Q<Button>("resume").Focus();
        }
        private void OnDisable()
        {
            settings?.Dispose();
            settings = null;
            if (settingsButton != null) settingsButton.clicked -= OpenSettings;
            if (session != null) session.Changed -= Render;
            if (pause != null) pause.performed -= Pause;
            if (cancel != null) cancel.performed -= Cancel;
            if (root == null) return;
            root.Q<Button>("resume").clicked -= Resume;
            root.Q<Button>("leave").clicked -= Leave;
            root.Q<Button>("invite").clicked -= Invite;
        }
    }
}
