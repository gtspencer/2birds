#if UNITY_INCLUDE_INSTRUMENTATION
using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace TwoBirds
{
    public enum GripAuthoringPhase { Live, Hold, Charged }

    public struct GripPoseCapture
    {
        public HoldClass Class;
        public bool FirstPerson, Charged;
        public float[] Muscles;
        public float Spread;
    }

    [DefaultExecutionOrder(250)]
    public sealed class GripAuthoringScene : MonoBehaviour
    {
        public const string SceneName = "AvatarPresentationDemo";
        public const string ScenePath = "Assets/Scenes/AvatarPresentationDemo.unity";
        private const float ObserverOffset = 1.2f, StripSpacing = 0.9f, ReequipDelay = 1f;
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
        private Camera viewCamera;
        private int viewMask;
        private readonly HashSet<uint> supplied = new();
        private readonly Dictionary<Transform, int> originalLayers = new();
        private readonly List<GripObserverPreview> strip = new();
        private HeldItemPresentationState editState;
        private AvatarBinding editBinding;
        private bool editFirstPerson, dragRight;
        private byte selectedItem, pendingItem;
        private bool attached, attaching, choosing, cleaning;
        private Coroutine throwing, reequipping;
        private UnityEngine.InputSystem.InputAction walkToggle;
        public bool Attached => attached;
        public AvatarId SelectedAvatar { get; private set; }
        public ItemDefinition SelectedDefinition => attached ? WorldItemRegistry.Instance.GetDefinition(selectedItem) : null;
        public GripAuthoringPhase Phase { get; private set; }
        public bool AutoReequip { get; set; } = true;
        public bool Walking => input && !input.AuthoringFocus;
        public bool Dragging => editState != null;
        public string Message { get; private set; } = "";
        public string ActionPhase => network ? network.ItemAction.State.ToString() : "Waiting for Solo";
        public Camera ViewCamera => viewCamera;

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
            Cancel(); EndEdit();
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
                strip.Add(preview);
            }
            ApplyPhase();
        }
        public void SelectItem(byte id)
        {
            if (!attached) return;
            Cancel(); EndEdit();
            selectedItem = pendingItem = id;
            Equip();
        }

        public void SetPhase(GripAuthoringPhase phase)
        {
            if (phase != GripAuthoringPhase.Live) Cancel();
            EndEdit();
            Phase = phase;
            ApplyPhase();
        }
        private void ApplyPhase()
        {
            if (!attached) return;
            Apply(held.State); Apply(observer.State);
            foreach (var preview in strip) if (preview) Apply(preview.State);
        }
        private void Apply(HeldItemPresentationState state)
        {
            if (state == null) return;
            state.Authoring = Phase == GripAuthoringPhase.Live ? null :
                new HeldItemPresentationState.AuthoringPose { Charged = Phase == GripAuthoringPhase.Charged };
        }

        internal bool TryRig(bool firstPerson, out HeldItemPresentationState state, out AvatarBinding binding)
        {
            state = null; binding = null;
            if (!attached) return false;
            state = firstPerson ? held.State : observer.State;
            binding = firstPerson ? avatar.Hands.LocalBinding : observer.Presentation.Binding;
            return state != null && binding != null;
        }
        private bool Showing(bool firstPerson, out HeldItemPresentationState state, out AvatarBinding binding) =>
            TryRig(firstPerson, out state, out binding) && state.CanShowHeldItem && state.committed.Item != 0;
        public bool TryPalm(bool firstPerson, bool right, out Pose palm)
        {
            palm = default;
            if (!TryRig(firstPerson, out _, out var binding)) return false;
            palm = binding.Palm(right);
            return true;
        }
        public bool TryGrip(bool firstPerson, out Pose grip, out AvatarId avatar)
        {
            grip = default; avatar = default;
            if (!Showing(firstPerson, out var state, out var binding)) return false;
            grip = state.GripFrame; avatar = binding.Id;
            return true;
        }
        public bool TryItem(bool firstPerson, out Pose item)
        {
            item = default;
            var definition = SelectedDefinition;
            if (!definition || !Showing(firstPerson, out var state, out var binding)) return false;
            item = HeldItemPoseCalculation.Compose(state.GripFrame, definition.Grip(binding.Id, firstPerson));
            return true;
        }
        public bool TryPouch(bool firstPerson, out Vector3 pouch)
        {
            pouch = default;
            if (SelectedDefinition is not SlingshotDefinition slingshot || !slingshot.HoldClass || !slingshot.HoldClass.View(firstPerson).Hold ||
                !Showing(firstPerson, out _, out var binding)) return false;
            var palm = binding.Palm(false);
            pouch = palm.position + palm.rotation * slingshot.PouchOffset;
            return true;
        }
        public bool TryRequestedPalm(bool firstPerson, bool right, out Pose requested, out Pose reached)
        {
            requested = reached = default;
            if (!TryRig(firstPerson, out var state, out _)) return false;
            var readout = state.Readout;
            requested = right ? readout.RequestedRight : readout.RequestedLeft;
            reached = right ? readout.EvaluatedRight : readout.EvaluatedLeft;
            return true;
        }

        public bool BeginPalmDrag(bool firstPerson, bool right)
        {
            if (!BeginEdit(firstPerson)) return false;
            dragRight = right;
            Seed(right);
            return true;
        }
        public void DragPalm(Pose world)
        {
            if (editState?.Authoring == null) return;
            var local = AvatarHandTargets.Rebase(world, editBinding.Body, Pose.identity);
            if (dragRight) editState.Authoring.Right = local; else editState.Authoring.Left = local;
        }
        public void SetSwivel(bool firstPerson, bool right, float degrees)
        {
            if (editState == null && !BeginEdit(firstPerson)) return;
            Seed(right);
            if (right) editState.Authoring.RightSwivel = degrees; else editState.Authoring.LeftSwivel = degrees;
        }
        public bool EndPalmDrag(out GripPoseCapture capture)
        {
            capture = default;
            if (editState == null) return false;
            var authoring = editState.Authoring;
            var definition = SelectedDefinition;
            bool valid = authoring != null && definition && definition.HoldClass &&
                TryRig(editFirstPerson, out _, out var current) && current == editBinding;
            if (valid)
                capture = new GripPoseCapture
                {
                    Class = definition.HoldClass, FirstPerson = editFirstPerson, Charged = authoring.Charged,
                    Muscles = editBinding.CaptureMuscles(),
                    Spread = Vector3.Distance(editBinding.Palm(true).position, editBinding.Palm(false).position)
                };
            EndEdit();
            return valid;
        }
        private bool BeginEdit(bool firstPerson)
        {
            EndEdit();
            if (Phase == GripAuthoringPhase.Live || !TryRig(firstPerson, out var state, out var binding) || state.Authoring == null) return false;
            editState = state; editBinding = binding; editFirstPerson = firstPerson;
            return true;
        }
        private void Seed(bool right)
        {
            var local = AvatarHandTargets.Rebase(editBinding.Palm(right), editBinding.Body, Pose.identity);
            if (right) editState.Authoring.Right ??= local; else editState.Authoring.Left ??= local;
        }
        private void EndEdit()
        {
            var authoring = editState?.Authoring;
            if (authoring != null) { authoring.Right = authoring.Left = null; authoring.RightSwivel = authoring.LeftSwivel = 0f; }
            editState = null; editBinding = null;
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
            if (!AutoReequip || reequipping != null || selectedItem == 0 || network.ItemAction.State != ItemActionState.Idle || Holds(selectedItem)) return;
            reequipping = StartCoroutine(Reequip());
        }
        private IEnumerator Reequip()
        {
            yield return new WaitForSecondsRealtime(ReequipDelay);
            reequipping = null;
            if (AutoReequip && network.ItemAction.State == ItemActionState.Idle && !Holds(selectedItem)) Equip();
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
            throwing = StartCoroutine(ThrowAfter(Mathf.Max(item.ThrowChargeTime, item.HoldClass ? item.HoldClass.ChargePoseDuration : 0f)));
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
            var item = SelectedDefinition;
            if (!item || item.HoldMode != ItemHoldMode.TwoHand) return "";
            var text = new StringBuilder();
            Shortfall(text, observer);
            foreach (var preview in strip) Shortfall(text, preview);
            return text.ToString();
        }
        private static void Shortfall(StringBuilder text, GripObserverPreview preview)
        {
            if (!preview || preview.State == null) return;
            var readout = preview.State.Readout;
            text.AppendLine($"{preview.DisplayName}: short R {readout.RightPositionError * 100f:F1} cm · L {readout.LeftPositionError * 100f:F1} cm");
        }

        private void OnDestroy()
        {
            EndEdit();
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
            foreach (var entry in originalLayers) if (entry.Key) entry.Key.gameObject.layer = entry.Value;
            walkToggle?.Dispose();
            if (Instance == this) Instance = null;
            SceneChanged?.Invoke();
        }
    }
}
#endif
