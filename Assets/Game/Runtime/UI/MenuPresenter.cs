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
        private readonly List<Action> unbind = new();
        private List<EndpointUtility.LanAddress> addresses = new();
        private string page = "main";
        private InputAction cancel;

        private void Start() => Bind();
        private void OnEnable() { if (session != null) Bind(); }
        private void Bind()
        {
            root = GetComponent<UIDocument>().rootVisualElement;
            session = SessionController.Instance;
            Click("solo", () => session.StartSession(SessionMode.Solo, "127.0.0.1", "7770"));
            Click("host", () => { Refresh(); Show("host"); });
            Click("join", () => Show("join"));
            Click("host-back", () => Show("main"));
            Click("join-back", () => Show("main"));
            Click("refresh", Refresh);
            Click("copy", () => GUIUtility.systemCopyBuffer = Endpoint());
            Click("start-host", () => session.StartSession(SessionMode.Host, "127.0.0.1", root.Q<TextField>("host-port").value, SelectedAddress()));
            Click("connect", () => session.StartSession(SessionMode.Join, root.Q<TextField>("join-ip").value, root.Q<TextField>("join-port").value));
            Click("cancel", () => session.Leave());
            root.Q<TextField>("host-port").RegisterValueChangedCallback(PortChanged);
            root.Q<DropdownField>("lan-address").RegisterValueChangedCallback(AddressChanged);
            session.Changed += Render;
            cancel = InputSystem.actions.FindAction("UI/Cancel");
            cancel.performed += Cancel;
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
            if (session.Phase != SessionPhase.Idle) session.Leave(); else Show("main");
        }
        private void PortChanged(ChangeEvent<string> evt) => RenderEndpoint();
        private void AddressChanged(ChangeEvent<string> evt) => RenderEndpoint();
        private void Refresh()
        {
            try { addresses = EndpointUtility.Discover(); }
            catch (System.Net.NetworkInformation.NetworkInformationException) { addresses.Clear(); }
            var field = root.Q<DropdownField>("lan-address");
            field.choices = addresses.ConvertAll(a => a.Label);
            field.SetEnabled(addresses.Count > 0);
            field.index = addresses.Count > 0 ? 0 : -1;
            RenderEndpoint();
        }
        private string SelectedAddress()
        {
            int index = root.Q<DropdownField>("lan-address").index;
            return index >= 0 && index < addresses.Count ? addresses[index].Address : "";
        }
        private string Endpoint() => $"{SelectedAddress()}:{root.Q<TextField>("host-port").value.Trim()}";
        private void RenderEndpoint()
        {
            bool available = SelectedAddress() != "";
            root.Q<Label>("endpoint").text = available ? Endpoint() : "No LAN IPv4 address found. Only same-machine access is available.";
            root.Q<Button>("copy").SetEnabled(available && EndpointUtility.TryPort(root.Q<TextField>("host-port").value, out _));
        }
        private void Show(string next)
        {
            page = next;
            foreach (string name in new[] { "main", "host", "join" })
                root.Q(name + "-page").style.display = name == page ? DisplayStyle.Flex : DisplayStyle.None;
            root.Q(page + "-page").Q<Button>()?.Focus();
        }
        private void Render()
        {
            bool idle = session.Phase == SessionPhase.Idle;
            foreach (string name in new[] { "main", "host", "join" }) root.Q(name + "-page").SetEnabled(idle);
            root.Q<Label>("status").text = session.Status;
            root.Q<Button>("cancel").style.display = idle ? DisplayStyle.None : DisplayStyle.Flex;
            root.Q<Button>("cancel").SetEnabled(session.Phase != SessionPhase.Stopping);
        }
        private void OnDisable()
        {
            foreach (Action action in unbind) action();
            unbind.Clear();
            if (session != null) session.Changed -= Render;
            if (cancel != null) cancel.performed -= Cancel;
            root?.Q<TextField>("host-port").UnregisterValueChangedCallback(PortChanged);
            root?.Q<DropdownField>("lan-address").UnregisterValueChangedCallback(AddressChanged);
        }
    }
}
