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
        private PlayerHealth health;
        private PlayerRagdoll ragdoll;
        private PlayerNameLabel nameLabel;
        private int cameraMask;
        internal static event System.Action<Camera> LocalCameraChanged;
        [SerializeField] private Renderer[] fallbackRenderers = System.Array.Empty<Renderer>();
        private bool[] bodyRendererStates;
        private uint resetRevision;
        private PlayerSeating seating;
        private NetworkTickSmoother tickSmoother;
        private bool seated;
        private Vector3 releaseOffset;
        private TransformProperties releaseTracker;
        private float releaseBlendRemaining;
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
            health = GetComponent<PlayerHealth>();
            ragdoll = GetComponent<PlayerRagdoll>();
            nameLabel = GetComponentInChildren<PlayerNameLabel>(true);
            cameraMask = LayerMask.GetMask("Ground", "Environment", "GolfCart");
            tickSmoother = graphics.GetComponent<NetworkTickSmoother>();
            bodyRendererStates = new bool[fallbackRenderers.Length];
            for (int i = 0; i < fallbackRenderers.Length; i++)
                bodyRendererStates[i] = fallbackRenderers[i] && fallbackRenderers[i].enabled;
        }

        public override void OnStartClient()
        {
            if (nameLabel) nameLabel.gameObject.SetActive(!IsOwner);
            if (!IsOwner) return;
            CreateCamera();
        }

        private void CreateCamera()
        {
            if (localCamera) return;
            var cameraObject = new GameObject("Local player camera", typeof(Camera), typeof(AudioListener));
            localCamera = cameraObject.GetComponent<Camera>();
            localCamera.nearClipPlane = 0.1f;
            localCamera.farClipPlane = 250f;
            localCamera.tag = "MainCamera";
            SetFallbackVisible(false);
            UpdateCamera();
            LocalCameraChanged?.Invoke(localCamera);
        }

        private void LateUpdate()
        {
            if (!seated && motor.ResetRevision != resetRevision)
            {
                resetRevision = motor.ResetRevision;
                // Restart only the presentation buffer. Authoritative reconcile discards the old fall state.
                SetSeated(false);
            }
            if (releaseBlendRemaining > 0f)
            {
                releaseBlendRemaining = Mathf.Max(0f, releaseBlendRemaining - Time.deltaTime);
                UpdateReleaseOffset();
            }
            UpdateCamera();
        }

        private void UpdateCamera()
        {
            if (localCamera == null) return;
            if (health.IsDowned)
            {
                const float radius = 0.18f;
                Vector3 root = ragdoll.RootPosition;
                Vector3 target = root + Vector3.up * 0.45f;
                if (Physics.SphereCast(root, radius, Vector3.up, out var overhead, 0.45f, cameraMask, QueryTriggerInteraction.Ignore))
                    target = root + Vector3.up * Mathf.Max(0f, overhead.distance - 0.02f);
                if (Physics.CheckSphere(target, radius, cameraMask, QueryTriggerInteraction.Ignore)) target = root;
                Quaternion rotation = Quaternion.Euler(input.Pitch, input.Yaw, 0f);
                Vector3 direction = rotation * Vector3.back;
                float distance = Physics.SphereCast(target, radius, direction, out var wall, 3f, cameraMask, QueryTriggerInteraction.Ignore)
                    ? Mathf.Max(0f, wall.distance - 0.02f) : 3f;
                localCamera.transform.SetPositionAndRotation(target + direction * distance, rotation);
                return;
            }
            var aim = AimPose;
            localCamera.transform.SetPositionAndRotation(aim.position, aim.rotation);
        }

        internal void SetSeated(bool value, Pose? releasePreview = null)
        {
            seated = value;
            releaseBlendRemaining = 0f;
            var smoother = tickSmoother.SmootherController;
            smoother?.StopSmoother();
            graphics.SetPositionAndRotation(transform.position, transform.rotation);
            if (!value) smoother?.StartSmoother();
            if (!value && releasePreview.HasValue && smoother != null)
            {
                releaseTracker = smoother.UniversalSmoother.GetGraphicalTrackerLocalProperties();
                releaseOffset = releasePreview.Value.position - graphics.position;
                releaseBlendRemaining = 0.5f;
                UpdateReleaseOffset();
                graphics.SetPositionAndRotation(releasePreview.Value.position, releasePreview.Value.rotation);
                smoother.UniversalSmoother.OnPreTick();
            }
        }

        private void UpdateReleaseOffset()
        {
            var properties = releaseTracker;
            properties.Position += transform.InverseTransformVector(releaseOffset * (releaseBlendRemaining / 0.5f));
            tickSmoother.SmootherController.UniversalSmoother.TrySetGraphicalTrackerLocalProperties(properties);
        }

        public override void OnStopClient()
        {
            releaseBlendRemaining = 0f;
            ReleaseCamera();
        }
        public override void OnOwnershipClient(NetworkConnection previousOwner)
        {
            if (nameLabel) nameLabel.gameObject.SetActive(!IsOwner);
            if (IsOwner) CreateCamera();
            else ReleaseCamera();
        }

        private void ReleaseCamera()
        {
            if (localCamera == null) return;
            localCamera.gameObject.SetActive(false);
            Destroy(localCamera.gameObject);
            localCamera = null;
            LocalCameraChanged?.Invoke(null);
        }

        public void SetFallbackVisible(bool value)
        {
            for (int i = 0; i < fallbackRenderers.Length; i++)
                if (fallbackRenderers[i]) fallbackRenderers[i].enabled = value && IsClientInitialized && !IsOwner && bodyRendererStates[i];
        }
    }
}
