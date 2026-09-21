using FishNet.Object;
using TMPro;
using UnityEngine;

namespace TwoBirds
{
    [DefaultExecutionOrder(210)]
    public sealed class PlayerNameLabel : MonoBehaviour
    {
        [SerializeField] private TMP_Text label;

        private Transform cameraTransform;
        private PlayerAvatarPresentation avatar;
        private Vector3 fallbackPosition;

        private void Start()
        {
            avatar = GetComponentInParent<PlayerAvatarPresentation>();
            fallbackPosition = transform.localPosition;
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
            if (avatar && avatar.Presentation.TryGetNameAnchor(out var anchor)) transform.position = anchor;
            else transform.localPosition = fallbackPosition;
            if (!cameraTransform)
            {
                var localPlayer = SessionController.Instance.LocalPlayer;
                if (!localPlayer) return;
                var cam = localPlayer.GetComponent<PlayerPresentation>().ViewCamera;
                if (!cam) return;
                cameraTransform = cam.transform;
            }

            transform.forward = cameraTransform.forward;
        }
    }
}
