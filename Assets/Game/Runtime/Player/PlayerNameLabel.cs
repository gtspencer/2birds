using FishNet.Object;
using TMPro;
using UnityEngine;

namespace TwoBirds
{
    public sealed class PlayerNameLabel : MonoBehaviour
    {
        [SerializeField] private TMP_Text label;

        private Transform cameraTransform;

        private void Start()
        {
            var networkObject = GetComponentInParent<NetworkObject>();
            if (!networkObject) return;

            if (networkObject.IsOwner)
            {
                gameObject.SetActive(false);
                return;
            }

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
