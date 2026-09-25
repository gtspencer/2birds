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
        private InputAction move, look, jump, sprint, drop, use, interact, lights, horn, directUse, secondaryInteract, emoteAction;
        private InputAction[] emoteCancels;
        private PlayerEmote emote;
        private PlayerSeating seating;
        private PlayerCarry carry;
        private PlayerHealth health;
        private PlayerNetworkState network;
        private InputAction giveUp;
        private bool giveUpBlocked;
        public bool Carried => carry && carry.IsCarried || seating && seating.AwaitingReference;
        private SessionController session;
        private bool interactBlocked, directUseBlocked, secondaryInteractBlocked, emoteBlocked;
        private int suppressedInteractionFrame = -1;
        private PlayerInventory inventory;
        private PlayerEquipment equipment;
        private Vector2 movement;
        private bool jumpPending;
        private bool gameplay;
        private bool inventoryOpen;
        private bool useBlocked;
        private bool jumpBlocked, dropBlocked, lightsBlocked, hornBlocked;
#if UNITY_INCLUDE_INSTRUMENTATION
        internal System.Func<MoveInput> AutomatedInput;
#endif
#if UNITY_INCLUDE_INSTRUMENTATION
        private bool authoringFocus;
        internal bool AuthoringFocus
        {
            get => authoringFocus;
            set
            {
                if (authoringFocus == value) return;
                authoringFocus = value;
                RefreshInputPresentation();
            }
        }
        internal bool AuthoringCharge { get; private set; }
        internal void BeginAuthoringUse() { AuthoringCharge = true; equipment.BeginUse(); }
        internal void EndAuthoringUse(bool cancel = false)
        {
            if (!AuthoringCharge) return;
            AuthoringCharge = false;
            if (cancel) equipment.CancelUse(); else equipment.EndUse();
        }
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
                RefreshInputPresentation();
            }
        }
        internal EmoteWheelSelection EmoteWheel { get; private set; }
        internal bool EmoteWheelOpen => EmoteWheel != null && EmoteWheel.Open;
        private bool WheelAvailable => GameplayActive && !InputSuppressed && !equipment.IsCharging && emote.CanStart;
        public bool SessionInputAvailable => gameplay && !InventoryOpen;
        public bool GameplayActive => SessionInputAvailable && (!health || health.IsAlive);
        public bool InteractHeld => GameplayActive && !InputSuppressed && !interactBlocked && ButtonHeld(interact);
        public bool GiveUpHeld => SessionInputAvailable && health && health.IsDowned && !InputSuppressed && !giveUpBlocked && ButtonHeld(giveUp);
        public Vector2 CartMove => GameplayActive && !seating.TransitionPending ? movement : default;
        internal bool InputSuppressed =>
#if UNITY_INCLUDE_INSTRUMENTATION
            AuthoringFocus ||
