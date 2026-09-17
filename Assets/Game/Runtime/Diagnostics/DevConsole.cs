#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;
using Cursor = UnityEngine.Cursor;

namespace TwoBirds
{
    public sealed class DevConsole : MonoBehaviour
    {
        private SessionController session;
        private GameObject view;
        private PanelSettings panel;
        private VisualElement modal;
        private ScrollView output;
        private TextField command;
        private Label stats;
        private DevConsoleStats sampler;
        private InputAction toggle, escape;
        private InputActionMap ui;
        private bool uiWasEnabled;
        private bool statsVisible;
        private readonly List<string> history = new();
        private int historyIndex;
        private int frames;
        private double sampleTime;

        private void Start()
        {
            session = GetComponent<SessionController>();
            var source = FindAnyObjectByType<UIDocument>();
            panel = Instantiate(source.panelSettings);
            panel.sortingOrder = 1000;
            view = new GameObject("Dev console UI");
            view.SetActive(false);
            view.transform.SetParent(transform, false);
            var document = view.AddComponent<UIDocument>();
            document.panelSettings = panel;
            view.SetActive(true);
            var root = document.rootVisualElement;
            root.pickingMode = PickingMode.Ignore;
            root.style.flexGrow = 1;
            root.style.color = new Color(0.9f, 0.94f, 0.96f);
            root.style.fontSize = 14;

            modal = new VisualElement();
            modal.style.position = Position.Absolute;
            modal.style.left = modal.style.right = modal.style.top = modal.style.bottom = 0;
            modal.style.backgroundColor = new Color(0, 0, 0, 0.2f);
            root.Add(modal);
            var console = new VisualElement();
            console.style.position = Position.Absolute;
            console.style.left = console.style.right = console.style.bottom = 12;
            console.style.height = 230;
            console.style.backgroundColor = new Color(0.04f, 0.06f, 0.08f, 0.98f);
            console.style.paddingLeft = console.style.paddingRight = 12;
            console.style.paddingTop = console.style.paddingBottom = 8;
            modal.Add(console);
            console.Add(new Label("DEV CONSOLE   ·   ~ close   ·   help"));
            output = new ScrollView();
            output.style.flexGrow = 1;
            console.Add(output);
            command = new TextField { label = ">", maxLength = 256 };
            command.Q("unity-text-input").style.color = Color.black;
            command.labelElement.style.minWidth = 16;
            command.labelElement.style.color = Color.white;
            command.RegisterCallback<KeyDownEvent>(KeyDown, TrickleDown.TrickleDown);
            console.Add(command);
            modal.style.display = DisplayStyle.None;

            stats = new Label { enableRichText = false, pickingMode = PickingMode.Ignore };
            stats.style.position = Position.Absolute;
            stats.style.top = stats.style.right = 12;
            stats.style.backgroundColor = new Color(0.04f, 0.06f, 0.08f, 0.9f);
            stats.style.paddingLeft = stats.style.paddingRight = 10;
            stats.style.paddingTop = stats.style.paddingBottom = 8;
            stats.style.display = DisplayStyle.None;
            root.Add(stats);
            sampler = new DevConsoleStats(session);
            ui = InputSystem.actions.FindActionMap("UI");
            toggle = new InputAction("Dev console", InputActionType.Button, "<Keyboard>/backquote");
            escape = new InputAction("Close dev console", InputActionType.Button, "<Keyboard>/escape");
            toggle.performed += Toggle;
            escape.performed += Escape;
            toggle.Enable();
            session.Changed += SessionChanged;
            Write("Type stats to toggle performance and network stats. Type help for commands.");
        }

        private void Toggle(InputAction.CallbackContext context) => SetOpen(!session.ConsoleOpen);
        private void Escape(InputAction.CallbackContext context) => SetOpen(false);

