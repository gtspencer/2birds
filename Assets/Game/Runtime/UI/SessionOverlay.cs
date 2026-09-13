using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

namespace TwoBirds
{
    public sealed class SessionOverlay : MonoBehaviour
    {
        private SessionController session;
        private GamePlayerSpawner spawner;
        private VisualElement root;
        private InputAction pause;
        private void OnEnable()
        {
            session = SessionController.Instance;
            if (session == null) return;
            spawner = FindAnyObjectByType<GamePlayerSpawner>();
            root = GetComponent<UIDocument>().rootVisualElement;
            root.Q<Button>("resume").clicked += Resume;
            root.Q<Button>("leave").clicked += Leave;
            root.Q<Button>("copy").clicked += Copy;
            session.Changed += Render;
            pause = InputSystem.actions.FindAction("UI/Cancel");
            pause.performed += Pause;
            Render();
        }
        private void Pause(InputAction.CallbackContext context)
        {
            if (session.Phase == SessionPhase.InGame) session.SetPanel(!session.PanelOpen);
            else session.Leave();
        }
        private void Resume() => session.SetPanel(false);
        private void Leave() => session.Leave();
        private void Copy() => GUIUtility.systemCopyBuffer = session.ShareEndpoint;
        private void Update()
        {
            if (root != null) root.Q<Label>("session-info").text = $"{session.Mode} · { (spawner != null ? spawner.PlayerCount : 0) } / {(session.Mode == SessionMode.Solo ? 1 : 2)} players";
        }
        private void Render()
        {
            bool visible = session.PanelOpen || session.Phase != SessionPhase.InGame;
            root.Q("session-panel").style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
            root.Q<Button>("resume").SetEnabled(session.Phase == SessionPhase.InGame);
            root.Q<Button>("leave").SetEnabled(session.Phase != SessionPhase.Stopping);
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
        }
    }
}
