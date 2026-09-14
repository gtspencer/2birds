using System;
using FishNet.Component.Transforming.Beta;
using FishNet.Managing;
using FishNet.Object;
using UnityEditor;
using UnityEngine;

namespace TwoBirds.Editor
{
    public static class MvpChecks
    {
        [MenuItem("Two Birds/Validate MVP Configuration")]
        public static void Run()
        {
            foreach (string invalid in new[] { "", "0", "65536", "-1", "abc", "7770.0" })
                Require(!EndpointUtility.TryPort(invalid, out _), "Invalid port accepted: " + invalid);
            foreach (string valid in new[] { "1", "65535", " 7770 " })
                Require(EndpointUtility.TryPort(valid, out _), "Valid port rejected: " + valid);
            foreach (string invalid in new[] { "", "localhost", "::1", "127.1", "2130706433", "0.0.0.0", "255.255.255.255", "224.0.0.1", "239.1.2.3", "256.1.1.1", "1.2.3.4:7770" })
                Require(!EndpointUtility.TryAddress(invalid, out _), "Invalid address accepted: " + invalid);
            Require(EndpointUtility.TryAddress(" 127.0.0.1 ", out string loopback) && loopback == "127.0.0.1", "Loopback is not canonicalized.");
            Require(EndpointUtility.TryAddress("192.168.1.50", out _), "LAN address rejected.");
            var scenes = EditorBuildSettings.scenes;
            Require(scenes.Length == 2 && scenes[0].enabled && scenes[1].enabled &&
                scenes[0].path == "Assets/Scenes/MainMenu.unity" && scenes[1].path == "Assets/Scenes/Game.unity", "Build scenes differ from MVP.");
            var player = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Game/Prefabs/Player.prefab");
            var root = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Game/Prefabs/SessionRoot.prefab");
            Require(player != null && root != null, "Missing prefabs.");
            Require(new SerializedObject(root.GetComponent<SessionController>()).FindProperty("settings").objectReferenceValue != null, "Session settings reference is missing.");
            Require(new SerializedObject(player.GetComponent<PlayerMotor>()).FindProperty("settings").objectReferenceValue != null, "Motor settings reference is missing.");
            Require(player.GetComponent<Rigidbody>().interpolation == RigidbodyInterpolation.None, "Rigidbody interpolation stacks with graphical smoothing.");
            Require(player.GetComponentsInChildren<Collider>().Length == 1, "Capsule has duplicate colliders.");
            Require(player.GetComponentsInChildren<NetworkTickSmoother>().Length == 1, "Expected one graphical smoother.");
            Require(player.GetComponent<NetworkObject>().GetGraphicalObject() == null, "Legacy smoother must be unassigned.");
            Require(root.GetComponent<NetworkManager>().SpawnablePrefabs.GetObjectCount() == 1, "Spawn registry must contain only Player.");
            Require(root.GetComponentsInChildren<NetworkObject>().Length == 0, "Persistent services contain network scene objects.");
            var transport = new SerializedObject(root.GetComponent<GameTransport>());
            Require(!transport.FindProperty("_enableIpv6").boolValue, "IPv6 must be disabled.");
            Require(!transport.FindProperty("_reuseAddress").boolValue, "Socket reuse must be disabled.");
            Debug.Log("MVP endpoint and asset configuration checks passed.");
        }
        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
