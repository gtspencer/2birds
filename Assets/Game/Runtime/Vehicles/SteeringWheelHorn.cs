using UnityEngine;

namespace TwoBirds
{
    public sealed class SteeringWheelHorn : MonoBehaviour, IInteractable
    {
        [SerializeField] private Transform tooltipAnchor;
        private GolfCartNetwork cart;
        public string ActionText => "Honk";
        public string InputActionPath => "Player/Interact";
        public Transform TooltipAnchor => tooltipAnchor;
        public bool CanInteract => cart != null;
        private void Awake() => cart = GetComponentInParent<GolfCartNetwork>();
        public void Interact() { if (cart != null) cart.Honk(); }
    }
}
