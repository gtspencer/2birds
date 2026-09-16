using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

namespace TwoBirds
{
    public sealed class SessionOverlay : MonoBehaviour
    {
        private SessionController session;
        private GamePlayerSpawner spawner;
        private HudController hud;
        private VisualElement root;
        private Label sessionInfo;
        private InputAction pause;
        private SessionMode displayedMode;
        private int displayedCount = -1, displayedCapacity;
        private void OnEnable()
        {
            session = SessionController.Instance;
            if (session == null) return;
            spawner = FindAnyObjectByType<GamePlayerSpawner>();
            hud = FindAnyObjectByType<HudController>();
            root = GetComponent<UIDocument>().rootVisualElement;
            sessionInfo = root.Q<Label>("session-info");
            displayedCount = -1;
            root.Q<Button>("resume").clicked += Resume;
            root.Q<Button>("leave").clicked += Leave;
            root.Q<Button>("copy").clicked += Copy;
            root.Q<Button>("invite").clicked += Invite;
            session.Changed += Render;
            pause = InputSystem.actions.FindAction("UI/Cancel");
            pause.performed += Pause;
            Render();
        }
        private void Pause(InputAction.CallbackContext context)
        {
            if (hud == null) hud = FindAnyObjectByType<HudController>();
            if (hud != null && hud.InventoryOpen) { hud.CloseInventory(); return; }
            if (session.Phase == SessionPhase.InGame) session.SetPanel(!session.PanelOpen);
            else session.Leave();
        }
        private void Resume() => session.SetPanel(false);
        private void Leave() => session.Leave();
        private void Copy() => GUIUtility.systemCopyBuffer = session.ShareEndpoint;
        private void Invite() => session.Lobby.InviteFriends();
        private void Update()
        {
            RefreshSessionInfo();
        }
        private void RefreshSessionInfo()
        {
            if (sessionInfo == null || !session) return;
            int count = spawner ? spawner.PlayerCount : 0;
            if (displayedMode == session.Mode && displayedCount == count && displayedCapacity == session.Capacity) return;
            displayedMode = session.Mode;
            displayedCount = count;
            displayedCapacity = session.Capacity;
            sessionInfo.text = $"{displayedMode} · {displayedCount} / {displayedCapacity} players";
        }
        private void Render()
        {
            RefreshSessionInfo();
            bool visible = session.PanelOpen || session.Phase != SessionPhase.InGame;
            root.Q("session-panel").style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
            root.Q<Button>("resume").SetEnabled(session.Phase == SessionPhase.InGame);
            root.Q<Button>("leave").SetEnabled(session.Phase != SessionPhase.Stopping);
            root.Q<Button>("invite").style.display = !session.LocalNetworking && session.Mode != SessionMode.Solo ? DisplayStyle.Flex : DisplayStyle.None;
            root.Q<Button>("invite").SetEnabled(session.Phase == SessionPhase.InGame);
            root.Q<Label>("status").text = session.Status;
            root.Q<Label>("share-endpoint").text = session.ShareEndpoint;
            root.Q<Button>("copy").style.display = session.ShareEndpoint == "" ? DisplayStyle.None : DisplayStyle.Flex;
            if (visible) root.Q<Button>("resume").Focus();
        }
        private void OnDisable()
        {
            if (session != null) session.Changed -= Render;
            if (pause != null) pause.performed -= Pause;
            if (root == null) return;
            root.Q<Button>("resume").clicked -= Resume;
            root.Q<Button>("leave").clicked -= Leave;
            root.Q<Button>("copy").clicked -= Copy;
            root.Q<Button>("invite").clicked -= Invite;
        }
    }
}