        private void SetOpen(bool open)
        {
            if (open == session.ConsoleOpen) return;
            if (open)
            {
                uiWasEnabled = ui.enabled;
                ui.Disable();
                escape.Enable();
            }
            session.SetConsoleOpen(open);
            modal.style.display = open ? DisplayStyle.Flex : DisplayStyle.None;
            if (open)
            {
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
                command.SetValueWithoutNotify("");
                historyIndex = history.Count;
                command.schedule.Execute(() => { if (session.ConsoleOpen) command.Focus(); });
            }
            else
            {
                escape.Disable();
                if (uiWasEnabled) ui.Enable();
                command.Blur();
                bool gameplay = session.Phase == SessionPhase.InGame && !session.PanelOpen;
                bool inventoryOpen = session.LocalPlayer && session.LocalPlayer.GetComponent<PlayerInputReader>().InventoryOpen;
                Cursor.lockState = gameplay && !inventoryOpen ? CursorLockMode.Locked : CursorLockMode.None;
                Cursor.visible = !gameplay || inventoryOpen;
            }
        }

        private void SessionChanged()
        {
            sampler.SessionChanged();
            if (!session.ConsoleOpen) return;
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            command.schedule.Execute(() => { if (session.ConsoleOpen) command.Focus(); });
        }

        private void KeyDown(KeyDownEvent evt)
        {
            if (evt.keyCode == KeyCode.BackQuote || evt.character is '`' or '~')
            {
                evt.StopImmediatePropagation();
                return;
            }
            if (evt.keyCode is KeyCode.UpArrow or KeyCode.DownArrow)
            {
                historyIndex = Mathf.Clamp(historyIndex + (evt.keyCode == KeyCode.UpArrow ? -1 : 1), 0, history.Count);
                command.SetValueWithoutNotify(historyIndex < history.Count ? history[historyIndex] : "");
                evt.StopImmediatePropagation();
                return;
            }
            if (evt.keyCode is not (KeyCode.Return or KeyCode.KeypadEnter)) return;
            evt.StopImmediatePropagation();
            string text = command.value.Trim();
            command.SetValueWithoutNotify("");
            if (text.Length == 0) return;
            if (history.Count == 50) history.RemoveAt(0);
            history.Add(text);
            historyIndex = history.Count;
            Write("> " + text);
            var words = text.ToLowerInvariant().Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            switch (words[0])
            {
                case "stats":
                    if (words.Length > 2 || words.Length == 2 && words[1] is not ("on" or "off"))
                    { Write("Usage: stats [on|off]"); break; }
                    statsVisible = words.Length == 1 ? !statsVisible : words[1] == "on";
                    stats.style.display = statsVisible ? DisplayStyle.Flex : DisplayStyle.None;
                    frames = 0;
                    sampleTime = Time.realtimeSinceStartupAsDouble;
                    sampler.BeginSample();
                    stats.text = "Collecting stats…";
                    Write(statsVisible ? "Stats enabled." : "Stats disabled.");
                    break;
                case "help":
                    Write("stats [on|off] — performance/network overlay\nclear — clear output\nhelp — commands\nUp/Down — command history; tilde/Escape — close");
                    break;
                case "clear": output.Clear(); break;
                default: Write("Unknown command. Type help for commands."); break;
            }
        }

        private void Write(string text)
        {
            if (output.contentContainer.childCount == 100) output.RemoveAt(0);
            var line = new Label(text) { enableRichText = false };
            line.style.whiteSpace = WhiteSpace.Normal;
            output.Add(line);
            output.schedule.Execute(() => output.ScrollTo(line));
        }

        private void Update()
        {
            if (!statsVisible) return;
            ++frames;
            double now = Time.realtimeSinceStartupAsDouble;
            double elapsed = now - sampleTime;
            if (elapsed < 0.5) return;
            stats.text = sampler.Sample(frames, elapsed);
            frames = 0;
            sampleTime = now;
        }

        private void OnDestroy()
        {
            if (session)
            {
                session.Changed -= SessionChanged;
                if (session.ConsoleOpen) SetOpen(false);
            }
            toggle?.Dispose();
            escape?.Dispose();
            if (view) Destroy(view);
            if (panel) Destroy(panel);
        }
    }
}
#endif
