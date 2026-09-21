using UnityEngine;

namespace TwoBirds
{
    public sealed class CauldronIntake : MonoBehaviour
    {
        private Cauldron cauldron;
        private void Awake() => cauldron = GetComponentInParent<Cauldron>();
        private void OnTriggerEnter(Collider other)
        {
            if (other.attachedRigidbody && other.attachedRigidbody.TryGetComponent<WorldItem>(out var item))
                WorldItemRegistry.Instance.QueueIntake(item, cauldron);
        }
    }
}
