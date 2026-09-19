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
        private VisualElement panel, pausePage, settingsPage;
        private Button settingsButton;
        private SettingsPanel settings;
        private InputAction pause;
        private MenuNavigation navigation;
        private Button resume, leave, invite;
        private Label status, footer;
        private int handledFrame = -1;
        private void OnEnable()
        {
            session = SessionController.Instance;
            if (!session) return;
            hud = FindAnyObjectByType<HudController>();
            root = GetComponent<UIDocument>().rootVisualElement;
            panel = root.Q("session-panel");
            pausePage = root.Q("pause-page");
            settingsPage = root.Q("settings-page");
            settingsButton = root.Q<Button>("settings");
            resume = root.Q<Button>("resume");
            leave = root.Q<Button>("leave");
            invite = root.Q<Button>("invite");
            status = root.Q<Label>("status");
            footer = root.Q<Label>("session-footer");
            settings = new SettingsPanel(root, session, CloseSettings);
            settings.Changed += PresentationChanged;
            settingsButton.clicked += OpenSettings;
            resume.clicked += Resume;
            leave.clicked += Leave;
            invite.clicked += Invite;
            session.Changed += Render;
            pause = InputSystem.actions.FindAction("UI/Pause");
            pause.performed += Pause;
            root.RegisterCallback<NavigationCancelEvent>(Cancel);
            navigation = new MenuNavigation(root, session.InputPresentation,
                () => panel.style.display == DisplayStyle.None ? null : settings.IsOpen ? settingsPage : pausePage,
                () => settings.IsOpen ? settings.SelectedTab : resume.enabledSelf ? resume : leave);
            session.InputPresentation.Changed += PresentationChanged;
            Render();
        }
        private void Pause(InputAction.CallbackContext context)
        {
            if (ControlsRemapPanel.SuppressMenuInput || session.InputPresentation.SuppressInput || session.ConsoleOpen || handledFrame == Time.frameCount) return;
            if (context.control.device is Keyboard && (session.PanelOpen || settings.IsOpen || hud && hud.InventoryOpen)) return;
            handledFrame = Time.frameCount;
            if (settings.IsOpen)
            {
                using var cancel = NavigationCancelEvent.GetPooled();
                (root.panel.focusController.focusedElement as VisualElement)?.SendEvent(cancel);
                return;
            }
            if (hud && hud.InventoryOpen) { hud.CloseInventory(); return; }
            if (session.Phase == SessionPhase.InGame) session.SetPanel(!session.PanelOpen);
        }
        private void Cancel(NavigationCancelEvent evt)
        {
            if (ControlsRemapPanel.SuppressMenuInput || session.InputPresentation.SuppressInput || session.ConsoleOpen || handledFrame == Time.frameCount) return;
            if (settings.IsOpen) CloseSettings();
            else if (session.PanelOpen && session.Phase == SessionPhase.InGame) session.SetPanel(false);
            else if (session.Phase is SessionPhase.LoadingGame or SessionPhase.Connecting) session.Leave();
            else return;
            handledFrame = Time.frameCount;
            evt.StopPropagation();
        }
        private void PresentationChanged()
        {
            var presentation = session.InputPresentation;
            footer.text = settings.IsOpen
                ? $"{presentation.Label("UI/Submit")} Select   {presentation.Label("UI/Cancel")} Back"
                : session.Phase == SessionPhase.InGame ? $"{presentation.Label("UI/Submit")} Select   {presentation.Label("UI/Pause")} / {presentation.Label("UI/Cancel")} Resume"
                : session.Phase == SessionPhase.Stopping ? "Leaving session…" : $"{presentation.Label("UI/Cancel")} Cancel connection";
            if (settings.IsOpen && settings.SelectedTab.name == "controls-tab") footer.text += $"   {presentation.ScrollLabel} Scroll";
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
            if (session.InputPresentation.ActiveDevice is Gamepad) settingsButton.Focus();
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
            resume.SetEnabled(session.Phase == SessionPhase.InGame);
            leave.SetEnabled(session.Phase != SessionPhase.Stopping);
            invite.style.display = !session.LocalNetworking && session.Mode != SessionMode.Solo ? DisplayStyle.Flex : DisplayStyle.None;
            invite.SetEnabled(session.Phase == SessionPhase.InGame);
            status.text = session.Status;
            if (visible && hud && hud.InventoryOpen) hud.CloseInventory();
            if (visible) navigation?.Repair(!settings.IsOpen && (!wasVisible || wasSettings) ? resume.enabledSelf ? resume : leave : null);
            if (visible && (!wasVisible || wasSettings != settings.IsOpen))
                root.schedule.Execute(() => navigation?.Repair());
            if (!visible) navigation?.Repair();
            PresentationChanged();
        }
        private void OnDisable()
        {
            if (settings != null) settings.Changed -= PresentationChanged;
            settings?.Dispose();
            settings = null;
            if (settingsButton != null) settingsButton.clicked -= OpenSettings;
            navigation?.Dispose();
            if (session) { session.Changed -= Render; session.InputPresentation.Changed -= PresentationChanged; }
            if (pause != null) pause.performed -= Pause;
            root?.UnregisterCallback<NavigationCancelEvent>(Cancel);
            if (root == null) return;
            resume.clicked -= Resume;
            leave.clicked -= Leave;
            invite.clicked -= Invite;
        }
    }
}
