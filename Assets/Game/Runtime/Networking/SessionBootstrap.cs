using UnityEngine;
using UnityEngine.InputSystem;

namespace TwoBirds
{
    public sealed class SessionBootstrap : MonoBehaviour
    {
        public static bool LocalNetworking { get; } = System.Array.Exists(System.Environment.GetCommandLineArgs(),
            argument => argument.Equals("-localNetworking", System.StringComparison.OrdinalIgnoreCase));
        [SerializeField] private GameObject sessionPrefab;
        private void Awake()
        {
            InputSystem.actions.FindActionMap("Player").Disable();
            InputSystem.actions.FindActionMap("UI").Enable();
            if (SessionController.Instance == null) Instantiate(sessionPrefab);
        }
    }
}
