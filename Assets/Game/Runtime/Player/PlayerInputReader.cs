using FishNet.Connection;
using FishNet.Object;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;

namespace TwoBirds
{
    public sealed class PlayerInputReader : NetworkBehaviour
    {
        private InputActionMap actions;
        private InputAction move, look, jump;
        private Vector2 movement;
        private bool jumpPending;
        private bool gameplay;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        internal System.Func<MoveInput> AutomatedInput;
#endif
        public float Yaw { get; private set; }
        public float Pitch { get; private set; }
        public bool InventoryOpen { get; set; }

        public override void OnStartClient()
        {
            if (!IsOwner || SessionController.Instance.Phase == SessionPhase.Stopping) return;
            Yaw = transform.eulerAngles.y;
            Pitch = 0f;
            actions = InputSystem.actions.FindActionMap("Player");
            move = actions.FindAction("Move");
            look = actions.FindAction("Look");
            jump = actions.FindAction("Jump");
            InputSystem.onAfterUpdate += ReadInput;
            SetGameplay(true);
            SessionController.Instance.PlayerReady(GetComponent<PlayerMotor>());
        }

        public void SetGameplay(bool value)
        {
            gameplay = value && IsOwner;
            Clear();
            if (actions == null) return;
            if (gameplay) actions.Enable(); else actions.Disable();
            Cursor.lockState = gameplay ? CursorLockMode.Locked : CursorLockMode.None;
            Cursor.visible = !gameplay;
        }

        private void ReadInput()
        {
            if (!gameplay || InventoryOpen || !IsOwner || InputState.currentUpdateType != UnityEngine.InputSystem.LowLevel.InputUpdateType.Dynamic) return;
            movement = Vector2.ClampMagnitude(move.ReadValue<Vector2>(), 1f);
            jumpPending |= jump.WasPressedThisFrame();
            Vector2 delta = look.ReadValue<Vector2>();
            float sensitivity = look.activeControl?.device is Gamepad ? 150f * Time.unscaledDeltaTime : 0.12f;
            Yaw = Mathf.Repeat(Yaw + delta.x * sensitivity, 360f);
            Pitch = Mathf.Clamp(Pitch - delta.y * sensitivity, -89f, 89f);
        }

        public MoveInput Consume()
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (AutomatedInput != null) return AutomatedInput();
#endif
            if (!gameplay || InventoryOpen) return default;
            Vector3 direction = Quaternion.Euler(0f, Yaw, 0f) * new Vector3(movement.x, 0f, movement.y);
            var result = new MoveInput(new Vector2(direction.x, direction.z), Yaw, jumpPending);
            jumpPending = false;
            return result;
        }

        private void Clear() { movement = default; jumpPending = false; }
        private void OnApplicationFocus(bool focus)
        {
            if (!focus && IsOwner && SessionController.Instance != null) SessionController.Instance.SetPanel(true);
        }
        public override void OnOwnershipClient(NetworkConnection previousOwner)
        {
            if (!IsOwner) Release();
        }
        public override void OnStopClient() => Release();
        private void Release()
        {
            InputSystem.onAfterUpdate -= ReadInput;
            if (actions != null) SetGameplay(false);
            actions = null;
        }
    }
}
