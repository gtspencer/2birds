using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

namespace TwoBirds
{
    [RequireComponent(typeof(UIDocument))]
    public sealed class MenuPresenter : MonoBehaviour
    {
        private VisualElement root;
        private SessionController session;
        private SteamLobby lobby;
        private readonly List<Action> unbind = new();
        private readonly Label[] roster = new Label[SessionController.MultiplayerCapacity];
        private string page = "main";
        private InputAction cancel;
        private InputAction pause;
        private SettingsPanel settings;

        private void Start() => Bind();
        private void OnEnable() { if (session) Bind(); }
        private void Bind()
        {
            root = GetComponent<UIDocument>().rootVisualElement;
            session = SessionController.Instance;
            lobby = session.GetComponent<SteamLobby>();
            Click("solo", () => session.StartSession(SessionMode.Solo));
            Click("host", () => { if (session.LocalNetworking) Show("host"); else session.StartSession(SessionMode.Host); });
            Click("join", () => { Show("join"); if (!session.LocalNetworking && lobby) lobby.RefreshFriends(); });
            Click("host-back", () => Show("main"));
            Click("join-back", () => Show("main"));
            Click("settings", () => Show("settings"));
            settings = new SettingsPanel(root, session, () => Show("main"));
            Click("start-host", () => session.StartSession(SessionMode.Host, portText: root.Q<TextField>("host-port").value));
            Click("connect", () => session.StartSession(SessionMode.Join, root.Q<TextField>("join-ip").value, root.Q<TextField>("join-port").value));
            Click("friends-refresh", () => { if (lobby) lobby.RefreshFriends(); });
            Click("start-game", session.StartGame);
            Click("invite", () => { if (lobby) lobby.InviteFriends(); });
            Click("lobby-leave", () => session.Leave());
            Click("cancel", () => session.Leave());
            root.Q("local-join").style.display = session.LocalNetworking ? DisplayStyle.Flex : DisplayStyle.None;
            root.Q("steam-join").style.display = session.LocalNetworking ? DisplayStyle.None : DisplayStyle.Flex;
            var slots = root.Q("roster");
            slots.Clear();
            for (int i = 0; i < roster.Length; i++)
            {
                roster[i] = new Label { enableRichText = false };
                slots.Add(roster[i]);
            }
            session.Changed += Render;
            if (lobby) lobby.Changed += RenderFriends;
            cancel = InputSystem.actions.FindAction("UI/Cancel");
            cancel.performed += Cancel;
            pause = InputSystem.actions.FindAction("UI/Pause");
            pause.performed += Pause;
            RenderFriends();
            Render();
            root.Q<Button>("solo").Focus();
        }

        private void Click(string name, Action action)
        {
            var button = root.Q<Button>(name);
            button.clicked += action;
            unbind.Add(() => button.clicked -= action);
        }

        private void Cancel(InputAction.CallbackContext context)
        {
            if (ControlsRemapPanel.SuppressMenuInput || context.control == Keyboard.current?.escapeKey) return;
            if (session.Phase != SessionPhase.Idle) session.Leave(); else Show("main");
        }

        private void Pause(InputAction.CallbackContext context)
        {
            if (ControlsRemapPanel.SuppressMenuInput) return;
            if (session.Phase != SessionPhase.Idle) session.Leave(); else Show("main");
        }

        private void Show(string next)
        {
            page = next;
            Render();
            root.Q(page + "-page").Q<Button>()?.Focus();
        }

        private void RenderFriends()
        {
            var list = root.Q("friends-list");
            list.Clear();
            root.Q<Label>("friends-status").text = lobby ? lobby.FriendsStatus : "Configure Steam on SessionRoot.";
            if (lobby)
                foreach (var friend in lobby.Friends)
                {
                    ulong id = friend.Id;
                    var button = new Button(() => session.JoinSteamLobby(id)) { text = friend.Name, enableRichText = false };
                    list.Add(button);
                }
            Render();
        }

        private void Render()
        {
            bool idle = session.Phase == SessionPhase.Idle;
            settings.SetVisible(idle && page == "settings");
            root.Q(className: "card").EnableInClassList("settings-card", idle && page == "settings");
            bool inLobby = session.Phase == SessionPhase.InLobby;
            foreach (string name in new[] { "main", "host", "join" })
                root.Q(name + "-page").style.display = idle && name == page ? DisplayStyle.Flex : DisplayStyle.None;
            root.Q("lobby-page").style.display = inLobby ? DisplayStyle.Flex : DisplayStyle.None;
            for (int i = 0; i < roster.Length; i++)
            {
                if (i >= session.Roster.Length) { roster[i].text = $"{i + 1}. Open slot"; continue; }
                var member = session.Roster[i];
                roster[i].text = $"{i + 1}. {member.Name}{(member.Host ? " (Host)" : "")}{(member.Ready ? " · In game" : "")}";
            }
            root.Q<Button>("start-game").style.display = session.Mode == SessionMode.Host ? DisplayStyle.Flex : DisplayStyle.None;
            root.Q<Button>("start-game").SetEnabled(session.CanStart);
            root.Q<Button>("invite").style.display = session.LocalNetworking ? DisplayStyle.None : DisplayStyle.Flex;
            root.Q<Button>("invite").SetEnabled(lobby && lobby.HasLobby);
            root.Q<Label>("status").text = session.Status;
            root.Q<Button>("cancel").style.display = idle || inLobby ? DisplayStyle.None : DisplayStyle.Flex;
            root.Q<Button>("cancel").SetEnabled(session.Phase != SessionPhase.Stopping);
        }

        private void OnDisable()
        {
            settings?.Dispose();
            settings = null;
            foreach (Action action in unbind) action();
            unbind.Clear();
            if (session) session.Changed -= Render;
            if (lobby) lobby.Changed -= RenderFriends;
            if (cancel != null) cancel.performed -= Cancel;
            if (pause != null) pause.performed -= Pause;
        }
    }
}
