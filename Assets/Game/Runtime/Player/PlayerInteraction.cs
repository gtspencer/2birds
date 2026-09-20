using FishNet.Object;
using UnityEngine;
using UnityEngine.InputSystem;
using System.Collections.Generic;

namespace TwoBirds
{
    [DefaultExecutionOrder(100)]
    public sealed class PlayerInteraction : NetworkBehaviour
    {
        [SerializeField] private float pickupRange = 3f;

        private PlayerInputReader inputReader;
        private PlayerPresentation presentation;
        private int queryMask;
        private int targetMask;
        private readonly RaycastHit[] hits = new RaycastHit[32];
        private readonly Collider[] overlaps = new Collider[16];
        private readonly Dictionary<string, InputAction> actions = new();
        public IInteractable Target { get; private set; }
        public Collider TargetCollider { get; private set; }
        public InputAction Action { get; private set; }
        public Camera ViewCamera { get; private set; }

        private void Awake()
        {
            inputReader = GetComponent<PlayerInputReader>();
            presentation = GetComponent<PlayerPresentation>();
            queryMask = Physics.DefaultRaycastLayers & ~LayerMask.GetMask("ItemHeld", "PlayerItemHitbox", "Player", "BirdBody", "BirdQuery");
            targetMask = LayerMask.GetMask("CartSeat", "GolfCart", "Player");
        }

        public override void OnStartClient()
        {
            if (!IsOwner) return;
            ViewCamera = presentation.ViewCamera;
            foreach (var map in InputSystem.actions.actionMaps)
                foreach (var action in map.actions) actions[map.name + "/" + action.name] = action;
        }

        private void LateUpdate()
        {
            RefreshTarget();
        }

        private void RefreshTarget()
        {
            ClearTarget();
            var session = SessionController.Instance;
            if (!IsOwner || inputReader == null || !inputReader.GameplayActive || inputReader.Carried || session == null ||
                session.Phase != SessionPhase.InGame || session.PanelOpen) return;
            if (ViewCamera == null) return;
            var aim = presentation.AimPose;
            Vector3 direction = aim.rotation * Vector3.forward;
            float obstruction = pickupRange;
            Collider selected = null;
            if (Physics.Raycast(aim.position, direction, out var solid, pickupRange, queryMask, QueryTriggerInteraction.Ignore))
            {
                obstruction = solid.distance;
                selected = solid.collider;
            }
            int count = Physics.RaycastNonAlloc(aim.position, direction, hits, obstruction, targetMask, QueryTriggerInteraction.Collide);
            int inside = Physics.OverlapSphereNonAlloc(aim.position, 0.025f, overlaps, targetMask, QueryTriggerInteraction.Collide);
            if (count == hits.Length || inside == overlaps.Length) return;
            float nearest = obstruction;
            for (int i = 0; i < count; i++)
                if (hits[i].distance < nearest && SelectableTarget(hits[i].collider))
                { nearest = hits[i].distance; selected = hits[i].collider; }
            for (int i = 0; i < inside; i++)
                if ((overlaps[i].ClosestPoint(aim.position) - aim.position).sqrMagnitude < 0.000001f &&
                    SelectableTarget(overlaps[i]))
                { selected = overlaps[i]; break; }
            if (selected == null) return;
            var target = selected.GetComponentInParent<IInteractable>();
            if (target == null || !target.CanInteract) return;
            if (!actions.TryGetValue(target.InputActionPath, out var action) || !action.enabled) return;
            Target = target;
            TargetCollider = selected;
            Action = action;
            if (!inputReader.InteractPressed) return;
            target.Interact();
            if (TargetCollider == null || !TargetCollider.gameObject.activeInHierarchy || !target.CanInteract)
                ClearTarget();
        }

        private static bool SelectableTarget(Collider collider) =>
            collider.TryGetComponent<PlayerCarry>(out var player) && player.PhysicalTarget(collider) || collider.isTrigger &&
            (collider.TryGetComponent<CartSeat>(out var seat) && seat.CanInteract ||
             collider.TryGetComponent<SteeringWheelHorn>(out var horn) && horn.CanInteract);

        private void ClearTarget() { Target = null; TargetCollider = null; Action = null; }
        public override void OnStopClient() => ClearTarget();
    }
}
