using FishNet.Component.Transforming.Beta;
using FishNet.Connection;
using FishNet.Object;
using UnityEngine;

namespace TwoBirds
{
    public readonly struct PlayerSnapshot
    {
        public readonly Vector3 LocalVelocity;
        public readonly float Speed;
        public readonly Quaternion Facing;
        public readonly bool Grounded;
        public readonly MovementMode Mode;
        public readonly PublicPlayerState PublicState;
        public PlayerSnapshot(PlayerMotor motor, PublicPlayerState state)
        {
            LocalVelocity = motor.transform.InverseTransformDirection(motor.Body.linearVelocity);
            Speed = new Vector2(LocalVelocity.x, LocalVelocity.z).magnitude;
            Facing = motor.Body.rotation;
            Grounded = motor.Grounded;
            Mode = motor.Mode;
            PublicState = state;
        }
    }

    [DefaultExecutionOrder(50)]
    public sealed class PlayerPresentation : NetworkBehaviour
    {
        [SerializeField] private Transform graphics;
        [SerializeField] private float eyeHeight = 0.75f;
        private PlayerMotor motor;
        private PlayerInputReader input;
        private PlayerNetworkState state;
        private Camera localCamera;
        private Renderer[] bodyRenderers;
        private bool[] bodyRendererStates;
        private uint resetRevision;
        private PlayerSeating seating;
        private NetworkTickSmoother tickSmoother;
        private bool seated;
        public Camera ViewCamera => localCamera;
        public Pose AimPose => seating != null && seating.Seated ? seating.AimPose :
            new Pose(graphics.position + Vector3.up * eyeHeight, Quaternion.Euler(input.Pitch, input.Yaw, 0f));
        internal Transform Graphics => graphics;
        public PlayerSnapshot Snapshot => new(motor, state.Snapshot);
        public float GraphicsOffset => Vector3.Distance(graphics.position, transform.position);

        private void Awake()
        {
            motor = GetComponent<PlayerMotor>();
            input = GetComponent<PlayerInputReader>();
            state = GetComponent<PlayerNetworkState>();
            seating = GetComponent<PlayerSeating>();
            tickSmoother = graphics.GetComponent<NetworkTickSmoother>();
            bodyRenderers = graphics.GetComponentsInChildren<Renderer>(true);
            bodyRendererStates = new bool[bodyRenderers.Length];
            for (int i = 0; i < bodyRenderers.Length; i++)
                bodyRendererStates[i] = bodyRenderers[i].enabled;
        }

        public override void OnStartClient()
        {
            if (!IsOwner) return;
            var cameraObject = new GameObject("Local player camera", typeof(Camera), typeof(AudioListener));
            localCamera = cameraObject.GetComponent<Camera>();
            localCamera.nearClipPlane = 0.1f;
            localCamera.farClipPlane = 250f;
            localCamera.tag = "MainCamera";
            for (int i = 0; i < bodyRenderers.Length; i++)
                bodyRenderers[i].enabled = false;
            UpdateCamera();
        }

        private void LateUpdate()
        {
            if (!seated && motor.ResetRevision != resetRevision)
            {
                resetRevision = motor.ResetRevision;
                // Restart only the presentation buffer. Authoritative reconcile discards the old fall state.
                var smoother = tickSmoother.SmootherController;
                smoother?.StopSmoother();
                graphics.SetPositionAndRotation(transform.position, transform.rotation);
                smoother?.StartSmoother();
            }
            UpdateCamera();
        }

        private void UpdateCamera()
        {
            if (localCamera == null) return;
            var aim = AimPose;
            localCamera.transform.SetPositionAndRotation(aim.position, aim.rotation);
        }

        internal void SetSeated(bool value)
        {
            seated = value;
            var smoother = tickSmoother.SmootherController;
            smoother?.StopSmoother();
            graphics.SetPositionAndRotation(transform.position, transform.rotation);
            if (!value) smoother?.StartSmoother();
        }

        public override void OnStopClient() => ReleaseCamera();
        public override void OnOwnershipClient(NetworkConnection previousOwner)
        {
            if (!IsOwner) ReleaseCamera();
        }

        private void ReleaseCamera()
        {
            if (localCamera == null) return;
            localCamera.gameObject.SetActive(false);
            Destroy(localCamera.gameObject);
            localCamera = null;
            for (int i = 0; i < bodyRenderers.Length; i++)
                if (bodyRenderers[i] != null) bodyRenderers[i].enabled = bodyRendererStates[i];
        }
    }
}
