using UnityEngine;

namespace TwoBirds
{
    public sealed class AvatarPresentationDemo : MonoBehaviour
    {
        [SerializeField] private AvatarPresentation presentation;
        [SerializeField] private AvatarRegistry registry;
        [SerializeField] private AvatarId avatarA, avatarB;
        [SerializeField] private Transform handTarget;
        [SerializeField] private Camera showcaseCamera;
        [SerializeField] private GameSettings gameSettings;
        private float clock;
        private bool showingB;
        private bool initialized;
        private Pose origin;

        private void Start()
        {
            if (!registry || !registry.TryResolve(avatarA, out var a) || !registry.TryResolve(avatarB, out var b) ||
                !presentation || !handTarget || !showcaseCamera || !gameSettings)
            { Debug.LogError("Assign both processed avatar IDs, registry, host, camera, hand target, and game settings to the showcase.", this); enabled = false; return; }
            origin = new Pose(presentation.transform.position, presentation.transform.rotation);
            Vector3 Shoulder(AvatarSettings s) => origin.position + origin.rotation * (s.StandingOffset +
                Quaternion.Euler(0f, s.YawOffset, 0f) * (s.Generated.RightShoulder * s.Scale));
            Vector3 shoulderA = Shoulder(a.Settings), shoulderB = Shoulder(b.Settings);
            float lengthA = (a.Settings.Generated.RightArm.x + a.Settings.Generated.RightArm.y) * a.Settings.Scale;
            float lengthB = (b.Settings.Generated.RightArm.x + b.Settings.Generated.RightArm.y) * b.Settings.Scale;
            float length = Mathf.Min(lengthA, lengthB);
            Vector3 target = (shoulderA + shoulderB) * 0.5f + origin.rotation * new Vector3(0f, -0.30f * length, 0.35f * length);
            if (Vector3.Distance(target, shoulderA) > 0.9f * lengthA || Vector3.Distance(target, shoulderB) > 0.9f * lengthB)
            { Debug.LogError($"Demo avatars {avatarA} and {avatarB} cannot reach shared target {target}; adjust their authored size/offset settings.", this); enabled = false; return; }
            handTarget.SetPositionAndRotation(target, origin.rotation * Quaternion.LookRotation(Vector3.back, Vector3.up));
            Frame(a.Settings, b.Settings);
            presentation.Configure(registry, true);
            presentation.InputSource = CaptureInput;
            presentation.SetHandTarget(AvatarIKGoal.RightHand, handTarget, 1f, 0.35f);
            presentation.RequestAvatar(avatarA);
            initialized = true;
        }

        private void OnEnable()
        {
            if (!initialized) return;
            presentation.InputSource = CaptureInput;
            presentation.SetVisual(true);
        }

        private AvatarPresentationInput CaptureInput()
        {
            clock = Mathf.Repeat(clock + Mathf.Min(Time.deltaTime, 0.05f), 16f);
            bool next = clock >= 8f;
            if (next != showingB)
            { showingB = next; presentation.RequestAvatar(showingB ? avatarB : avatarA); }
            return new AvatarPresentationInput
            {
                Facing = origin, SolePosition = origin.position, Grounded = true, Mode = MovementMode.Walking,
                LookYaw = origin.rotation.eulerAngles.y + Mathf.Sin(clock * Mathf.PI / 4f) * 25f,
                GroundMask = gameSettings.GroundLayers, WalkSpeed = gameSettings.WalkSpeed, SprintSpeed = gameSettings.SprintSpeed
            };
        }

        private void Frame(AvatarSettings a, AvatarSettings b)
        {
            Bounds bounds = new(origin.position, Vector3.zero);
            Include(a); Include(b);
            void Include(AvatarSettings settings)
            {
                Bounds source = settings.Generated.Bounds;
                for (int i = 0; i < 8; i++)
                {
                    Vector3 corner = source.center + Vector3.Scale(source.extents,
                        new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                    corner.y -= settings.Generated.SolePlane;
                    bounds.Encapsulate(origin.position + origin.rotation * (settings.StandingOffset +
                        Quaternion.Euler(0f, settings.YawOffset, 0f) * corner * settings.Scale));
                }
            }
            showcaseCamera.orthographic = true;
            Vector3 direction = new Vector3(3f, 2f, 5f).normalized;
            showcaseCamera.transform.SetPositionAndRotation(bounds.center + direction * 8f, Quaternion.LookRotation(-direction));
            float extent = 0f;
            for (int i = 0; i < 8; i++)
            {
                Vector3 corner = bounds.center + Vector3.Scale(bounds.extents,
                    new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                Vector3 local = showcaseCamera.transform.InverseTransformPoint(corner);
                extent = Mathf.Max(extent, Mathf.Abs(local.y), Mathf.Abs(local.x) / showcaseCamera.aspect);
            }
            showcaseCamera.orthographicSize = extent * 1.2f;
        }

        private void OnDisable()
        { if (presentation) { presentation.InputSource = null; presentation.SetVisual(false); } }
    }
}
