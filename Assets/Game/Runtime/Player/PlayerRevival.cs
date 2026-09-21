using System;
using UnityEngine;

namespace TwoBirds
{
    public sealed class PlayerRevival : MonoBehaviour, IInteractable
    {
        [SerializeField, Min(0.1f)] private float reviveDuration = 5f;
        private PlayerNetworkState network, target;
        private PlayerRevival targetBody;
        private PlayerHealth health;
        private PlayerRagdoll ragdoll;
        private PlayerInputReader input;
        private PlayerInteraction interaction;
        private PlayerPresentation presentation;
        private PlayerInventory inventory;
        private InputPresentation inputPresentation;
        private uint attempt, token;
        private bool canceled, completionSent, giveUpSent;
        private float giveUpTime;
        private int obstructionMask;
        private readonly RaycastHit[] hits = new RaycastHit[32];
        public float Duration => reviveDuration;
        public bool Busy => target && !canceled;
        public float GiveUpProgress => Mathf.Clamp01(giveUpTime / 2f);
        public PlayerNetworkState ProgressTarget => Busy ? target : health.IsDowned ? network : null;
        public Vector3 RootPosition => ragdoll.RootPosition;
        public string ActionText => network.Claim.Active ? "Being revived" : "Revive";
        public string InputActionPath => "Player/Interact";
        public string TooltipTextOverride => "";
        public bool HideTooltipText => false;
        public bool Displayable => health.IsDowned;
        public bool CanInteract => health.IsDowned && !network.Claim.Active;
        public event Action ProgressChanged;

        private void Awake()
        {
            network = GetComponent<PlayerNetworkState>();
            health = GetComponent<PlayerHealth>();
            ragdoll = GetComponent<PlayerRagdoll>();
            input = GetComponent<PlayerInputReader>();
            interaction = GetComponent<PlayerInteraction>();
            presentation = GetComponent<PlayerPresentation>();
            inventory = GetComponent<PlayerInventory>();
            obstructionMask = LayerMask.GetMask("Ground", "Environment", "GolfCart");
            health.DamagingHit += Cancel;
            health.LifeChanged += LifeChanged;
            network.ClaimChanged += NotifyProgress;
        }
        private void Start()
        {
            inputPresentation = SessionController.Instance.InputPresentation;
            inputPresentation.Interrupted += Cancel;
            enabled = network.IsOwner && (health.IsDowned || Busy);
        }
        public void Interact()
        {
            if (PlayerSeating.Local && CanInteract)
                PlayerSeating.Local.GetComponent<PlayerRevival>().Begin(this);
        }
        private void Begin(PlayerRevival body)
        {
            if (target || !network.IsOwner || !health.IsAlive || !input.GameplayActive || !body.CanInteract || !Geometry(body)) return;
            target = body.network;
            targetBody = body;
            enabled = true;
            attempt++;
            token = 0;
            canceled = completionSent = false;
            target.ClaimChanged += ClaimChanged;
            target.Health.LifeChanged += TargetLifeChanged;
            inventory.ApplyControlPermissions();
            network.RequestRevive(target, attempt);
            ProgressChanged?.Invoke();
        }
        internal bool Geometry(PlayerRevival body)
        {
            Vector3 origin = presentation.AimPose.position;
            Vector3 delta = body.RootPosition - origin;
            if (delta.sqrMagnitude > interaction.PickupRange * interaction.PickupRange) return false;
            int count = Physics.RaycastNonAlloc(origin, delta.normalized, hits, delta.magnitude,
                obstructionMask, QueryTriggerInteraction.Ignore);
            if (count == hits.Length) return false;
            for (int i = 0; i < count; i++)
                if (!hits[i].collider.transform.IsChildOf(transform)) return false;
            return true;
        }
        private void Update()
        {
            if (!network.IsOwner) return;
            if (health.IsDowned)
            {
                if (!input.GiveUpHeld)
                {
                    if (giveUpTime > 0f) { giveUpTime = 0f; ProgressChanged?.Invoke(); }
                    return;
                }
                if (!giveUpSent)
                {
                    giveUpTime += Time.unscaledDeltaTime;
                    if (giveUpTime >= 2f && network.DownConfirmed) { giveUpSent = true; network.GiveUp(); }
                }
                return;
            }
            if (!Busy) return;
            if (!target.IsSpawned || !input.InteractHeld || !Geometry(targetBody)) { Cancel(); return; }
            if (token == 0 || completionSent || target.ReviveRemaining > 0f) return;
            completionSent = true;
            network.EndRevive(target, attempt, token, true);
        }
        private void ClaimChanged()
        {
            if (!target) { Finish(); return; }
            var claim = target.Claim;
            if (!claim.Active)
            {
                if (token != 0 || claim.Attempt == attempt && claim.Rescuer == network.ObjectId) Finish();
                return;
            }
            if (claim.Rescuer != network.ObjectId || claim.RescuerLifetime != network.Lifetime || claim.Attempt != attempt)
            { Finish(); return; }
            token = claim.Sequence;
            if (canceled || !input.InteractHeld) Cancel();
            ProgressChanged?.Invoke();
        }
        public void Cancel()
        {
            giveUpTime = 0f;
            enabled = network.IsOwner && health.IsDowned;
            if (!target) { ProgressChanged?.Invoke(); return; }
            canceled = true;
            if (network.IsOwner && network.IsClientInitialized && target.IsSpawned)
                network.EndRevive(target, attempt, token, false);
            inventory.ApplyControlPermissions();
            ProgressChanged?.Invoke();
        }
        internal void Rejected(uint sequence) { if (sequence == attempt) Finish(); }
        private void TargetLifeChanged() { if (!target || !target.Health.IsDowned) Finish(); }
        private void LifeChanged()
        {
            Cancel();
            giveUpSent = false;
        }
        private void NotifyProgress() => ProgressChanged?.Invoke();
        private void Finish()
        {
            if (target)
            {
                target.ClaimChanged -= ClaimChanged;
                target.Health.LifeChanged -= TargetLifeChanged;
            }
            target = null;
            targetBody = null;
            enabled = network.IsOwner && health.IsDowned;
            token = 0;
            completionSent = canceled = false;
            inventory.ApplyControlPermissions();
            ProgressChanged?.Invoke();
        }
        private void OnDestroy()
        {
            if (inputPresentation != null) inputPresentation.Interrupted -= Cancel;
            if (health) { health.DamagingHit -= Cancel; health.LifeChanged -= LifeChanged; }
            if (network) network.ClaimChanged -= NotifyProgress;
            if (target) { target.ClaimChanged -= ClaimChanged; target.Health.LifeChanged -= TargetLifeChanged; }
        }
    }
}
