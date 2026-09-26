#if UNITY_INCLUDE_INSTRUMENTATION
using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace TwoBirds
{
    public enum GripAuthoringPhase { Live, Hold, Charged }
    public enum GripAuthoringMode { HeldItem, WorldContact }

    [DefaultExecutionOrder(250)]
    public sealed class GripAuthoringScene : MonoBehaviour
    {
        public const string SceneName = "AvatarPresentationDemo";
        public const string ScenePath = "Assets/Scenes/AvatarPresentationDemo.unity";
        private const float ObserverOffset = 1.2f, StripSpacing = 0.9f, ReequipDelay = 1f;
        private static readonly GripTarget[] SlotTargets =
        {
            GripTarget.HoldPose, GripTarget.ChargePose, GripTarget.RightHand, GripTarget.LeftHand, GripTarget.PouchDraw,
            GripTarget.RightElbowHold, GripTarget.RightElbowCharge, GripTarget.LeftElbowHold, GripTarget.LeftElbowCharge
        };
        public static GripAuthoringScene Instance { get; private set; }
        public static event Action SceneChanged;
        [SerializeField] private GripObserverPreview observer;
        private SessionController session;
        private PlayerInventory inventory;
        private PlayerEquipment equipment;
        private PlayerInputReader input;
        private PlayerAvatarPresentation avatar;
        private PlayerHeldItemPresentation held;
        private PlayerNetworkState network;
        private PlayerSeating seating;
        private GolfCartNetwork cart;
        private Camera viewCamera;
        private int viewMask;
        private readonly HashSet<uint> supplied = new();
        private readonly Dictionary<Transform, int> originalLayers = new();
        private readonly List<GripObserverPreview> strip = new();
        private readonly List<GripTarget> phaseTargets = new();
        private byte selectedItem, pendingItem;
        private bool attached, attaching, choosing, cleaning;
        private Coroutine throwing, reequipping;
        private UnityEngine.InputSystem.InputAction walkToggle;
        public bool Attached => attached;
        public AvatarId SelectedAvatar { get; private set; }
        public ItemDefinition SelectedDefinition => attached ? WorldItemRegistry.Instance.GetDefinition(selectedItem) : null;
        public GripAuthoringPhase Phase { get; private set; }
        public GripAuthoringMode Mode { get; private set; }
        public bool FirstPerson { get; private set; }
        public bool Mirror { get; private set; }
        public HoldSlotMode SlotMode => SelectedDefinition ? SelectedDefinition.HoldMode : HoldSlotMode.Hand;
        public bool AutoReequip { get; set; } = true;
        public bool Walking => input && !input.AuthoringFocus;
        public string Message { get; private set; } = "";
        public string ActionPhase => network ? network.ItemAction.State.ToString() : "Waiting for Solo";
        public Camera ViewCamera => viewCamera;
        public bool HasCart => cart;
        public bool Seated => seating && seating.IsDriver;
        public AvatarHandContact LeftContact => cart ? cart.Presentation.LeftHandContact : null;
        public AvatarHandContact RightContact => cart ? cart.Presentation.RightHandContact : null;
        private HeldItemPresentationState EditedState => !attached ? null : FirstPerson ? held.State : observer.State;

        private IEnumerator Start()
        {
            Instance = this;
            while (!SessionController.Instance || !SessionController.Instance.Network) yield return null;
            session = SessionController.Instance;
            session.Changed += SessionChanged;
            if (session.Phase == SessionPhase.Idle) session.StartGripAuthoring();
            SessionChanged();
        }
        private void SessionChanged()
        {
            if (attached || attaching || session.Phase != SessionPhase.InGame || !session.LocalPlayer) return;
            StartCoroutine(Attach());
        }
        private IEnumerator Attach()
        {
            attaching = true;
            // InGame can fire from inside the local player's OnStartClient; let its start cycle finish.
            yield return null;
            attaching = false;
            if (attached || session.Phase != SessionPhase.InGame || !session.LocalPlayer) yield break;
            attached = true;
            var player = session.LocalPlayer;
            inventory = player.GetComponent<PlayerInventory>(); equipment = player.GetComponent<PlayerEquipment>();
            avatar = player.GetComponent<PlayerAvatarPresentation>(); held = player.GetComponent<PlayerHeldItemPresentation>();
            network = player.GetComponent<PlayerNetworkState>(); input = player.GetComponent<PlayerInputReader>();
            seating = player.GetComponent<PlayerSeating>();
            var presentation = player.GetComponent<PlayerPresentation>();
            viewCamera = presentation.ViewCamera;
            viewMask = viewCamera.cullingMask;
            viewCamera.cullingMask &= ~LayerMask.GetMask(AvatarEditorPreview.LayerName);
            observer.Initialize(avatar, session.Avatars, ObserverOffset);
            AssignOwnerLayer(presentation.Graphics);
            avatar.Presentation.DidBind += OwnerBound;
            avatar.Hands.LocalBound += LocalBound;
            inventory.InventoryChanged += InventoryChanged; network.ActionChanged += ActionChanged;
            walkToggle = new UnityEngine.InputSystem.InputAction("Grip walking", binding: "<Keyboard>/f2");
            walkToggle.performed += _ => SetWalking(!Walking);
            walkToggle.Enable();
            SetWalking(false);
            SelectedAvatar = avatar.Presentation.Resolved != null ? avatar.Presentation.Resolved.Id : session.Avatars.DefaultId;
            RebuildStrip();
            SceneChanged?.Invoke();
        }
        private void OwnerBound(AvatarBinding binding) => AssignOwnerLayer(binding.Animator.transform);
        private void LocalBound(AvatarBinding binding) => ApplyPhase();
        private void AssignOwnerLayer(Transform root)
        {
            int layer = LayerMask.NameToLayer("GripAuthoringOwner");
            foreach (var child in root.GetComponentsInChildren<Transform>(true))
            { if (!originalLayers.ContainsKey(child)) originalLayers.Add(child, child.gameObject.layer); child.gameObject.layer = layer; }
        }

        public void SetWalking(bool walking) { if (input) input.AuthoringFocus = !walking; }

        public void SelectAvatar(AvatarId id)
        {
            if (!attached || !id.IsValid) return;
            Cancel();
            SelectedAvatar = id; avatar.RequestAuthoringAvatar(id);
            RebuildStrip();
        }
        private void RebuildStrip()
        {
            foreach (var preview in strip) if (preview) Destroy(preview.gameObject);
            strip.Clear();
            int index = 0;
            foreach (var entry in session.Avatars.Entries)
            {
                if (entry.Id == SelectedAvatar) continue;
                var root = new GameObject($"Grip strip {entry.Id}");
                root.transform.SetParent(observer.transform.parent, false);
                root.AddComponent<AvatarPresentation>();
                var preview = root.AddComponent<GripObserverPreview>();
                preview.Initialize(avatar, session.Avatars, ObserverOffset + ++index * StripSpacing, entry.Id);
                root.SetActive(Mode == GripAuthoringMode.HeldItem);
                strip.Add(preview);
            }
            ApplyPhase();
        }
        public void SelectItem(byte id)
        {
            if (!attached) return;
            Cancel();
            selectedItem = pendingItem = id;
            if (Mode == GripAuthoringMode.HeldItem) Equip();
            ApplyPhase();
        }

        public void SetPhase(GripAuthoringPhase phase)
        {
            if (phase != GripAuthoringPhase.Live) Cancel();
            // Hold and Charged share the locked transforms; only entering or leaving Live re-applies them.
            bool reapply = Phase == GripAuthoringPhase.Live || phase == GripAuthoringPhase.Live;
            Phase = phase;
            ApplyPhase(reapply);
        }

        public void SetMirror(bool mirror)
        {
            Mirror = mirror;
            ApplyPhase(false);
        }

        public void SetView(bool firstPerson)
        {
            FirstPerson = firstPerson;
            ApplyPhase();
        }

        public void SetMode(GripAuthoringMode mode)
        {
            if (!attached || Mode == mode) return;
            Cancel();
            Mode = mode;
            bool contact = mode == GripAuthoringMode.WorldContact;
            if (contact)
            {
                inventory.SelectSlot(-1);
                if (!cart) cart = FindFirstObjectByType<GolfCartNetwork>();
                if (!cart) Message = "Add a GolfCart to the authoring scene to author world contacts.";
                else EnterSeat();
            }
            else
            {
                AvatarHandContact.LiveView = null;
                ExitSeat();
            }
            observer.SetLateral(contact ? 0f : ObserverOffset);
            observer.ContactSource = contact ? avatar.Hands : null;
            foreach (var preview in strip) if (preview) preview.gameObject.SetActive(!contact);
            ApplyPhase();
            if (!contact) Equip();
        }

        public void EnterSeat()
        {
            if (!attached || !cart || seating.Seated) return;
            Message = !input.GameplayActive ? "Can't enter the seat: gameplay input is unavailable." :
                seating.TransitionPending ? "Can't enter the seat: a seat transition is in progress." : "";
            if (Message.Length == 0) seating.Request(cart, 0);
        }
        public void ExitSeat() { if (attached && seating.Seated) seating.RequestExit(); }

        private void ApplyPhase(bool reapply = true)
        {
            if (!attached) return;
            var edited = Mode == GripAuthoringMode.HeldItem ? EditedState : null;
            Apply(held.State, edited, reapply); Apply(observer.State, edited, reapply);
            foreach (var preview in strip) if (preview) Apply(preview.State, edited, reapply);
            if (reapply) ApplyContacts();
        }
        private void Apply(HeldItemPresentationState state, HeldItemPresentationState edited, bool reapply)
        {
            if (state == null) return;
            state.AuthoringPhase = Phase;
            state.AuthoringMirror = Mirror && state == edited;
            if (!reapply) return;
            state.AuthoringLocked = false;
            state.ReapplyAuthored();
            if (state == edited) state.AuthoringLocked = Phase != GripAuthoringPhase.Live;
        }
        private void ApplyContacts()
        {
            if (Mode != GripAuthoringMode.WorldContact || !cart) return;
            AvatarHandContact.LiveView = FirstPerson;
            if (LeftContact) LeftContact.ApplyAuthored(SelectedAvatar, FirstPerson);
            if (RightContact) RightContact.ApplyAuthored(SelectedAvatar, FirstPerson);
        }

        public IReadOnlyList<GripTarget> PhaseTargets()
        {
            phaseTargets.Clear();
            var mode = SlotMode;
            if (Phase == GripAuthoringPhase.Live)
            {
                foreach (var target in SlotTargets) if (GripPoses.Uses(mode, target)) phaseTargets.Add(target);
                return phaseTargets;
            }
            bool charged = Phase == GripAuthoringPhase.Charged;
            phaseTargets.Add(charged ? GripTarget.ChargePose : GripTarget.HoldPose);
            phaseTargets.Add(GripTarget.RightHand);
            phaseTargets.Add(charged ? GripTarget.RightElbowCharge : GripTarget.RightElbowHold);
            if (mode == HoldSlotMode.Heavy)
            {
                phaseTargets.Add(GripTarget.LeftHand);
                phaseTargets.Add(charged ? GripTarget.LeftElbowCharge : GripTarget.LeftElbowHold);
            }
            else if (mode == HoldSlotMode.Slingshot && charged)
            {
                phaseTargets.Add(GripTarget.LeftHand); phaseTargets.Add(GripTarget.PouchDraw); phaseTargets.Add(GripTarget.LeftElbowCharge);
            }
            return phaseTargets;
        }

        public bool TryTarget(GripTarget target, out Transform transform, out Pose stored, out GripLayer layer, out bool dirty)
        {
            transform = null; stored = default; layer = GripLayer.None; dirty = false;
            var state = EditedState;
            return state != null && state.TryAuthored(target, out transform, out stored, out layer, out dirty);
        }

        public void ResetTarget(GripTarget target) => EditedState?.ResetAuthored(target);

        public void Reapply()
        {
            EditedState?.ReapplyAuthored();
            ApplyContacts();
        }

        public void Equip()
        {
            if (!attached || choosing || selectedItem == 0) return;
            pendingItem = selectedItem;
            if (network.ItemAction.State == ItemActionState.Recovering) return;
            choosing = true;
            try
            {
                Cleanup();
                foreach (uint id in supplied)
                    if (WorldItemRegistry.Instance.TryGetRecord(id, out var retained) && retained.State == WorldItemState.Held && retained.DefinitionId != pendingItem)
                    { Message = "The previous supplied item is still held. Equip again when it can be dropped."; return; }
                Message = "";
                for (int slot = 0; slot < inventory.Count; slot++)
                {
                    var stack = inventory.GetSlot(slot);
                    if (stack.IsEmpty || stack.ItemId != pendingItem) continue;
                    if (slot >= PlayerInventory.HotbarSize) { inventory.SwapSlots(slot, 0); slot = 0; }
                    if (inventory.SelectedSlot != slot) inventory.SelectSlot((sbyte)slot);
                    pendingItem = 0; return;
                }
                var item = WorldItemRegistry.Instance.SupplyAuthoringItem(pendingItem, session.LocalPlayer.transform.position + Vector3.up);
                if (!item) return;
                supplied.Add(item.Record.Motion.Id); inventory.Collect(item);
            }
            finally { choosing = false; }
            InventoryChanged();
        }
        private bool Holds(byte id)
        {
            for (int i = 0; i < inventory.Count; i++)
                if (!inventory.GetSlot(i).IsEmpty && inventory.GetSlot(i).ItemId == id) return true;
            return false;
        }
        private void InventoryChanged()
        {
            if (choosing) return;
            if (pendingItem != 0) { if (Holds(pendingItem)) Equip(); return; }
            QueueReequip();
        }
        private void ActionChanged()
        {
            if (network.ItemAction.State != ItemActionState.Idle) return;
            if (pendingItem != 0) { Cleanup(); Equip(); }
            else QueueReequip();
        }
        private void QueueReequip()
        {
            if (!AutoReequip || Mode != GripAuthoringMode.HeldItem || reequipping != null || selectedItem == 0 ||
                network.ItemAction.State != ItemActionState.Idle || Holds(selectedItem)) return;
            reequipping = StartCoroutine(Reequip());
        }
        private IEnumerator Reequip()
        {
            yield return new WaitForSecondsRealtime(ReequipDelay);
            reequipping = null;
            if (AutoReequip && Mode == GripAuthoringMode.HeldItem && network.ItemAction.State == ItemActionState.Idle && !Holds(selectedItem)) Equip();
        }
        private void Cleanup()
        {
            if (cleaning || network.ItemAction.State == ItemActionState.Recovering) return;
            cleaning = true;
            var remove = new List<uint>();
            foreach (uint id in supplied)
            {
                if (!WorldItemRegistry.Instance.TryGetRecord(id, out var record)) { remove.Add(id); continue; }
                if (record.DefinitionId == selectedItem && record.State == WorldItemState.Held) continue;
                if (record.State == WorldItemState.Held)
                    for (int slot = 0; slot < inventory.Count; slot++)
                    {
                        var stack = inventory.GetSlot(slot);
                        if (!stack.IsEmpty && Array.IndexOf(stack.WorldIds, id) >= 0) { inventory.DropSlot(slot); break; }
                    }
                if (WorldItemRegistry.Instance.TryGetRecord(id, out record) && record.State != WorldItemState.Held)
                { WorldItemRegistry.Instance.Remove(id); remove.Add(id); }
            }
            foreach (uint id in remove) supplied.Remove(id);
            cleaning = false;
        }

        private void Act(Action action) { if (!attached) return; SetPhase(GripAuthoringPhase.Live); action(); }
        public void EquipAction() => Act(Equip);
        public void Dequip() => Act(() => { Cancel(); inventory.SelectSlot(-1); });
        public void Hold() => Act(() => { Cancel(); input.BeginAuthoringUse(); });
        public void Release() => Act(() => input.EndAuthoringUse());
        public void Throw() => Act(() =>
        {
            Cancel();
            var item = SelectedDefinition;
            if (!item) return;
            throwing = StartCoroutine(ThrowAfter(Mathf.Max(item.ThrowChargeTime, item.ChargePoseDuration)));
        });
        public void CancelAction() => Act(Cancel);
        public void Drop() => Act(() => { Cancel(); inventory.DropSelected(); });
        public void Use() => Act(() => { Cancel(); equipment.DirectUse(); });
        private IEnumerator ThrowAfter(float seconds)
        {
            input.BeginAuthoringUse();
            yield return new WaitForSecondsRealtime(seconds);
            throwing = null;
            input.EndAuthoringUse();
        }
        private void Cancel()
        {
            if (!input) return;
            if (throwing != null) { StopCoroutine(throwing); throwing = null; }
            input.EndAuthoringUse(true); equipment.CancelUse();
        }

        internal bool TryRig(bool firstPerson, out HeldItemPresentationState state, out AvatarBinding binding)
        {
            state = null; binding = null;
            if (!attached) return false;
            state = firstPerson ? held.State : observer.State;
            binding = firstPerson ? avatar.Hands.LocalBinding : observer.Presentation.Binding;
            return state != null && binding != null;
        }

        public string Readout(bool firstPerson)
        {
            if (!TryRig(firstPerson, out var state, out _)) return "Waiting for presentation";
            var value = state.Readout;
            string status = value.ActiveBlend ? "Active blend" : value.RightUnreachable || value.LeftUnreachable
                ? $"Unreachable palm: {(value.LeftUnreachable ? "Left " : "")}{(value.RightUnreachable ? "Right" : "")}" : "Contact";
            if (value.ClearanceAdjusted) status += " · clearance adjusted";
            return $"{status}\nRight {value.RightPositionError:F4} m / {value.RightAngleError:F1}°" +
                (value.HasLeft ? $" · Left {value.LeftPositionError:F4} m / {value.LeftAngleError:F1}°" : "");
        }
        public string StripReadouts()
        {
            if (!SelectedDefinition) return "";
            bool left = SlotMode != HoldSlotMode.Hand;
            var text = new StringBuilder();
            Shortfall(text, observer, left);
            foreach (var preview in strip) Shortfall(text, preview, left);
            return text.ToString();
        }
        private static void Shortfall(StringBuilder text, GripObserverPreview preview, bool left)
        {
            if (!preview || preview.State == null) return;
            var readout = preview.State.Readout;
            string warning = readout.RightUnreachable || readout.LeftUnreachable ? " · unreachable" : "";
            text.AppendLine($"{preview.DisplayName}: short R {readout.RightPositionError * 100f:F1} cm" +
                (left ? $" · L {readout.LeftPositionError * 100f:F1} cm" : "") + warning);
        }

        private void OnDestroy()
        {
            foreach (var preview in strip) if (preview) Destroy(preview.gameObject);
            strip.Clear();
            if (session) session.Changed -= SessionChanged;
            if (attached)
            {
                Phase = GripAuthoringPhase.Live; ApplyPhase();
                Cancel(); SetWalking(true);
                if (inventory) inventory.InventoryChanged -= InventoryChanged;
                if (network) network.ActionChanged -= ActionChanged;
                if (avatar)
                {
                    avatar.Presentation.DidBind -= OwnerBound;
                    if (avatar.Hands) avatar.Hands.LocalBound -= LocalBound;
                }
                if (viewCamera) viewCamera.cullingMask = viewMask;
            }
            AvatarHandContact.LiveView = null;
            foreach (var entry in originalLayers) if (entry.Key) entry.Key.gameObject.layer = entry.Value;
            walkToggle?.Dispose();
            if (Instance == this) Instance = null;
            SceneChanged?.Invoke();
        }
    }
}
#endif
