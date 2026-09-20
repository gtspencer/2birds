using UnityEngine;

namespace TwoBirds
{
    public sealed class CartSeat : MonoBehaviour, IInteractable
    {
        [SerializeField, Range(0, 3)] private int index;
        [SerializeField] private Transform rider;
        [SerializeField] private Transform eye;
        [SerializeField] private Transform exit;
        [SerializeField] private Transform tooltipAnchor;
        [SerializeField, Tooltip("Replaces the default hover tooltip text when set.")] private string tooltipTextOverride;
        [SerializeField, Tooltip("Show only the interaction glyph in the hover tooltip.")] private bool hideTooltipText;
        public int Index => index;
        public Transform Rider => rider;
        public Transform Eye => eye;
        public Transform Exit => exit;
        internal Pose RiderLocal { get; private set; }
        internal Pose ExitLocal { get; private set; }
        private Pose eyeLocal, tooltipLocal;
        internal Pose PhysicalRider => new(Cart.transform.TransformPoint(RiderLocal.position), Cart.transform.rotation * RiderLocal.rotation);
        internal Pose VisualRider => Cart.VisualPose(RiderLocal);
        internal Pose VisualEye => Cart.VisualPose(eyeLocal);
        internal Vector3 VisualTooltipPosition => Cart.VisualPose(tooltipLocal).position;
        public GolfCartNetwork Cart { get; private set; }
        public string InputActionPath => "Player/Interact";
        public string ActionText => Cart.Recovery == CartRecovery.Flipped ? "Flip cart" :
            Cart.Recovery == CartRecovery.Stuck ? "Unstick cart" : "Enter seat";
        public string TooltipTextOverride => tooltipTextOverride;
        public bool HideTooltipText => hideTooltipText;
        public Transform TooltipAnchor => tooltipAnchor;
        public bool CanInteract => Cart != null && !Cart.Busy &&
            (Cart.Recovery != CartRecovery.None || !Cart.IsOccupied(index));
        private void Awake()
        {
            Cart = GetComponentInParent<GolfCartNetwork>();
            RiderLocal = LocalPose(rider);
            ExitLocal = LocalPose(exit);
            eyeLocal = LocalPose(eye);
            tooltipLocal = LocalPose(tooltipAnchor ? tooltipAnchor : transform);
        }
        private Pose LocalPose(Transform anchor) => new(Cart.transform.InverseTransformPoint(anchor.position),
            Quaternion.Inverse(Cart.transform.rotation) * anchor.rotation);
        public void Interact()
        {
            var player = PlayerSeating.Local;
            if (CanInteract && player != null) player.Request(Cart, index);
        }
    }
}
