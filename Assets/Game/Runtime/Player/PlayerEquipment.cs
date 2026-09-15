using FishNet.Object;
using UnityEngine;

namespace TwoBirds
{
    public sealed class PlayerEquipment : NetworkBehaviour
    {
        [SerializeField] private Transform equipSlot;
        private PlayerPresentation presentation;
        private Transform viewmodelSlot;
        public Transform HeldTransform => IsOwner && viewmodelSlot != null ? viewmodelSlot : equipSlot;

        private void Awake() => presentation = GetComponent<PlayerPresentation>();

        public override void OnStartClient()
        {
            if (IsOwner)
            {
                viewmodelSlot = new GameObject("ViewmodelSlot").transform;
                viewmodelSlot.SetParent(presentation.ViewCamera.transform, false);
                viewmodelSlot.localPosition = new Vector3(0.35f, -0.3f, 0.6f);
                viewmodelSlot.localScale = Vector3.one * 0.5f;
            }
            WorldItemRegistry.Instance?.RefreshHolders();
        }

        public override void OnStopClient()
        {
            if (viewmodelSlot != null) Destroy(viewmodelSlot.gameObject);
        }
    }
}
