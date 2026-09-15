using UnityEngine;

namespace TwoBirds
{
    [RequireComponent(typeof(Rigidbody), typeof(CapsuleCollider))]
    public sealed class PlayerItemHitbox : MonoBehaviour
    {
        private Rigidbody body;
        public Collider Collider { get; private set; }
        public PlayerMotor Motor { get; private set; }

        private void Awake()
        {
            body = GetComponent<Rigidbody>();
            Collider = GetComponent<Collider>();
            Motor = GetComponentInParent<PlayerMotor>();
        }

        internal void FollowMotor()
        {
            body.MovePosition(Motor.Body.position);
            body.MoveRotation(Motor.Body.rotation);
        }
    }
}