#endif
            presentation == null || presentation.SuppressInput || suppressedInteractionFrame == Time.frameCount;
        public bool Handbrake => GameplayActive && !InputSuppressed && !jumpBlocked && !seating.TransitionPending && jump.IsPressed();
        public bool InteractPressed => !Carried && GameplayActive && !InputSuppressed && !seating.TransitionPending && !interactBlocked &&
            suppressedInteractionFrame != Time.frameCount && interact.WasPressedThisFrame() && !EmoteWheelOpen;
        public bool SecondaryInteractPressed => !Carried && GameplayActive && !InputSuppressed && !seating.TransitionPending &&
            !secondaryInteractBlocked && secondaryInteract.WasPressedThisFrame() && !EmoteWheelOpen && !seating.Seated;
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
            emoteAction = actions.FindAction("Emote");
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
            emoteCancels = new InputAction[7 + PlayerInventory.HotbarSize];
            emoteCancels[0] = use; emoteCancels[1] = directUse; emoteCancels[2] = drop; emoteCancels[3] = interact;
            emoteCancels[4] = secondaryInteract; emoteCancels[5] = actions.FindAction("Previous"); emoteCancels[6] = actions.FindAction("Next");
            for (int i = 0; i < PlayerInventory.HotbarSize; i++) emoteCancels[7 + i] = actions.FindAction($"Hotbar{i + 1}");
            session = SessionController.Instance;
            emote = GetComponent<PlayerAvatarPresentation>().Emote;
            EmoteWheel = new EmoteWheelSelection(session.Emotes);
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
            RefreshInputPresentation();
        }

        private void RefreshInputPresentation()
        {
            bool available = SessionInputAvailable;
#if UNITY_INCLUDE_INSTRUMENTATION
            available &= !AuthoringFocus;
#endif
            presentation?.SetGameplay(available);
        }

        private void ReadInput()
        {
            if (!IsOwner || InputState.currentUpdateType != UnityEngine.InputSystem.LowLevel.InputUpdateType.Dynamic) return;
            bool blockDirect = directUseBlocked;
            if (directUseBlocked && !ButtonHeld(directUse)) directUseBlocked = false;
            bool blockSecondary = secondaryInteractBlocked;
            if (secondaryInteractBlocked && !ButtonHeld(secondaryInteract)) secondaryInteractBlocked = false;
            bool blocked = useBlocked;
            if (useBlocked && !UseButtonHeld()) useBlocked = false;
            bool blockEmote = emoteBlocked;
            if (emoteBlocked && !ButtonHeld(emoteAction)) emoteBlocked = false;
            if (interactBlocked && !ButtonHeld(interact)) interactBlocked = false;
            bool blockJump = jumpBlocked, blockDrop = dropBlocked, blockLights = lightsBlocked, blockHorn = hornBlocked;
            if (jumpBlocked && !ButtonHeld(jump)) jumpBlocked = false;
            if (dropBlocked && !ButtonHeld(drop)) dropBlocked = false;
            if (lightsBlocked && !ButtonHeld(lights)) lightsBlocked = false;
            if (hornBlocked && !ButtonHeld(horn)) hornBlocked = false;
            if (giveUpBlocked && !ButtonHeld(giveUp)) giveUpBlocked = false;
            if (!SessionInputAvailable || InputSuppressed)
            {
                EmoteWheel.Close();
#if UNITY_INCLUDE_INSTRUMENTATION
                if (AuthoringCharge && SessionInputAvailable) return;
#endif
                if (equipment.IsCharging || carry && carry.IsCharging) CancelUse();
                return;
            }
            movement = health.IsAlive ? Vector2.ClampMagnitude(move.ReadValue<Vector2>(), 1f) : Vector2.zero;
            if (emote.Active && movement.sqrMagnitude > 0f) emote.Stop();
            Vector2 delta = look.ReadValue<Vector2>();
            if (EmoteWheel.Open) { UpdateWheel(delta); return; }
            if (health.IsAlive && !Carried && !blockJump && !seating.Seated && !seating.TransitionPending) jumpPending |= jump.WasPressedThisFrame();
            float sensitivity = look.activeControl?.device is Gamepad ?
                session.ControllerSensitivity * Time.unscaledDeltaTime : session.MouseSensitivity;
            if (health.IsAlive && seating.Seated) seating.AddLook(delta.x * sensitivity);
            else Yaw = Mathf.Repeat(Yaw + delta.x * sensitivity, 360f);
            Pitch = Mathf.Clamp(Pitch - delta.y * sensitivity, -89f, 89f);
            if (!network.CanGameplayActions) return;
            if (Carried) { Clear(); return; }
            if (!blockSecondary && secondaryInteract.WasPressedThisFrame() && seating.Seated && !seating.TransitionPending)
            {
                suppressedInteractionFrame = Time.frameCount;
                seating.RequestExit();
                return;
            }
            if (seating.TransitionPending || seating.PlacementPending) return;
            if (!blockEmote && emoteAction.WasPressedThisFrame() && WheelAvailable) { EmoteWheel.Begin(presentation.IsController); return; }
            if (emote.Active && (jump.WasPressedThisFrame() || AnyPressed(emoteCancels))) emote.Stop();
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

        private void UpdateWheel(Vector2 delta)
        {
            if (!WheelAvailable) { CloseWheel(); return; }
            bool confirm = use.WasPressedThisFrame() || jump.WasPressedThisFrame() && jump.activeControl?.device is Gamepad;
            if (confirm && EmoteWheel.Highlight >= 0 || !ButtonHeld(emoteAction)) { Pick(); return; }
            EmoteWheel.Look(delta, look.activeControl?.device is Gamepad);
        }

        private void Pick()
        {
            int slot = EmoteWheel.Highlight;
            CloseWheel();
            if (slot >= 0 && movement == Vector2.zero) emote.Play((byte)slot);
        }

        private void CloseWheel()
        {
            EmoteWheel.Close();
            useBlocked = UseButtonHeld(); jumpBlocked = ButtonHeld(jump); emoteBlocked = ButtonHeld(emoteAction);
        }

        private static bool AnyPressed(InputAction[] actions)
        {
            foreach (var action in actions) if (action.WasPressedThisFrame()) return true;
            return false;
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
#if UNITY_INCLUDE_INSTRUMENTATION
            AuthoringCharge = false;
#endif
            useBlocked = UseButtonHeld();
            equipment?.CancelUse();
        }

        public MoveInput Consume()
        {
            if (Carried || health && health.IsDowned) return default;
#if UNITY_INCLUDE_INSTRUMENTATION
            if (AutomatedInput != null) return AutomatedInput();
#endif
            if (seating.Seated || seating.PlacementPending) return default;
            float facing = emote && emote.Active ? emote.FacingYaw : Yaw;
            if (!gameplay || InputSuppressed || InventoryOpen || seating.TransitionPending)
                return new MoveInput(Vector2.zero, facing, false);
            Vector3 direction = Quaternion.Euler(0f, Yaw, 0f) * new Vector3(movement.x, 0f, movement.y);
            var result = new MoveInput(new Vector2(direction.x, direction.z), facing, jumpPending,
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
            EmoteWheel?.Close();
            emoteBlocked = ButtonHeld(emoteAction);
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
