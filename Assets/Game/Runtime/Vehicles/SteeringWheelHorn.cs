using UnityEngine;

namespace TwoBirds
{
    public sealed class SteeringWheelHorn : MonoBehaviour, IInteractable
    {
        [SerializeField] private Transform tooltipAnchor;
        [SerializeField, Tooltip("Replaces the default hover tooltip text when set.")] private string tooltipTextOverride;
        [SerializeField, Tooltip("Show only the interaction glyph in the hover tooltip.")] private bool hideTooltipText;
        private GolfCartNetwork cart;
        public string ActionText => "Honk";
        public string TooltipTextOverride => tooltipTextOverride;
        public bool HideTooltipText => hideTooltipText;
        public string InputActionPath => "Player/Interact";
        public Transform TooltipAnchor => tooltipAnchor;
        public bool CanInteract => cart != null;
        private void Awake() => cart = GetComponentInParent<GolfCartNetwork>();
        public void Interact() { if (cart != null) cart.Honk(); }
    }
}
