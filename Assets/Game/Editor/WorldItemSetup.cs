using System.IO;
using FishNet.Component.Prediction;
using UnityEditor;
using UnityEngine;
using UnityEngine.InputSystem;

namespace TwoBirds.Editor
{
    [InitializeOnLoad]
    public static class WorldItemSetup
    {
        private const string SessionPath = "Assets/Game/Prefabs/SessionRoot.prefab";
        private const string PlayerPath = "Assets/Game/Prefabs/Player.prefab";
        private const string InputPath = "Assets/InputSystem_Actions.inputactions";

        static WorldItemSetup()
        {
            EditorApplication.delayCall += Install;
            EditorApplication.playModeStateChanged += state =>
            {
                if (state == PlayModeStateChange.EnteredEditMode) Install();
            };
        }

        [MenuItem("Two Birds/Set Up World Item Physics")]
        public static void Install()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) return;
            var sessionAsset = AssetDatabase.LoadAssetAtPath<GameObject>(SessionPath);
            if (sessionAsset == null || sessionAsset.GetComponent<WorldItemRegistry>() != null) return;
            var definitions = AssetDatabase.LoadAssetAtPath<ItemRegistry>("Assets/Game/ScriptableObjects/ItemRegistry.asset");
            ConfigureLayers();
            foreach (var definition in definitions.Items)
                ConfigureItem(definition);
            ConfigurePlayer();
            ConfigureInput();

            var session = PrefabUtility.LoadPrefabContents(SessionPath);
            try
            {
                var registry = session.AddComponent<WorldItemRegistry>();
                var serialized = new SerializedObject(registry);
                serialized.FindProperty("itemRegistry").objectReferenceValue = definitions;
                serialized.FindProperty("settings").objectReferenceValue =
                    AssetDatabase.LoadAssetAtPath<GameSettings>("Assets/Game/Settings/GameSettings.asset");
                serialized.ApplyModifiedPropertiesWithoutUndo();
                PrefabUtility.SaveAsPrefabAsset(session, SessionPath);
            }
            finally { PrefabUtility.UnloadPrefabContents(session); }
        }

        private static void ConfigureLayers()
        {
            var tags = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset")[0]);
            var layers = tags.FindProperty("layers");
            layers.GetArrayElementAtIndex(8).stringValue = "ItemHeld";
            layers.GetArrayElementAtIndex(9).stringValue = "ItemWorld";
            layers.GetArrayElementAtIndex(10).stringValue = "PlayerItemHitbox";
            tags.ApplyModifiedPropertiesWithoutUndo();
            AssetDatabase.SaveAssetIfDirty(tags.targetObject);

            for (int layer = 0; layer < 32; layer++)
            {
                Physics.IgnoreLayerCollision(8, layer, true);
                Physics.IgnoreLayerCollision(9, layer, layer == 6 || layer == 8 || layer == 5);
                Physics.IgnoreLayerCollision(10, layer, layer != 9);
            }
            var physics = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/DynamicsManager.asset")[0];
            EditorUtility.SetDirty(physics);
            AssetDatabase.SaveAssetIfDirty(physics);
        }

        private static void ConfigureItem(ItemDefinition definition)
        {
            if (definition == null || definition.WorldPrefab == null) return;
            string path = AssetDatabase.GetAssetPath(definition.WorldPrefab);
            var root = PrefabUtility.LoadPrefabContents(path);
            try
            {
                var visual = root.transform.Find("VisualRoot");
                if (visual == null)
                {
                    visual = new GameObject("VisualRoot").transform;
                    visual.SetParent(root.transform, false);
                    for (int i = root.transform.childCount - 1; i >= 0; i--)
                    {
                        var child = root.transform.GetChild(i);
                        if (child != visual && child.GetComponentInChildren<Collider>() == null) child.SetParent(visual, false);
                    }
                    var filter = root.GetComponent<MeshFilter>();
                    var renderer = root.GetComponent<MeshRenderer>();
                    if (filter != null)
                    {
                        EditorUtility.CopySerialized(filter, visual.gameObject.AddComponent<MeshFilter>());
                        Object.DestroyImmediate(filter);
                    }
                    if (renderer != null)
                    {
                        EditorUtility.CopySerialized(renderer, visual.gameObject.AddComponent<MeshRenderer>());
                        Object.DestroyImmediate(renderer);
                    }
                }
                foreach (var child in root.GetComponentsInChildren<Transform>(true)) child.gameObject.layer = 9;
                var body = root.GetComponent<Rigidbody>();
                if (body == null) body = root.AddComponent<Rigidbody>();
                body.mass = definition.Mass;
                body.linearDamping = definition.LinearDamping;
                body.angularDamping = definition.AngularDamping;
                body.collisionDetectionMode = definition.CollisionDetection;
                body.isKinematic = false;
                if (root.GetComponent<OfflineRigidbody>() == null) root.AddComponent<OfflineRigidbody>();
                var item = root.GetComponent<WorldItem>();
                if (item == null) item = root.AddComponent<WorldItem>();
                var serialized = new SerializedObject(item);
                serialized.FindProperty("visualRoot").objectReferenceValue = visual;
                serialized.ApplyModifiedPropertiesWithoutUndo();
                PrefabUtility.SaveAsPrefabAsset(root, path);
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }
        }

        private static void ConfigurePlayer()
        {
            var player = PrefabUtility.LoadPrefabContents(PlayerPath);
            try
            {
                if (player.GetComponentInChildren<PlayerItemHitbox>() == null)
                {
                    var proxy = new GameObject("ItemHitbox");
                    proxy.layer = 10;
                    proxy.transform.SetParent(player.transform, false);
                    var body = proxy.AddComponent<Rigidbody>();
                    body.isKinematic = true;
                    body.useGravity = false;
                    var capsule = proxy.AddComponent<CapsuleCollider>();
                    EditorUtility.CopySerialized(player.GetComponent<CapsuleCollider>(), capsule);
                    proxy.AddComponent<PlayerItemHitbox>();
                    proxy.AddComponent<OfflineRigidbody>();
                }
                var equip = player.transform.Find("Graphics/EquipSlot");
                if (equip == null)
                {
                    equip = new GameObject("EquipSlot").transform;
                    equip.SetParent(player.transform.Find("Graphics"), false);
                    equip.localPosition = new Vector3(0.45f, 0.1f, 0.45f);
                }
                var equipment = new SerializedObject(player.GetComponent<PlayerEquipment>());
                equipment.FindProperty("equipSlot").objectReferenceValue = equip;
                equipment.ApplyModifiedPropertiesWithoutUndo();
                PrefabUtility.SaveAsPrefabAsset(player, PlayerPath);
            }
            finally { PrefabUtility.UnloadPrefabContents(player); }
        }

        private static void ConfigureInput()
        {
            var actions = InputActionAsset.FromJson(File.ReadAllText(InputPath));
            var player = actions.FindActionMap("Player");
            if (player.FindAction("Throw") == null)
            {
                var throwItem = player.AddAction("Throw", InputActionType.Button);
                throwItem.AddBinding("<Mouse>/leftButton", groups: "Keyboard&Mouse");
                throwItem.AddBinding("<Gamepad>/rightTrigger", groups: "Gamepad");
                player.FindAction("Drop").AddBinding("<Gamepad>/dpad/down", groups: "Gamepad");
                File.WriteAllText(InputPath, actions.ToJson());
                AssetDatabase.ImportAsset(InputPath);
            }
            Object.DestroyImmediate(actions);
        }
    }
}
