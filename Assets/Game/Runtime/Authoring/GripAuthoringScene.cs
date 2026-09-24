#if UNITY_INCLUDE_INSTRUMENTATION
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace TwoBirds
{
    [DefaultExecutionOrder(250)]
    public sealed class GripAuthoringScene : MonoBehaviour
    {
        public static GripAuthoringScene Instance { get; private set; }
        public static event Action SceneChanged;
        [SerializeField] private GripObserverPreview observer;
        [SerializeField] private Camera observerCamera;
        [SerializeField] private UIDocument document;
        private SessionController session;
        private PlayerInventory inventory;
        private PlayerEquipment equipment;
        private PlayerInputReader input;
        private PlayerAvatarPresentation avatar;
        private PlayerHeldItemPresentation held;
        private PlayerNetworkState network;
        private GripAuthoringPanel panel;
        private readonly HashSet<uint> supplied = new();
        private readonly Dictionary<Transform, int> originalLayers = new();
        private byte pendingItem;
        private bool attached, attaching, choosing, cleaning;
        private UnityEngine.InputSystem.InputAction editToggle;
        public GripComparisonViews Views { get; private set; }
        public GripAuthoringDrafts Drafts => GripAuthoringSession.Drafts;
        public GripObserverPreview Observer => observer;
        public string ActionPhase => network ? network.ItemAction.State.ToString() : "Waiting for Solo";

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
            observer.Initialize(avatar, Drafts);
            Views = new GripComparisonViews(player.GetComponent<PlayerPresentation>().ViewCamera, observerCamera);
            avatar.Hands.LocalBound += OwnerBound;
            if (avatar.Hands.LocalBinding != null) OwnerBound(avatar.Hands.LocalBinding);
            AssignOwnerLayer(player.GetComponent<PlayerPresentation>().Graphics);
            inventory.InventoryChanged += InventoryChanged; network.ActionChanged += ActionChanged;
            panel = new GripAuthoringPanel(document.rootVisualElement, Drafts, this);
#if UNITY_EDITOR
            panel.Save = record => EditorSaveRequested?.Invoke(record);
            panel.BeforeEdit = () => GripAuthoringSession.BeforeEditorEdit?.Invoke();
#else
            panel.Save = record => panel.ShowMessage(GripAuthoringExport.Save(Drafts, record));
            panel.ShowMessage(GripAuthoringExport.Folder);
#endif
            panel.OpenFolder = GripAuthoringExport.OpenFolder;
            editToggle = new UnityEngine.InputSystem.InputAction("Grip editing", binding: "<Keyboard>/f2");
            editToggle.performed += _ => SetEditing(!input.AuthoringFocus);
            editToggle.Enable();
            SetEditing(true); SelectAvatar(Drafts.SelectedAvatar); SelectItem(Drafts.SelectedItem);
            SceneChanged?.Invoke();
        }
#if UNITY_EDITOR
        public static event Action<GripAuthoringDraft> EditorSaveRequested;
#endif
        private void OwnerBound(AvatarBinding binding) => AssignOwnerLayer(binding.Animator.transform);
        private void AssignOwnerLayer(Transform root)
        {
            int layer = LayerMask.NameToLayer("GripAuthoringOwner");
            foreach (var child in root.GetComponentsInChildren<Transform>(true))
            { if (!originalLayers.ContainsKey(child)) originalLayers.Add(child, child.gameObject.layer); child.gameObject.layer = layer; }
        }
        public void SetEditing(bool editing)
        {
            if (!input) return;
            input.AuthoringFocus = editing;
        }
        public void SelectAvatar(AvatarId id)
        {
            if (!avatar) return;
            Cancel(); Drafts.SelectedAvatar = id; avatar.RequestAuthoringAvatar(id); Drafts.SelectionChanged();
        }
        public void SelectItem(byte id)
        {
            if (!inventory) return;
            Cancel(); Drafts.SelectedItem = id; pendingItem = id;
            Drafts.SelectionChanged(); Equip();
        }
        public void Equip()
        {
            if (!inventory || choosing) return;
            pendingItem = Drafts.SelectedItem;
            if (network.ItemAction.State == ItemActionState.Recovering) return;
            choosing = true;
            try
            {
                Cleanup();
                foreach (uint id in supplied)
                    if (WorldItemRegistry.Instance.TryGetRecord(id, out var retained) && retained.State == WorldItemState.Held && retained.DefinitionId != pendingItem)
                    { panel?.ShowMessage("The previous supplied item is still held. Equip again when it can be dropped."); return; }
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
        private void InventoryChanged()
        {
            if (choosing || pendingItem == 0) return;
            for (int i = 0; i < inventory.Count; i++)
                if (!inventory.GetSlot(i).IsEmpty && inventory.GetSlot(i).ItemId == pendingItem) { Equip(); break; }
        }
        private void ActionChanged()
        {
            if (network.ItemAction.State != ItemActionState.Idle) return;
            Cleanup(); if (pendingItem != 0) Equip();
        }
        private void Cleanup()
        {
            if (cleaning || network.ItemAction.State == ItemActionState.Recovering) return;
            cleaning = true;
            var remove = new List<uint>();
            foreach (uint id in supplied)
            {
                if (!WorldItemRegistry.Instance.TryGetRecord(id, out var record)) { remove.Add(id); continue; }
                if (record.DefinitionId == Drafts.SelectedItem && record.State == WorldItemState.Held) continue;
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
        public void Dequip() { Cancel(); if (inventory) inventory.SelectSlot(-1); }
        public void BeginUse() { if (input) input.BeginAuthoringUse(); }
        public void EndUse() { if (input) input.EndAuthoringUse(); }
        public void Cancel() { if (input) { input.EndAuthoringUse(true); equipment.CancelUse(); } }
        public void DirectUse() { Cancel(); if (equipment) equipment.DirectUse(); }
        public void Drop() { Cancel(); if (inventory) inventory.DropSelected(); }
        public void Exit() { Cancel(); session.Leave(); }
        private void LateUpdate()
        {
            if (!attached) return;
            Views.Follow(avatar.CurrentPlacement.SolePosition + Vector3.up * (avatar.Presentation.Resolved.Settings.VisualHeight * 0.6f));
            panel?.UpdateReadouts();
        }
        public string ReachText(bool firstPerson)
        {
            var state = firstPerson ? held?.State : observer.State;
            if (state == null) return "Waiting for presentation";
            var value = state.Readout;
            string status = value.ActiveBlend ? "Active blend" : value.RightUnreachable || value.LeftUnreachable
                ? $"Unreachable contact: {(value.LeftUnreachable ? "Left " : "")}{(value.RightUnreachable ? "Right" : "")}" : value.ReachLimited ? "Reach limited" : "Contact";
            if (value.ClearanceAdjusted) status += " · clearance adjusted";
            return $"{status}\nRight {value.RightPositionError:F4} m / {value.RightAngleError:F1}°" +
                (value.HasLeft ? $" · Left {value.LeftPositionError:F4} m / {value.LeftAngleError:F1}°" : "");
        }
        public bool TryHandle(string path, out GripAuthoringHandle handle)
        {
            handle = default;
            if (!attached || Drafts?.Current == null) return false;
            bool firstPerson = path.StartsWith("FirstPerson");
            var state = firstPerson ? held.State : observer.State;
            var binding = firstPerson ? avatar.Hands.LocalBinding : observer.Presentation.Binding;
            if (state == null || binding == null) return false;
            var record = Drafts.Current;
            Pose basis;
            Vector3 scale = Vector3.one;
            string position, euler;
            if (path.EndsWith("Contact"))
            {
                var root = observer.ItemRoot;
                if (!root) return false;
                basis = new Pose(path == "PullingPalmContact" && observer.State.Slingshot ? observer.State.Slingshot.Center : root.position, root.rotation);
                scale = ((ItemDefinition)record.Runtime).WorldPrefab.transform.localScale;
                position = path + ".Position"; euler = path + ".Euler";
            }
            else if (path.EndsWith("Correction"))
            {
                bool right = path.StartsWith("Right");
                var generated = binding.Settings.Generated;
                var wrist = binding.GetBone(right ? HumanBodyBones.RightHand : HumanBodyBones.LeftHand);
                basis = new Pose(wrist.position + wrist.rotation * ((right ? generated.RightWristToPalmPosition : generated.LeftWristToPalmPosition) * binding.Scale),
                    wrist.rotation * (right ? generated.RightWristToPalmRotation : generated.LeftWristToPalmRotation));
                scale = Vector3.one * binding.Scale;
                position = path + ".Position"; euler = path + ".Euler";
            }
            else
            {
                int dot = path.IndexOf('.');
                string group = dot < 0 ? path : path[..dot];
                string field = dot < 0 ? group.Contains("ChargePose") ? "ChargedPositionOffset" : "HoldPosition" : path[(dot + 1)..];
                var item = record.Runtime as ItemDefinition;
                bool shared = record.Runtime is HeldItemSettings;
                if (shared) item = Drafts.Items.Get(Drafts.SelectedItem);
                if (!item) return false;
                if (shared && (group == "HoldSettings" && (item.HoldMode != ItemHoldMode.Hand || item.OverrideHoldSettings) ||
                    group == "HeavyHoldSettings" && (item.HoldMode != ItemHoldMode.Heavy || item.OverrideHoldSettings) ||
                    group == "FirstPersonPose" && (item.HoldMode == ItemHoldMode.Heavy || item.OverrideFirstPersonPose) ||
                    group == "SlingshotChargePose" && (item is not SlingshotDefinition defaultsSling || defaultsSling.OverrideRemoteChargePose))) return false;
                if (!shared && (group == "HandPose" && !item.OverrideHoldSettings || group == "FirstPersonPose" && !item.OverrideFirstPersonPose)) return false;
                if (item is SlingshotDefinition sling && (group == "RemoteChargePose" && !sling.OverrideRemoteChargePose ||
                    group == "FirstPersonChargePose" && !sling.OverrideFirstPersonChargePose)) return false;
                var body = state.LastBody;
                if (field == "PullingHandDrawOffset")
                {
                    if (!state.Slingshot) return false;
                    basis = new Pose(state.Slingshot.DrawCenter.position, state.Slingshot.transform.rotation);
                    scale = item.WorldPrefab.transform.localScale; euler = null;
                }
                else if (group.Contains("ChargePose"))
                {
                    if (!state.Slingshot) return false;
                    var data = new HeldItemPoseData(item, Drafts.Held, firstPerson);
                    var hold = HeldItemPoseCalculation.Hold(body, binding.Settings, data);
                    var input = held.CaptureInput(firstPerson);
                    var aim = Quaternion.Euler(input.Placement.LookPitch, input.Placement.LookYaw, 0f);
                    var charged = HeldItemPoseCalculation.SlingshotCharge(body, data, state.Slingshot, hold, hold, 1f, firstPerson, input.Camera, aim, out _);
                    Quaternion rotation = firstPerson ? input.Camera.rotation : aim;
                    basis = new Pose(charged.FollowPosition - rotation * data.SlingshotCharge.ChargedPositionOffset, rotation);
                    euler = group + ".ChargedPalmEuler";
                }
                else
                {
                    bool heavy = item.HoldMode == ItemHoldMode.Heavy;
                    Vector3 origin = heavy ? body.CenterToWorld(Vector3.zero) : body.ToWorld(Vector3.zero);
                    float length = Vector3.Distance(origin, heavy ? body.CenterToWorld(Vector3.right) : body.ToWorld(Vector3.right));
                    basis = new Pose(origin, body.Rotation); scale = Vector3.one * length;
                    euler = field == "ChargeControlPosition" ? null : group + (field == "ChargedPosition" ? ".ChargedWristEuler" : ".HoldWristEuler");
                }
                position = group + "." + field;
            }
            var local = new Pose((Vector3)GripAuthoringFields.Get(record.Values, position),
                euler == null ? Quaternion.identity : Quaternion.Euler((Vector3)GripAuthoringFields.Get(record.Values, euler)));
            handle = new GripAuthoringHandle { Basis = basis, Scale = scale, PositionField = position, EulerField = euler, Local = local };
            return true;
        }
        public bool TryPalmReadout(bool firstPerson, out Pose requestedRight, out Pose evaluatedRight, out Pose requestedLeft, out Pose evaluatedLeft, out bool hasLeft)
        {
            var state = firstPerson ? held?.State : observer.State;
            var readout = state?.Readout ?? default;
            requestedRight = readout.RequestedRight; evaluatedRight = readout.EvaluatedRight;
            requestedLeft = readout.RequestedLeft; evaluatedLeft = readout.EvaluatedLeft; hasLeft = readout.HasLeft;
            return state != null;
        }
        private void OnDestroy()
        {
            if (session) session.Changed -= SessionChanged;
            if (attached)
            {
                Cancel(); SetEditing(false);
                if (inventory) inventory.InventoryChanged -= InventoryChanged;
                if (network) network.ActionChanged -= ActionChanged;
                if (avatar && avatar.Hands) avatar.Hands.LocalBound -= OwnerBound;
            }
            foreach (var entry in originalLayers) if (entry.Key) entry.Key.gameObject.layer = entry.Value;
            panel?.Dispose(); Views?.Dispose();
            editToggle?.Dispose();
            if (Instance == this) Instance = null;
            SceneChanged?.Invoke();
        }
    }
}
#endif
