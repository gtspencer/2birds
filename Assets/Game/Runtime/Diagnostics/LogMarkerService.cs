#if UNITY_EDITOR || DEVELOPMENT_BUILD
using UnityEngine;
using UnityEngine.InputSystem;

namespace TwoBirds
{
    public sealed class LogMarkerService : MonoBehaviour
    {
        private InputAction marker;

        private void Start()
        {
            marker = new InputAction("Log marker", InputActionType.Button);
            marker.AddBinding("<Keyboard>/p");
            marker.AddBinding("<Gamepad>/dpad/left");
            marker.performed += _ => Log.Marker();
            marker.Enable();
        }

        private void OnDestroy() => marker?.Dispose();
    }
}
#endif
