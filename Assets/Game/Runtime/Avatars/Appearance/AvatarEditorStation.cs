using UnityEngine;

namespace TwoBirds
{
    public sealed class AvatarEditorStation : MonoBehaviour, IInteractable
    {
        [SerializeField] private Collider surface;
        [SerializeField] private Transform tooltipAnchor;
        private SessionController session;
        private PlayerMotor player;
        private PlayerInputReader input;
        public Collider Surface => surface;
        public string ActionText => "Edit avatar";
        public string TooltipTextOverride => null;
        public bool HideTooltipText => false;
        public string InputActionPath => "Player/Interact";
        public Transform TooltipAnchor => tooltipAnchor;
        public bool CanInteract
        {
            get
            {
                return session && session.Phase == SessionPhase.InGame && !session.EditorOpen &&
                    input && input.GameplayActive;
            }
        }
        private void Awake() { if (!surface) surface = GetComponentInChildren<Collider>(); }
        private void Start()
        {
            session = SessionController.Instance; session.Changed += ContextChanged; ContextChanged();
        }
        private void ContextChanged()
        {
            if (player == session.LocalPlayer) return;
            player = session.LocalPlayer; input = player ? player.GetComponent<PlayerInputReader>() : null;
        }
        private void OnDestroy() { if (session) session.Changed -= ContextChanged; }
        public void Interact() { if (CanInteract) session.AvatarEditor.Open(origin: this); }
    }
}
