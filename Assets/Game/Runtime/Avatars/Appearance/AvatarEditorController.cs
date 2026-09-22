using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

namespace TwoBirds
{
    public sealed class AvatarEditorController : MonoBehaviour
    {
        [SerializeField] private AvatarEditorPreview preview;
        [SerializeField] private AvatarEditorPanel panel;
        private SessionController session;
        private AvatarAppearance original, draft, staged;
        private AvatarEditorStation station;
        private PlayerHealth health;
        private PlayerPresentation player;
        private float stationRange;
        private Action restore;
        private uint originSession;
        private SessionPhase originPhase;
        private InputActionMap actions;
        private InputAction scroll, navigate, cancel, pause;
        private bool scrollEnabled, navigateEnabled;
        private int handledFrame = -1;
        public bool IsOpen { get; private set; }
        public bool Placement { get; private set; }
        public bool Confirming { get; private set; }
        public int Selection { get; private set; } = -1;
        public bool Switching => staged != null;
        public AvatarAppearance Draft => draft;
        public event Action Changed;
        internal AvatarEditorPreview Preview => preview;
        internal SessionController Session => session;

        internal void Initialize(SessionController value)
        {
            session = value;
            actions = InputSystem.actions.FindActionMap("AvatarEditor", true); actions.Disable();
            scroll = InputSystem.actions.FindAction("UI/Scroll", true);
            navigate = InputSystem.actions.FindAction("UI/Navigate", true);
            cancel = InputSystem.actions.FindAction("UI/Cancel", true);
            pause = InputSystem.actions.FindAction("UI/Pause", true);
            preview.Initialize(session); preview.Accepted += Accepted;
            panel.Initialize(this); panel.SetVisible(false);
        }
        public void Open(Action returnFocus = null, AvatarEditorStation origin = null)
        {
            if (IsOpen || session.Phase is not (SessionPhase.Idle or SessionPhase.InLobby or SessionPhase.InGame)) return;
            original = session.Appearance.Committed; draft = original.Clone(); staged = null;
            Selection = -1; Placement = Confirming = false; restore = returnFocus;
            preview.SetPlacement(false);
            originSession = session.SessionId; originPhase = session.Phase;
            station = origin; IsOpen = true;
            if (station)
            {
                health = session.LocalPlayer.GetComponent<PlayerHealth>();
                player = session.LocalPlayer.GetComponent<PlayerPresentation>();
                stationRange = session.LocalPlayer.GetComponent<PlayerInteraction>().PickupRange;
                health.DamagingHit += ForceCommit; health.LifeChanged += LifeChanged;
            }
            scrollEnabled = scroll.enabled; navigateEnabled = navigate.enabled;
            scroll.Disable(); actions.Enable();
            cancel.performed += BackInput; pause.performed += BackInput;
            session.InputPresentation.Interrupted += Interrupted;
            session.Unlocks.Changed += Notify;
            session.SetEditorOpen(true);
            panel.SetVisible(true); preview.Show(draft); Notify();
        }
        private void Accepted(AvatarId id)
        {
            if (!IsOpen) return;
            if (staged != null && staged.Avatar == id)
            {
                draft = staged; staged = null; Selection = -1; SetPlacement(false);
            }
            preview.SetAppearance(draft); Notify();
        }
        public void SelectAvatar(AvatarId id)
        {
            if (!IsOpen || Switching || !session.Unlocks.Available(id) || id == draft.Avatar || !session.Avatars.TryResolve(id, out var entry)) return;
            staged = AvatarTattooPlacement.Transfer(draft, entry);
            preview.Show(staged); Notify();
        }
        public void SelectHat(HatId id)
        { if (Switching || !session.Unlocks.Available(id)) return; draft.Hat = id; Edit(); }
        public void AddTattoo(TattooId id)
        {
            if (Switching || draft.Tattoos.Length >= AvatarAppearance.MaximumTattoos || !session.Tattoos.TryResolve(id, out _)) return;
            var value = new TattooAppearance { Design = id, Size = 0.3f, R = 24, G = 24, B = 24 };
            if (!preview.CentralTattoo(value, out value)) return;
            var list = new List<TattooAppearance>(draft.Tattoos) { value };
            draft.Tattoos = list.ToArray(); Selection = list.Count - 1; SetPlacement(true); Edit();
        }
        public void SelectTattoo(int index)
        { Selection = index; preview.FocusTattoo(index); SetPlacement(false); Notify(); }
        public void RemoveTattoo(int index)
        {
            var list = new List<TattooAppearance>(draft.Tattoos); list.RemoveAt(index);
            draft.Tattoos = list.ToArray();
            Selection = list.Count == 0 ? -1 : Mathf.Min(index, list.Count - 1);
            if (Selection < 0) SetPlacement(false);
            Edit();
        }
        public void RemoveAll()
        { if (Switching) return; draft.Hat = default; draft.Tattoos = Array.Empty<TattooAppearance>(); Selection = -1; SetPlacement(false); Edit(); }
        public void UpdateTattoo(TattooAppearance value)
        { if (Switching || Selection < 0) return; draft.Tattoos[Selection] = value; preview.SetAppearance(draft); panel.UpdateGizmos(); }
        public void SetInk(Color32 color)
        {
            if (Selection < 0) return;
            var value = draft.Tattoos[Selection]; value.R = color.r; value.G = color.g; value.B = color.b;
            UpdateTattoo(value);
        }
        public void SetPlacement(bool value)
        {
            Placement = value && Selection >= 0 && !Switching;
            if (Placement && !Confirming) navigate.Disable(); else if (navigateEnabled) navigate.Enable();
            preview.SetPlacement(Placement); Notify();
        }
        public void Back()
        {
            if (!IsOpen || session.InputPresentation.SuppressInput || handledFrame == Time.frameCount) return;
            handledFrame = Time.frameCount;
            if (Confirming) { Confirming = false; SetPlacement(Placement); return; }
            if (Placement) { SetPlacement(false); return; }
            if (draft.Equals(original)) { Close(false, false); return; }
            Confirming = true; if (navigateEnabled) navigate.Enable(); Notify();
        }
        private void BackInput(InputAction.CallbackContext context) => Back();
        public void KeepEditing() { Confirming = false; SetPlacement(Placement); }
        public void Apply() { if (!Switching) Close(true, false); }
        public void Cancel() => Close(false, false);
        public void ForceCommit() => Close(true, true);
        private void LifeChanged() { if (health && health.IsDowned) ForceCommit(); }
        private void Close(bool commit, bool forced)
        {
            if (!IsOpen) return;
            IsOpen = false;
            if (commit) session.Appearance.Commit(draft);
            staged = null;
            Cleanup();
            var callback = restore; restore = null;
            if (!forced && originSession == session.SessionId && originPhase == session.Phase) callback?.Invoke();
        }
        private void Cleanup()
        {
            if (health) { health.DamagingHit -= ForceCommit; health.LifeChanged -= LifeChanged; }
            health = null; player = null; station = null;
            cancel.performed -= BackInput; pause.performed -= BackInput;
            session.InputPresentation.Interrupted -= Interrupted; session.Unlocks.Changed -= Notify;
            panel.ReleaseGesture(); actions.Disable();
            if (scrollEnabled) scroll.Enable(); if (navigateEnabled) navigate.Enable();
            preview.Hide(); panel.SetVisible(false); session.SetEditorOpen(false);
        }
        private void Interrupted() => panel.ReleaseGesture();
        private void Update()
        {
            if (!IsOpen) return;
            if (station && player && Vector3.Distance(player.AimPose.position, station.Surface.ClosestPoint(player.AimPose.position)) > stationRange)
            { ForceCommit(); return; }
            if (!Confirming && !session.InputPresentation.SuppressInput) panel.TickInput();
        }
        private void Edit() { preview.SetAppearance(draft); Notify(); }
        private void Notify() => Changed?.Invoke();
        private void OnDisable() { if (IsOpen) { IsOpen = false; Cleanup(); } }
    }
}
