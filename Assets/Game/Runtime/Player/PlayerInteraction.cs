using FishNet.Object;
using UnityEngine;
using UnityEngine.InputSystem;

namespace TwoBirds
{
    [DefaultExecutionOrder(100)]
    public sealed class PlayerInteraction : NetworkBehaviour
    {
        [SerializeField] private float pickupRange = 3f;

        private PlayerInputReader inputReader;
        private PlayerPresentation presentation;
        private int queryMask;
        public IInteractable Target { get; private set; }
        public Collider TargetCollider { get; private set; }
        public InputAction Action { get; private set; }
        public Camera ViewCamera { get; private set; }

        private void Awake()
        {
            inputReader = GetComponent<PlayerInputReader>();
            presentation = GetComponent<PlayerPresentation>();
            queryMask = Physics.DefaultRaycastLayers & ~LayerMask.GetMask("ItemHeld", "PlayerItemHitbox", "Player");
        }

        public override void OnStartClient()
        {
            if (IsOwner) ViewCamera = presentation.ViewCamera;
        }

        private void LateUpdate()
        {
            ClearTarget();
            var session = SessionController.Instance;
            if (!IsOwner || inputReader == null || !inputReader.GameplayActive || session == null ||
                session.Phase != SessionPhase.InGame || session.PanelOpen) return;
            if (ViewCamera == null) return;
            if (!Physics.Raycast(ViewCamera.transform.position, ViewCamera.transform.forward, out var hit, pickupRange,
                queryMask, QueryTriggerInteraction.Ignore)) return;

            var target = hit.collider.GetComponentInParent<IInteractable>();
            if (target == null || !target.CanInteract) return;
            var action = InputSystem.actions.FindAction(target.InputActionPath);
            if (action == null || !action.enabled) return;
            Target = target;
            TargetCollider = hit.collider;
            Action = action;
            if (!action.WasPressedThisFrame()) return;
            target.Interact();
            if (TargetCollider == null || !TargetCollider.gameObject.activeInHierarchy || !target.CanInteract)
                ClearTarget();
        }

        private void ClearTarget() { Target = null; TargetCollider = null; Action = null; }
        public override void OnStopClient() => ClearTarget();
    }
}
