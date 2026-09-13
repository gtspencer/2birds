#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.Collections;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.UIElements;

namespace TwoBirds
{
    public sealed class UiInputValidation : MonoBehaviour
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Initialize()
        {
            if (MvpValidation.Argument("-uiInputValidation") != "true") return;
            var go = new GameObject("UI input validation");
            DontDestroyOnLoad(go);
            go.AddComponent<UiInputValidation>();
        }
        private static void Check(bool value, string message)
        {
            if (!value) throw new InvalidOperationException("UI INPUT FAIL: " + message);
            Debug.Log("UI INPUT PASS: " + message);
        }
        private IEnumerator Start()
        {
            while (SessionController.Instance == null || !SessionController.Instance.GameplayAllowed) yield return null;
            var session = SessionController.Instance;
            var root = FindAnyObjectByType<SessionOverlay>().GetComponent<UIDocument>().rootVisualElement;
            var actions = InputSystem.actions;
            var keyboard = InputSystem.AddDevice<Keyboard>();
            var gamepad = InputSystem.AddDevice<Gamepad>();
            try
            {
                Check(!actions.FindAction("UI/Navigate").enabled && !actions.FindAction("UI/Submit").enabled, "gameplay disables UI navigation and submit");
                Check(actions.FindAction("UI/Cancel").enabled && actions.FindAction("UI/Inventory").enabled && actions.FindAction("UI/Slot1").enabled, "gameplay preserves cancel, inventory, and selection shortcuts");
                foreach (var slot in root.Q("hotbar").Query<InventorySlotView>().ToList()) Check(!slot.focusable, "closed hotbar cannot receive navigation focus");
                InputSystem.QueueStateEvent(keyboard, new KeyboardState(Key.W));
                yield return new WaitForSecondsRealtime(0.2f);
                Check(actions.FindAction("Player/Move").ReadValue<Vector2>().y > 0.5f, "W still drives movement");
                Check(root.focusController.focusedElement == null, "held W does not focus HUD controls");
                InputSystem.QueueStateEvent(keyboard, new KeyboardState());
                session.SetInventory(true);
                yield return null;
                Check(actions.FindAction("UI/Navigate").enabled && actions.FindAction("UI/Submit").enabled, "inventory restores UI navigation and submit");
                InputSystem.QueueStateEvent(gamepad, new GamepadState { leftStick = Vector2.up });
                yield return new WaitForSecondsRealtime(0.1f);
                Check(actions.FindAction("UI/Navigate").ReadValue<Vector2>().y > 0.5f, "controller navigation works in inventory");
                InputSystem.QueueStateEvent(gamepad, new GamepadState());
                session.SetInventory(false);
                yield return null;
                Check(root.focusController.focusedElement == null, "closing inventory clears UI focus");
                session.SetPanel(true);
                yield return null;
                Check(actions.FindAction("UI/Navigate").enabled && actions.FindAction("UI/Submit").enabled, "session panel restores navigation and submit");
                session.SetPanel(false);
                InputSystem.QueueStateEvent(keyboard, new KeyboardState(Key.W, Key.Space));
                yield return new WaitForSecondsRealtime(0.2f);
                Check(!session.PanelOpen && root.focusController.focusedElement == null, "movement and jump do not navigate or submit after resume");
                InputSystem.QueueStateEvent(keyboard, new KeyboardState());
                session.Leave();
                while (session.Phase != SessionPhase.Idle) yield return null;
                Check(actions.FindAction("UI/Navigate").enabled && actions.FindAction("UI/Submit").enabled, "main menu navigation restored after leave");
                Debug.Log("UI INPUT VALIDATION COMPLETE");
            }
            finally { InputSystem.RemoveDevice(keyboard); InputSystem.RemoveDevice(gamepad); }
        }
    }
}
#endif
