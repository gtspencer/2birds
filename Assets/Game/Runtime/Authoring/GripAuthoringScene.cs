#if UNITY_INCLUDE_INSTRUMENTATION
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace TwoBirds
{
    public struct GripPoseCapture { public ItemHoldMode Mode; public bool FirstPerson, Charged; public float[] Muscles; }

    [DefaultExecutionOrder(250)]
    public sealed class GripAuthoringScene : MonoBehaviour
    {
        public static GripAuthoringScene Instance { get; private set; }
        public static event Action SceneChanged;
        [SerializeField] private GripObserverPreview observer;
        [SerializeField] private Camera observerCamera;
        [SerializeField] private UIDocument document;
        [SerializeField] private Material ghostMaterial;
        private static readonly string[] GhostPaths = { "RightPalmContact", "LeftPalmContact", "PullingPalmContact" };
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
        private readonly Dictionary<string, GripGhostHand> ghosts = new();
        private readonly List<GripObserverPreview> strip = new();
        private HeldItemPresentationState poseState;
        public ItemHoldMode PoseMode { get; private set; }
        public int PoseSlot { get; private set; }
        public bool PoseEditing => poseState?.Edit != null;
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
            panel.SavePose = capture => EditorPoseSaveRequested?.Invoke(capture);
            panel.BeforeEdit = () => GripAuthoringSession.BeforeEditorEdit?.Invoke();
#else
            panel.Save = record => panel.ShowMessage(GripAuthoringExport.Save(Drafts, record));
            panel.SavePose = _ => panel.ShowMessage("Pose clips save only in the editor.");
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
        public static event Action<GripPoseCapture> EditorPoseSaveRequested;
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
            EndPoseEdit();
            Cancel(); Drafts.SelectedAvatar = id; avatar.RequestAuthoringAvatar(id); Drafts.SelectionChanged();
            RebuildStrip();
        }
        private void RebuildStrip()
        {
            foreach (var preview in strip) if (preview) Destroy(preview.gameObject);
            strip.Clear();
            int index = 0;
            foreach (var entry in Drafts.Avatars.Entries)
            {
                if (entry.Id == Drafts.SelectedAvatar) continue;
                var root = new GameObject($"Grip strip {entry.Id}");
                root.transform.SetParent(observer.transform.parent, false);
                root.AddComponent<AvatarPresentation>();
                var preview = root.AddComponent<GripObserverPreview>();
                preview.Initialize(avatar, Drafts, entry.Id, ++index * 0.9f);
                strip.Add(preview);
            }
        }
        public void SelectItem(byte id)
        {
            if (!inventory) return;
            EndPoseEdit();
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
            UpdateGhosts();
            panel?.UpdateReadouts();
        }
        private void UpdateGhosts()
        {
            var item = Drafts.Items.Get(Drafts.SelectedItem);
            AvatarRegistry.Entry entry = null;
            foreach (var candidate in Drafts.Avatars.Entries) if (candidate.Id == Drafts.SelectedAvatar) { entry = candidate; break; }
            foreach (string path in GhostPaths)
            {
                ghosts.TryGetValue(path, out var ghost);
                bool applies = Drafts.Context == GripAuthoringContext.Item && observer.ItemRoot && item && entry != null &&
                    (path == "RightPalmContact" || path == "LeftPalmContact" && item.HoldMode == ItemHoldMode.TwoHand ||
                        path == "PullingPalmContact" && item is SlingshotDefinition);
                if (!applies || !TryHandle(path, out var handle)) { ghost?.SetVisible(false); continue; }
                var fingers = path != "PullingPalmContact" && item.GripFingers ? item.GripFingers : Drafts.Avatars.Animations.GripFingers;
                if (ghost == null || ghost.Avatar != entry.Id || ghost.Fingers != fingers)
                {
                    ghost?.Dispose();
                    ghosts[path] = ghost = new GripGhostHand(entry, Drafts.Avatars.Animations, fingers, path == "RightPalmContact",
                        LayerMask.NameToLayer(AvatarEditorPreview.LayerName), ghostMaterial);
                }
                ghost.SetVisible(true);
                ghost.Place(handle.World);
            }
        }
        public string ReachText(bool firstPerson)
        {
            var state = firstPerson ? held?.State : observer.State;
            if (state == null) return "Waiting for presentation";
            var value = state.Readout;
            string status = value.ActiveBlend ? "Active blend" : value.RightUnreachable || value.LeftUnreachable
                ? $"Unreachable contact: {(value.LeftUnreachable ? "Left " : "")}{(value.RightUnreachable ? "Right" : "")}" : "Contact";
            if (value.ClearanceAdjusted) status += " · clearance adjusted";
            string text = $"{status}\nRight {value.RightPositionError:F4} m / {value.RightAngleError:F1}°" +
                (value.HasLeft ? $" · Left {value.LeftPositionError:F4} m / {value.LeftAngleError:F1}°" : "");
            var item = Drafts.Items.Get(Drafts.SelectedItem);
            if (firstPerson || !item || item.HoldMode != ItemHoldMode.TwoHand) return text;
            text += Shortfall(observer);
            foreach (var preview in strip) if (preview && preview.State != null) text += Shortfall(preview);
            return text;
        }
        private static string Shortfall(GripObserverPreview preview)
        {
            var readout = preview.State.Readout;
            return $"\n{preview.DisplayName}: short R {readout.RightPositionError * 100f:F1} cm · L {readout.LeftPositionError * 100f:F1} cm";
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
            else return false;
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
        public void BeginPoseEdit(ItemHoldMode mode, int slot)
        {
            EndPoseEdit();
            if (!attached) return;
            poseState = slot >= 2 ? held.State : observer.State;
            if (poseState == null) return;
            poseState.Edit = new HeldItemPresentationState.PoseEdit { Mode = mode, Charged = slot % 2 == 1 };
            PoseMode = mode; PoseSlot = slot;
        }
        public void EndPoseEdit()
        {
            if (poseState != null) poseState.Edit = null;
            poseState = null;
        }
        public void ResetPoseEdit()
        {
            var edit = poseState?.Edit;
            if (edit == null) return;
            edit.Seeded = false; edit.RightSwivel = edit.LeftSwivel = 0f;
        }
        public void SetSwivel(bool right, float degrees)
        {
            var edit = poseState?.Edit;
            if (edit == null) return;
            if (right) edit.RightSwivel = degrees; else edit.LeftSwivel = degrees;
        }
        public float PoseSwivel(bool right)
        {
            var edit = poseState?.Edit;
            return edit == null ? 0f : right ? edit.RightSwivel : edit.LeftSwivel;
        }
        private AvatarBinding PoseBinding => !PoseEditing ? null : PoseSlot >= 2 ? avatar.Hands.LocalBinding : observer.Presentation.Binding;
        public bool TryPoseHandle(bool right, out Pose world)
        {
            world = default;
            var binding = PoseBinding;
            if (binding == null || !poseState.Edit.Seeded) return false;
            world = AvatarHandTargets.Rebase(right ? poseState.Edit.Right : poseState.Edit.Left, Pose.identity, binding.Body);
            return true;
        }
        public void SetPosePalm(bool right, Pose world)
        {
            var binding = PoseBinding;
            if (binding == null) return;
            var local = AvatarHandTargets.Rebase(world, binding.Body, Pose.identity);
            if (right) poseState.Edit.Right = local; else poseState.Edit.Left = local;
        }
        public bool TryCapturePose(out GripPoseCapture capture)
        {
            capture = default;
            var binding = PoseBinding;
            if (binding == null) return false;
            using var handler = new HumanPoseHandler(binding.Animator.avatar, binding.Animator.transform);
            var pose = new HumanPose();
            handler.GetHumanPose(ref pose);
            capture = new GripPoseCapture { Mode = PoseMode, FirstPerson = PoseSlot >= 2, Charged = PoseSlot % 2 == 1, Muscles = (float[])pose.muscles.Clone() };
            return true;
        }
        private void OnDestroy()
        {
            EndPoseEdit();
            foreach (var ghost in ghosts.Values) ghost.Dispose();
            ghosts.Clear();
            foreach (var preview in strip) if (preview) Destroy(preview.gameObject);
            strip.Clear();
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
