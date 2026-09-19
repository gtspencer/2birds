using UnityEngine;
using UnityEngine.InputSystem;

namespace TwoBirds
{
    public sealed class SessionBootstrap : MonoBehaviour
    {
        private static bool? localNetworking;
        public static bool LocalNetworking => localNetworking ??=
#if UNITY_EDITOR
            UnityEditor.EditorPrefs.GetBool("TwoBirds.LocalNetworking", false) ||
#elif TWO_BIRDS_LOCAL_NETWORKING
            true ||
#endif
            System.Array.Exists(System.Environment.GetCommandLineArgs(),
                argument => argument.Equals("-localNetworking", System.StringComparison.OrdinalIgnoreCase));
        [SerializeField] private GameObject sessionPrefab;
        private void Awake()
        {
            if (!SessionController.Instance) InputBindings.Load(InputSystem.actions);
            InputSystem.actions.FindActionMap("Player").Disable();
            InputSystem.actions.FindActionMap("UI").Enable();
            if (SessionController.Instance == null) Instantiate(sessionPrefab);
        }
    }
}
