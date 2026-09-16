using UnityEditor;

namespace TwoBirds.Editor
{
    public static class LocalNetworkingToggle
    {
        private const string Key = "TwoBirds.LocalNetworking";
        private const string Menu = "Tools/Local Networking";

        [MenuItem(Menu, priority = 200)]
        private static void Toggle() => EditorPrefs.SetBool(Key, !EditorPrefs.GetBool(Key, false));

        [MenuItem(Menu, true)]
        private static bool Validate()
        {
            UnityEditor.Menu.SetChecked(Menu, EditorPrefs.GetBool(Key, false));
            return true;
        }
    }
}
