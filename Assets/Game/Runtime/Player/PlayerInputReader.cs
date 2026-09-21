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
        private InputAction move, look, jump, sprint, drop, use, exit, interact, lights, horn, directUse, secondaryInteract;
        private PlayerSeating seating;
        private PlayerCarry carry;
        private PlayerHealth health;
        private PlayerNetworkState network;
        private InputAction giveUp;
        private bool giveUpBlocked;
        public bool Carried => carry && carry.IsCarried || seating && seating.AwaitingReference;
        private SessionController session;
        private bool exitBlocked, interactBlocked, directUseBlocked, secondaryInteractBlocked;
        private int suppressedInteractionFrame = -1;
        private PlayerInventory inventory;
        private PlayerEquipment equipment;
        private Vector2 movement;
        private bool jumpPending;
        private bool gameplay;
        private bool inventoryOpen;
        private bool useBlocked;
        private bool jumpBlocked, dropBlocked, lightsBlocked, hornBlocked;
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
                if (inventoryOpen == value) return;
                ClearContext();
                inventoryOpen = value;
                presentation?.SetGameplay(SessionInputAvailable);
            }
        }
        public bool SessionInputAvailable => gameplay && !InventoryOpen;
        public bool GameplayActive => SessionInputAvailable && (!health || health.IsAlive);
        public bool InteractHeld => GameplayActive && !InputSuppressed && !interactBlocked && ButtonHeld(interact);
        public bool GiveUpHeld => SessionInputAvailable && health && health.IsDowned && !InputSuppressed && !giveUpBlocked && ButtonHeld(giveUp);
        public Vector2 CartMove => GameplayActive && !seating.TransitionPending ? movement : default;
        internal bool InputSuppressed => presentation == null || presentation.SuppressInput || suppressedInteractionFrame == Time.frameCount;
        public bool Handbrake => GameplayActive && !jumpBlocked && !seating.TransitionPending && jump.IsPressed();
        public bool InteractPressed => !Carried && GameplayActive && !seating.TransitionPending && !interactBlocked &&
            suppressedInteractionFrame != Time.frameCount && interact.WasPressedThisFrame();
        public bool SecondaryInteractPressed => !Carried && GameplayActive && !InputSuppressed && !seating.TransitionPending &&
            !secondaryInteractBlocked && secondaryInteract.WasPressedThisFrame();
        private InputPresentation presentation;
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
            directUse = actions.FindAction("DirectUse");
            secondaryInteract = actions.FindAction("SecondaryInteract");
            exit = actions.FindAction("ExitVehicle");
            interact = actions.FindAction("Interact");
            lights = actions.FindAction("Lights");
            horn = actions.FindAction("Horn");
            sprint = actions.FindAction("Sprint");
            giveUp = actions.FindAction("GiveUp");
            health = GetComponent<PlayerHealth>();
            network = GetComponent<PlayerNetworkState>();
            seating = GetComponent<PlayerSeating>();
            carry = GetComponent<PlayerCarry>();
            inventory = GetComponent<PlayerInventory>();
            equipment = GetComponent<PlayerEquipment>();
            session = SessionController.Instance;
            presentation = session.InputPresentation;
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
            presentation?.SetGameplay(SessionInputAvailable);
        }

        private void ReadInput()
        {
            if (!IsOwner || InputState.currentUpdateType != UnityEngine.InputSystem.LowLevel.InputUpdateType.Dynamic) return;
            bool blockDirect = directUseBlocked;
            if (directUseBlocked && !ButtonHeld(directUse)) directUseBlocked = false;
            if (secondaryInteractBlocked && !ButtonHeld(secondaryInteract)) secondaryInteractBlocked = false;
            bool blocked = useBlocked;
            if (useBlocked && !UseButtonHeld()) useBlocked = false;
            bool blockExit = exitBlocked;
            if (exitBlocked && !ButtonHeld(exit)) exitBlocked = false;
            if (interactBlocked && !ButtonHeld(interact)) interactBlocked = false;
            bool blockJump = jumpBlocked, blockDrop = dropBlocked, blockLights = lightsBlocked, blockHorn = hornBlocked;
            if (jumpBlocked && !ButtonHeld(jump)) jumpBlocked = false;
            if (dropBlocked && !ButtonHeld(drop)) dropBlocked = false;
            if (lightsBlocked && !ButtonHeld(lights)) lightsBlocked = false;
            if (hornBlocked && !ButtonHeld(horn)) hornBlocked = false;
            if (giveUpBlocked && !ButtonHeld(giveUp)) giveUpBlocked = false;
            if (!SessionInputAvailable || presentation.SuppressInput || suppressedInteractionFrame == Time.frameCount)
            {
                if (equipment.IsCharging || carry && carry.IsCharging) CancelUse();
                return;
            }
            movement = health.IsAlive ? Vector2.ClampMagnitude(move.ReadValue<Vector2>(), 1f) : Vector2.zero;
            if (health.IsAlive && !Carried && !blockJump && !seating.Seated && !seating.TransitionPending) jumpPending |= jump.WasPressedThisFrame();
            Vector2 delta = look.ReadValue<Vector2>();
            float sensitivity = look.activeControl?.device is Gamepad ?
                session.ControllerSensitivity * Time.unscaledDeltaTime : session.MouseSensitivity;
            if (health.IsAlive && seating.Seated) seating.AddLook(delta.x * sensitivity);
            else Yaw = Mathf.Repeat(Yaw + delta.x * sensitivity, 360f);
            Pitch = Mathf.Clamp(Pitch - delta.y * sensitivity, -89f, 89f);
            if (!network.CanGameplayActions) return;
            if (Carried) { Clear(); return; }
            if (!blockExit && exit.WasPressedThisFrame() && seating.Seated && !seating.TransitionPending)
            {
                suppressedInteractionFrame = Time.frameCount;
                seating.RequestExit();
                return;
            }
            if (seating.TransitionPending || seating.PlacementPending) return;
            if (seating.IsDriver)
            {
                if (!blockLights && lights.WasPressedThisFrame()) seating.Cart.ToggleLights();
                if (!blockHorn && horn.WasPressedThisFrame()) seating.Cart.Honk();
            }
            if (!blockDrop && drop.WasPressedThisFrame())
            {
                CancelUse();
                inventory.DropSelected();
                return;
            }
            if (!blockDirect && directUse.WasPressedThisFrame())
            {
                CancelUse();
                equipment.DirectUse();
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
            if (Carried || health && health.IsDowned) return default;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (AutomatedInput != null) return AutomatedInput();
#endif
            if (!gameplay || InventoryOpen || seating.Seated || seating.TransitionPending || seating.PlacementPending) return default;
            Vector3 direction = Quaternion.Euler(0f, Yaw, 0f) * new Vector3(movement.x, 0f, movement.y);
            var result = new MoveInput(new Vector2(direction.x, direction.z), Yaw, jumpPending,
                sprint != null && sprint.IsPressed() && movement.sqrMagnitude > 0.0001f);
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
            directUseBlocked = ButtonHeld(directUse);
            secondaryInteractBlocked = ButtonHeld(secondaryInteract);
            jumpBlocked = ButtonHeld(jump);
            dropBlocked = ButtonHeld(drop);
            lightsBlocked = ButtonHeld(lights);
            hornBlocked = ButtonHeld(horn);
            giveUpBlocked = ButtonHeld(giveUp);
            suppressedInteractionFrame = Time.frameCount;
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
            if (actions != null) SetGameplay(false);
            presentation = null;
            actions = null;
            use = null;
        }
    }
}
