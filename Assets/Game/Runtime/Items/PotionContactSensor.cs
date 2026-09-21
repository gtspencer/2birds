using UnityEngine;

namespace TwoBirds
{
    public sealed class PotionContactSensor : MonoBehaviour
    {
        private WorldItem item;
        private void Awake() => item = GetComponentInParent<WorldItem>();
        private void OnTriggerEnter(Collider other)
        {
            if (other.TryGetComponent<PlayerEffectReceiver>(out var receiver)) item.PotionReceiverContact(receiver);
        }
    }
}
