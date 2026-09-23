#if UNITY_INCLUDE_INSTRUMENTATION
using UnityEngine;
using UnityEngine.InputSystem;

namespace TwoBirds
{
    public sealed class LogMarkerService : MonoBehaviour
    {
        private InputAction marker;
        private InputAction screenshot;

        private void Start()
        {
            marker = new InputAction("Log marker", InputActionType.Button);
            marker.AddBinding("<Keyboard>/p");
            marker.AddBinding("<Gamepad>/dpad/left");
            marker.performed += _ => Log.Marker();
            marker.Enable();
            screenshot = new InputAction("AI screenshot", InputActionType.Button, "<Keyboard>/leftBracket");
            screenshot.performed += _ => AILogger.Screenshot();
            screenshot.Enable();
        }

        private void OnDestroy()
        {
            marker?.Dispose();
            screenshot?.Dispose();
        }
    }
}
#endif
