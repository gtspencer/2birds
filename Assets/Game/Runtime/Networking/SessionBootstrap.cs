using UnityEngine;
using UnityEngine.InputSystem;

namespace TwoBirds
{
    public sealed class SessionBootstrap : MonoBehaviour
    {
        [SerializeField] private GameObject sessionPrefab;
        private void Awake()
        {
            InputSystem.actions.FindActionMap("Player").Disable();
            InputSystem.actions.FindActionMap("UI").Enable();
            if (SessionController.Instance == null) Instantiate(sessionPrefab);
        }
    }
}
