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
        private VisualElement root, card, entryPanel, entryKeys;
        private SessionController session;
        private SteamLobby lobby;
        private readonly List<Action> unbind = new();
        private readonly Dictionary<string, VisualElement> pages = new();
        private readonly Label[] roster = new Label[SessionController.MultiplayerCapacity];
        private readonly List<Button> friends = new();
        private string page = "main", renderedPage;
        private InputAction pause;
        private SettingsPanel settings;
        private MenuNavigation navigation;
        private ScrollView friendList;
        private Label friendStatus, status, footer, entryError;
        private Button start, invite, cancel, refresh;
        private Label hostEndpoint, lobbyEndpoint;
        private TextField hostPort, joinIP, joinPort, entryValue, editing;
        private Button decimalKey;
        private int handledFrame = -1;
        private bool restoreFriend;

        private void Start() => Bind();
        private void OnEnable() { if (session) Bind(); }
        private void Bind()
        {
            root = GetComponent<UIDocument>().rootVisualElement;
            session = SessionController.Instance;
            lobby = session.GetComponent<SteamLobby>();
            page = session.MenuPage;
            restoreFriend = page == "join" && session.FriendSelection != 0;
            card = root.Q(className: "card");
            foreach (string name in new[] { "main", "host", "join", "settings", "lobby" }) pages[name] = root.Q(name + "-page");
            hostPort = root.Q<TextField>("host-port");
            joinIP = root.Q<TextField>("join-ip");
            joinPort = root.Q<TextField>("join-port");
            hostPort.value = session.HostPort;
            joinIP.value = session.JoinAddress;
            joinPort.value = session.JoinPort;
            BindField(hostPort, value => session.HostPort = value);
            BindField(joinIP, value => session.JoinAddress = value);
            BindField(joinPort, value => session.JoinPort = value);
            hostEndpoint = root.Q<Label>("host-endpoint");
            lobbyEndpoint = root.Q<Label>("lobby-endpoint");
            friendList = root.Q<ScrollView>("friends-list");
            friendStatus = root.Q<Label>("friends-status");
            status = root.Q<Label>("status");
            footer = root.Q<Label>("menu-footer");
            start = root.Q<Button>("start-game");
            invite = root.Q<Button>("invite");
            cancel = root.Q<Button>("cancel");
            refresh = root.Q<Button>("friends-refresh");
            entryPanel = root.Q("endpoint-entry");
            entryKeys = root.Q("entry-keys");
            entryValue = root.Q<TextField>("entry-value");
            entryError = root.Q<Label>("entry-error");
            entryPanel.RegisterCallback<NavigationCancelEvent>(EntryBack, TrickleDown.TrickleDown);
            BuildEntry();
            Click("solo", () => { session.MenuSelection = "solo"; session.StartSession(SessionMode.Solo); });
            Click("host", () => { session.MenuSelection = "host"; if (session.LocalNetworking) Show("host"); else session.StartSession(SessionMode.Host); });
            Click("join", () => { session.MenuSelection = "join"; Show("join"); if (!session.LocalNetworking && lobby) lobby.RefreshFriends(); });
            Click("host-back", Back);
            Click("join-back", Back);
            Click("settings", () => Show("settings"));
            Click("quit", Application.Quit);
            settings = new SettingsPanel(root, session, Back);
            settings.Changed += PresentationChanged;
            Click("start-host", () => { if (Validate(hostPort)) session.StartSession(SessionMode.Host, portText: hostPort.value); });
            Click("connect", () => { if (Validate(joinIP) && Validate(joinPort)) session.StartSession(SessionMode.Join, joinIP.value, joinPort.value); });
            Click("friends-refresh", () => { if (lobby) lobby.RefreshFriends(); });
            Click("start-game", session.StartGame);
            Click("invite", () => { if (lobby) lobby.InviteFriends(); });
            Click("lobby-leave", () => session.Leave());
            Click("cancel", () => session.Leave());
            root.Q("local-join").style.display = session.LocalNetworking ? DisplayStyle.Flex : DisplayStyle.None;
            root.Q("steam-join").style.display = session.LocalNetworking ? DisplayStyle.None : DisplayStyle.Flex;
            var slots = root.Q("roster");
            slots.Clear();
            for (int i = 0; i < roster.Length; i++) { roster[i] = new Label { enableRichText = false }; slots.Add(roster[i]); }
            session.Changed += Render;
            if (lobby) lobby.Changed += RenderFriends;
            root.RegisterCallback<NavigationCancelEvent>(Cancel);
            root.RegisterCallback<FocusInEvent>(RememberFocus);
            pause = InputSystem.actions.FindAction("UI/Pause");
            pause.performed += Pause;
            navigation = new MenuNavigation(root, session.InputPresentation, Scope, Initial);
            session.InputPresentation.Changed += PresentationChanged;
            RenderFriends();
            Render();
            root.schedule.Execute(() => navigation?.Repair(Initial()));
        }

        private void Click(string name, Action action)
        {
            var button = root.Q<Button>(name);
            button.clicked += action;
            unbind.Add(() => button.clicked -= action);
        }

        private void BindField(TextField field, Action<string> save)
        {
            EventCallback<ChangeEvent<string>> changed = evt => save(evt.newValue);
            EventCallback<NavigationSubmitEvent> submit = evt =>
            {
                if (session.InputPresentation.ActiveDevice is not Gamepad) return;
                OpenEntry(field);
                evt.StopPropagation();
            };
            field.RegisterValueChangedCallback(changed);
            field.RegisterCallback(submit, TrickleDown.TrickleDown);
            unbind.Add(() => { field.UnregisterValueChangedCallback(changed); field.UnregisterCallback(submit, TrickleDown.TrickleDown); });
        }

        private bool Validate(TextField field)
        {
            bool valid = field == joinIP ? EndpointUtility.TryAddress(field.value, out _) : EndpointUtility.TryPort(field.value, out _);
            root.Q<Label>(field.name + "-error").text = valid ? "" : field == joinIP ? "Enter a valid IPv4 host address." : "Enter a port from 1 to 65535.";
            if (!valid) field.Focus();
            return valid;
        }

        private void BuildEntry()
        {
            entryKeys.Clear();
            foreach (string text in new[] { "1", "2", "3", "4", "5", "6", "7", "8", "9", ".", "0", "Delete" })
            {
                string key = text;
                var button = new Button(() =>
                {
                    if (key == "Delete") entryValue.value = entryValue.value.Length > 0 ? entryValue.value.Substring(0, entryValue.value.Length - 1) : "";
                    else if (entryValue.value.Length < entryValue.maxLength) entryValue.value += key;
                }) { text = text };
                entryKeys.Add(button);
                if (text == ".") decimalKey = button;
            }
            Click("entry-confirm", ConfirmEntry);
            Click("entry-cancel", CloseEntry);
        }

        private void OpenEntry(TextField field)
        {
            editing = field;
            entryValue.maxLength = field.maxLength;
            entryValue.value = field.value;
            entryError.text = "";
            decimalKey.SetEnabled(field == joinIP);
            pages[page].style.display = DisplayStyle.None;
            entryPanel.style.display = DisplayStyle.Flex;
            entryKeys.Q<Button>().Focus();
            PresentationChanged();
        }

        private void ConfirmEntry()
        {
            if (editing == null) return;
            bool valid = editing == joinIP ? EndpointUtility.TryAddress(entryValue.value, out _) : EndpointUtility.TryPort(entryValue.value, out _);
            if (!valid) { entryError.text = editing == joinIP ? "Enter a valid IPv4 host address." : "Enter a port from 1 to 65535."; return; }
            editing.value = entryValue.value;
            Validate(editing);
            CloseEntry();
        }

        private void CloseEntry()
        {
            if (editing == null) return;
            var field = editing;
            editing = null;
            entryPanel.style.display = DisplayStyle.None;
            Render();
            field.Focus();
        }

        private void EntryBack(NavigationCancelEvent evt)
        {
            if (session.InputPresentation.SuppressInput) return;
            Back();
            evt.StopPropagation();
        }

        private void Cancel(NavigationCancelEvent evt)
        {
            if (ControlsRemapPanel.SuppressMenuInput || session.InputPresentation.SuppressInput) return;
            Back();
            evt.StopPropagation();
        }

        private void Pause(InputAction.CallbackContext context)
        {
            if (ControlsRemapPanel.SuppressMenuInput || session.InputPresentation.SuppressInput) return;
            // Escape is delivered as Toolkit Cancel; Start never leaves a lobby.
            if (context.control.device is Gamepad && session.Phase == SessionPhase.Idle)
            {
                using var evt = NavigationCancelEvent.GetPooled();
                (root.panel.focusController.focusedElement as VisualElement)?.SendEvent(evt);
            }
        }

        private void Back()
        {
            if (handledFrame == Time.frameCount) return;
            handledFrame = Time.frameCount;
            if (editing != null) { CloseEntry(); return; }
            if (session.Phase != SessionPhase.Idle) { session.Leave(); return; }
            if (page == "main") return;
            string returning = page;
            Show("main", root.Q<Button>(returning));
        }

        private void Show(string next, VisualElement focus = null)
        {
            session.MenuFocus = "";
            session.FriendSelection = 0;
            restoreFriend = false;
            session.MenuPage = page = next;
            if (next == "settings") session.MenuSelection = "settings";
            Render();
            navigation.Repair(focus ?? Initial());
            root.schedule.Execute(() => navigation?.Repair(focus ?? Initial()));
        }

        private VisualElement Scope() => editing != null ? entryPanel : session.Phase == SessionPhase.Idle ? pages[page] : session.Phase == SessionPhase.InLobby ? pages["lobby"] : card;
        private VisualElement Initial() => editing != null ? entryKeys.Q<Button>() : session.Phase == SessionPhase.InLobby ? MenuNavigation.Eligible(start) ? start : MenuNavigation.Eligible(invite) ? invite : root.Q<Button>("lobby-leave") :
            session.Phase != SessionPhase.Idle ? cancel : page switch
            {
                "main" => root.Q<Button>(session.MenuSelection),
                "host" => root.Q(session.MenuFocus.Length > 0 ? session.MenuFocus : "start-host"),
                "join" => session.MenuFocus.Length > 0 ? root.Q(session.MenuFocus) : session.LocalNetworking ? joinIP :
                    friends.Find(button => (ulong)button.userData == session.FriendSelection) ?? (friends.Count > 0 ? friends[0] : refresh),
                "settings" => settings.SelectedTab,
                _ => null
            };

        private void RememberFocus(FocusInEvent evt)
        {
            if (session.Phase != SessionPhase.Idle || editing != null || page is not ("host" or "join")) return;
            var element = evt.target as VisualElement;
            while (element != null && element is not Button && element is not TextField) element = element.parent;
            if (element == null || !pages[page].Contains(element)) return;
            if (restoreFriend && element == refresh) return;
            restoreFriend = false;
            session.MenuFocus = element.name ?? "";
            session.FriendSelection = element.userData is ulong identity ? identity : 0;
        }

        private void RenderFriends()
        {
            var selected = root.panel?.focusController.focusedElement as Button;
            int index = friends.IndexOf(selected);
            ulong? identity = index >= 0 ? (ulong)selected.userData : null;
            friendList.Clear();
            friends.Clear();
            friendStatus.text = lobby ? lobby.FriendsStatus : "Configure Steam on SessionRoot.";
            if (lobby)
                foreach (var friend in lobby.Friends)
                {
                    ulong id = friend.Id;
                    var button = new Button(() => session.JoinSteamLobby(id)) { text = friend.Name, userData = id, enableRichText = false };
                    friendList.Add(button);
                    friends.Add(button);
                }
            if (index >= 0)
            {
                var next = friends.Find(button => (ulong)button.userData == identity);
                (next ?? (friends.Count > 0 ? friends[Mathf.Min(index, friends.Count - 1)] : refresh)).Focus();
            }
            else if (restoreFriend && friends.Count > 0 && (selected == refresh || selected == null))
            {
                var next = friends.Find(button => (ulong)button.userData == session.FriendSelection) ?? friends[0];
                restoreFriend = false;
                next.Focus();
            }
            Render();
        }

        private void PresentationChanged()
        {
            var presentation = session.InputPresentation;
            footer.text = $"{presentation.Label("UI/Submit")} Select";
            if (editing != null || page != "main" || session.Phase != SessionPhase.Idle)
                footer.text += $"   {presentation.Label("UI/Cancel")} {(editing != null ? "Cancel edit" : session.Phase == SessionPhase.InLobby ? "Leave lobby" : "Back")}";
            if (editing == null && session.Phase == SessionPhase.Idle &&
                (page == "join" && !session.LocalNetworking || page == "settings" && settings.SelectedTab.name == "controls-tab"))
                footer.text += $"   {presentation.ScrollLabel} Scroll";
            if (editing != null)
            {
                entryValue.isReadOnly = presentation.ActiveDevice is Gamepad;
                entryValue.focusable = !entryValue.isReadOnly;
            }
        }

        private void Render()
        {
            bool idle = session.Phase == SessionPhase.Idle;
            bool inLobby = session.Phase == SessionPhase.InLobby;
            settings.SetVisible(idle && page == "settings");
            card.EnableInClassList("settings-card", settings.IsOpen);
            foreach (string name in new[] { "main", "host", "join" }) pages[name].style.display = idle && name == page && editing == null ? DisplayStyle.Flex : DisplayStyle.None;
            pages["lobby"].style.display = inLobby ? DisplayStyle.Flex : DisplayStyle.None;
            for (int i = 0; i < roster.Length; i++)
            {
                if (i >= session.Roster.Length) { roster[i].text = $"{i + 1}. Open slot"; continue; }
                var member = session.Roster[i];
                roster[i].text = $"{i + 1}. {member.Name}{(member.Host ? " (Host)" : "")}{(member.Ready ? " ? In game" : "")}";
            }
            string endpoint = session.LocalNetworking && session.ShareEndpoint.Length > 0 ? $"Others on your network can join at {session.ShareEndpoint}" : "";
            hostEndpoint.text = session.LocalNetworking ? "Others on your network can join using this port." : "";
            lobbyEndpoint.text = endpoint;
            lobbyEndpoint.style.display = endpoint.Length > 0 ? DisplayStyle.Flex : DisplayStyle.None;
            start.style.display = session.Mode == SessionMode.Host ? DisplayStyle.Flex : DisplayStyle.None;
            start.SetEnabled(session.CanStart);
            invite.style.display = session.LocalNetworking ? DisplayStyle.None : DisplayStyle.Flex;
            invite.SetEnabled(lobby && lobby.HasLobby);
            status.text = session.Status;
            cancel.style.display = idle || inLobby ? DisplayStyle.None : DisplayStyle.Flex;
            cancel.SetEnabled(session.Phase != SessionPhase.Stopping);
            string current = idle ? page : session.Phase.ToString();
            bool changed = current != renderedPage;
            renderedPage = current;
            navigation?.Repair(changed ? Initial() : null);
            if (changed) root.schedule.Execute(() => navigation?.Repair(Initial()));
            PresentationChanged();
        }

        private void OnDisable()
        {
            navigation?.Dispose();
            navigation = null;
            if (settings != null) settings.Changed -= PresentationChanged;
            settings?.Dispose();
            settings = null;
            foreach (Action action in unbind) action();
            unbind.Clear();
            if (session) { session.Changed -= Render; session.InputPresentation.Changed -= PresentationChanged; }
            if (lobby) lobby.Changed -= RenderFriends;
            root?.UnregisterCallback<NavigationCancelEvent>(Cancel);
            root?.UnregisterCallback<FocusInEvent>(RememberFocus);
            entryPanel?.UnregisterCallback<NavigationCancelEvent>(EntryBack, TrickleDown.TrickleDown);
            if (pause != null) pause.performed -= Pause;
            editing = null;
            renderedPage = null;
        }
    }
}
