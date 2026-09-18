using FishNet.Connection;
using FishNet.Object;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.InputSystem.LowLevel;

namespace TwoBirds
{
    public sealed class PlayerInputReader : NetworkBehaviour
    {
        private InputActionMap actions;
        private InputAction move, look, jump, drop, use, exit, interact, lights, horn;
        private PlayerSeating seating;
        private bool exitBlocked, interactBlocked;
        private int suppressedInteractionFrame = -1;
        private PlayerInventory inventory;
        private PlayerEquipment equipment;
        private Vector2 movement;
        private bool jumpPending;
        private bool gameplay;
        private bool inventoryOpen;
        private bool useBlocked;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        internal System.Func<MoveInput> AutomatedInput;
#endif
        public float Yaw { get; private set; }
        public float Pitch { get; private set; }
        public bool InventoryOpen
        {
            get => inventoryOpen;
            set
            {
                ClearContext();
                inventoryOpen = value;
            }
        }
        public bool GameplayActive => gameplay && !InventoryOpen;
        public Vector2 CartMove => GameplayActive && !seating.TransitionPending ? movement : default;
        public bool Handbrake => GameplayActive && !seating.TransitionPending && jump.IsPressed();
        public bool InteractPressed => GameplayActive && !seating.TransitionPending && !interactBlocked &&
            suppressedInteractionFrame != Time.frameCount && interact.WasPressedThisFrame();
        private InputPresentation presentation;
        private InputDevice previousDevice;
        public InputDevice ActiveDevice => presentation?.ActiveDevice;

        public override void OnStartClient()
        {
            if (!IsOwner || SessionController.Instance.Phase == SessionPhase.Stopping) return;
            Yaw = transform.eulerAngles.y;
            Pitch = 0f;
            actions = InputSystem.actions.FindActionMap("Player");
            move = actions.FindAction("Move");
            look = actions.FindAction("Look");
            jump = actions.FindAction("Jump");
            drop = actions.FindAction("Drop");
            use = actions.FindAction("Use");
            exit = actions.FindAction("ExitVehicle");
            interact = actions.FindAction("Interact");
            lights = actions.FindAction("Lights");
            horn = actions.FindAction("Horn");
            seating = GetComponent<PlayerSeating>();
            inventory = GetComponent<PlayerInventory>();
            equipment = GetComponent<PlayerEquipment>();
            presentation = SessionController.Instance.InputPresentation;
            previousDevice = presentation.ActiveDevice;
            presentation.Changed += PresentationChanged;
            InputSystem.onAfterUpdate += ReadInput;
            SetGameplay(false);
            SessionController.Instance.PlayerReady(GetComponent<PlayerMotor>());
        }

        public void SetGameplay(bool value)
        {
            ClearContext();
            if (!value || !IsOwner) CancelUse();
            else if (UseButtonHeld()) useBlocked = true;
            gameplay = value && IsOwner;
            Clear();
            if (actions == null) return;
            if (gameplay) actions.Enable(); else actions.Disable();
            Cursor.lockState = gameplay ? CursorLockMode.Locked : CursorLockMode.None;
            Cursor.visible = !gameplay;
        }

        private void ReadInput()
        {
            if (!IsOwner || InputState.currentUpdateType != UnityEngine.InputSystem.LowLevel.InputUpdateType.Dynamic) return;
            bool blocked = useBlocked;
            if (useBlocked && !UseButtonHeld()) useBlocked = false;
            bool blockExit = exitBlocked;
            if (exitBlocked && !ButtonHeld(exit)) exitBlocked = false;
            if (interactBlocked && !ButtonHeld(interact)) interactBlocked = false;
            if (!GameplayActive) return;
            movement = Vector2.ClampMagnitude(move.ReadValue<Vector2>(), 1f);
            if (!seating.Seated && !seating.TransitionPending) jumpPending |= jump.WasPressedThisFrame();
            Vector2 delta = look.ReadValue<Vector2>();
            float sensitivity = look.activeControl?.device is Gamepad ? 150f * Time.unscaledDeltaTime : 0.12f;
            if (seating.Seated) seating.AddLook(delta.x * sensitivity);
            else Yaw = Mathf.Repeat(Yaw + delta.x * sensitivity, 360f);
            Pitch = Mathf.Clamp(Pitch - delta.y * sensitivity, -89f, 89f);
            if (!blockExit && exit.WasPressedThisFrame() && seating.Seated && !seating.TransitionPending)
            {
                suppressedInteractionFrame = Time.frameCount;
                seating.RequestExit();
                return;
            }
            if (seating.TransitionPending || seating.PlacementPending) return;
            if (seating.IsDriver)
            {
                if (lights.WasPressedThisFrame()) seating.Cart.ToggleLights();
                if (horn.WasPressedThisFrame()) seating.Cart.Honk();
            }
            if (drop.WasPressedThisFrame())
            {
                CancelUse();
                inventory.DropSelected();
                return;
            }
            if (blocked) return;
            if (use.WasPressedThisFrame()) equipment.BeginUse();
            if (use.WasReleasedThisFrame()) equipment.EndUse();
        }

        private bool UseButtonHeld()
        {
            return ButtonHeld(use);
        }

        private void PresentationChanged()
        {
            if (previousDevice != null && !previousDevice.added) ClearContext();
            previousDevice = presentation.ActiveDevice;
        }

        private static bool ButtonHeld(InputAction action)
        {
            if (action == null) return false;
            foreach (var control in action.controls)
                if (control is ButtonControl button && button.isPressed) return true;
            return false;
        }

        private void CancelUse()
        {
            useBlocked = UseButtonHeld();
            equipment?.CancelUse();
        }

        public MoveInput Consume()
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (AutomatedInput != null) return AutomatedInput();
#endif
            if (!gameplay || InventoryOpen || seating.Seated || seating.TransitionPending || seating.PlacementPending) return default;
            Vector3 direction = Quaternion.Euler(0f, Yaw, 0f) * new Vector3(movement.x, 0f, movement.y);
            var result = new MoveInput(new Vector2(direction.x, direction.z), Yaw, jumpPending);
            jumpPending = false;
            return result;
        }

        private void Clear() { movement = default; jumpPending = false; }
        internal void SetWorldYaw(float yaw) => Yaw = Mathf.Repeat(yaw, 360f);
        internal void ClearContext()
        {
            Clear();
            CancelUse();
            exitBlocked = ButtonHeld(exit);
            interactBlocked = ButtonHeld(interact);
            suppressedInteractionFrame = Time.frameCount;
        }
        private void OnApplicationFocus(bool focus)
        {
            if (!focus) ClearContext();
            if (!focus && IsOwner && SessionController.Instance != null) SessionController.Instance.SetPanel(true);
        }
        public override void OnOwnershipClient(NetworkConnection previousOwner)
        {
            if (!IsOwner) Release();
        }
        public override void OnStopClient() => Release();
        private void Release()
        {
            CancelUse();
            InputSystem.onAfterUpdate -= ReadInput;
            if (presentation != null) presentation.Changed -= PresentationChanged;
            presentation = null;
            previousDevice = null;
            if (actions != null) SetGameplay(false);
            actions = null;
            use = null;
        }
    }
}
