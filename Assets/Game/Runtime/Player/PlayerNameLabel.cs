using FishNet.Object;
using TMPro;
using UnityEngine;

namespace TwoBirds
{
    [DefaultExecutionOrder(210)]
    public sealed class PlayerNameLabel : MonoBehaviour
    {
        private const float PanelClearance = 0.3f;
        [SerializeField] private TMP_Text label;
        [SerializeField] private Transform healthBarFill;
        private PlayerHealth health;
        private PlayerRagdoll ragdoll;
        private Vector3 fillScale, fillPosition;

        private Transform cameraTransform;
        private PlayerAvatarPresentation avatar;
        private Vector3 fallbackPosition;

        private void Awake()
        {
            avatar = GetComponentInParent<PlayerAvatarPresentation>();
            health = GetComponentInParent<PlayerHealth>();
            ragdoll = GetComponentInParent<PlayerRagdoll>();
            fallbackPosition = transform.localPosition;
            fillScale = healthBarFill.localScale;
            fillPosition = healthBarFill.localPosition;
        }
        private void OnEnable()
        {
            health.HealthChanged += RefreshHealth;
            PlayerPresentation.LocalCameraChanged += CameraChanged;
            RefreshHealth();
        }
        private void OnDisable()
        {
            health.HealthChanged -= RefreshHealth;
            PlayerPresentation.LocalCameraChanged -= CameraChanged;
        }
        private void CameraChanged(Camera camera) => cameraTransform = camera ? camera.transform : null;
        private void RefreshHealth()
        {
            Vector3 scale = fillScale;
            scale.x *= health.Normalized;
            healthBarFill.localScale = scale;
            healthBarFill.localPosition = fillPosition + Vector3.left * ((fillScale.x - scale.x) * 0.5f);
        }

        private void Start()
        {
            var networkObject = GetComponentInParent<NetworkObject>();
            if (!networkObject) return;

            string displayName = null;
            int clientId = networkObject.Owner.ClientId;
            var roster = SessionController.Instance.Roster;
            for (int i = 0; i < roster.Length; i++)
            {
                if (roster[i].Connection == clientId)
                {
                    displayName = roster[i].Name;
                    break;
                }
            }

            label.text = displayName ?? $"Player {clientId + 1}";

            var localPlayer = SessionController.Instance.LocalPlayer;
            if (localPlayer)
            {
                var cam = localPlayer.GetComponent<PlayerPresentation>().ViewCamera;
                if (cam) cameraTransform = cam.transform;
            }
        }

        private void LateUpdate()
        {
            if (health.IsDowned) transform.position = ragdoll.RootPosition + Vector3.up * 0.9f;
            else if (avatar && avatar.Presentation.TryGetNameAnchor(out var anchor)) transform.position = anchor + Vector3.up * PanelClearance;
            else transform.localPosition = fallbackPosition;
            if (!cameraTransform) return;

            transform.forward = cameraTransform.forward;
        }
    }
}
