using UnityEngine;

namespace TwoBirds
{
    public sealed class CartSeat : MonoBehaviour, IInteractable
    {
        [SerializeField, Range(0, 3)] private int index;
        [SerializeField] private Transform rider;
        [SerializeField] private Transform eye;
        [SerializeField] private Transform exit;
        public int Index => index;
        public Transform Rider => rider;
        public Transform Eye => eye;
        public Transform Exit => exit;
        public GolfCartNetwork Cart { get; private set; }
        public string InputActionPath => "Player/Interact";
        public string ActionText => Cart.Recovery == CartRecovery.Flipped ? "Flip cart" :
            Cart.Recovery == CartRecovery.Stuck ? "Unstick cart" : "Enter seat";
        public bool CanInteract => Cart != null && !Cart.Busy &&
            (Cart.Recovery != CartRecovery.None || !Cart.IsOccupied(index));
        private void Awake() => Cart = GetComponentInParent<GolfCartNetwork>();
        public void Interact()
        {
            var player = PlayerSeating.Local;
            if (CanInteract && player != null) player.Request(Cart, index);
        }
    }
}
