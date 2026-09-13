using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

namespace TwoBirds.Editor
{
    public static class InventoryAssets
    {
        [MenuItem("Two Birds/Install Inventory Assets")]
        public static void Install()
        {
            var catalog = AssetDatabase.LoadAssetAtPath<ItemCatalog>("Assets/Game/Settings/ItemCatalog.asset");
            if (catalog == null)
            {
                catalog = ScriptableObject.CreateInstance<ItemCatalog>();
                AssetDatabase.CreateAsset(catalog, "Assets/Game/Settings/ItemCatalog.asset");
            }
            string path = "Assets/Game/Prefabs/Player.prefab";
            var player = PrefabUtility.LoadPrefabContents(path);
            try
            {
                var inventory = player.GetComponent<PlayerInventory>() ?? player.AddComponent<PlayerInventory>();
                var data = new SerializedObject(inventory);
                data.FindProperty("catalog").objectReferenceValue = catalog;
                data.ApplyModifiedPropertiesWithoutUndo();
                if (player.GetComponent<PlayerHealth>() == null) player.AddComponent<PlayerHealth>();
                PrefabUtility.SaveAsPrefabAsset(player, path);
            }
            finally { PrefabUtility.UnloadPrefabContents(player); }
            var setup = EditorSceneManager.GetSceneManagerSetup();
            try
            {
                var scene = EditorSceneManager.OpenScene("Assets/Scenes/Game.unity");
                foreach (var root in scene.GetRootGameObjects())
                    foreach (var overlay in root.GetComponentsInChildren<SessionOverlay>(true))
                        if (overlay.GetComponent<InventoryHudPresenter>() == null) overlay.gameObject.AddComponent<InventoryHudPresenter>();
                EditorSceneManager.SaveScene(scene);
            }
            finally { if (!Application.isBatchMode) EditorSceneManager.RestoreSceneManagerSetup(setup); }
            AssetDatabase.SaveAssets();
            Validate();
        }
        public static void Validate()
        {
            var player = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Game/Prefabs/Player.prefab");
            Require(player.GetComponent<PlayerInventory>() != null && player.GetComponent<PlayerHealth>() != null, "Player inventory/health missing");
            var catalog = player.GetComponent<PlayerInventory>().Catalog;
            Require(catalog != null, "Player catalog missing");
            catalog.Validate();
            foreach (string guid in AssetDatabase.FindAssets("t:ItemCatalog"))
                AssetDatabase.LoadAssetAtPath<ItemCatalog>(AssetDatabase.GUIDToAssetPath(guid)).Validate();
            var definitionIds = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
            foreach (string guid in AssetDatabase.FindAssets("t:ItemDefinition"))
            {
                var definition = AssetDatabase.LoadAssetAtPath<ItemDefinition>(AssetDatabase.GUIDToAssetPath(guid));
                Require(!string.IsNullOrWhiteSpace(definition.DefinitionId) && definition.MaximumStack > 0 && definitionIds.Add(definition.DefinitionId), "Invalid or duplicate definition");
            }
            var actions = AssetDatabase.LoadAssetAtPath<InputActionAsset>("Assets/InputSystem_Actions.inputactions");
            Require(actions.FindAction("UI/Inventory", true).bindings.Count == 2, "Inventory toggle bindings missing");
            for (int i = 1; i <= 8; i++) Require(actions.FindAction("UI/Slot" + i, true).bindings[0].path == "<Keyboard>/" + i, "Selection binding missing");
            var tree = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>("Assets/Game/UI/Session.uxml").CloneTree();
            for (int i = 0; i < 3; i++) Require(tree.Q("inventory-row-" + i) != null, "Inventory row missing");
            Require(tree.Q("hotbar") != null && tree.Q("health-text") != null && tree.Q("inventory-close") != null, "HUD structure missing");
            var scene = UnityEngine.SceneManagement.SceneManager.GetSceneByPath("Assets/Scenes/Game.unity");
            bool opened = !scene.isLoaded;
            if (opened) scene = EditorSceneManager.OpenScene("Assets/Scenes/Game.unity", OpenSceneMode.Additive);
            try
            {
                int presenters = 0;
                foreach (var root in scene.GetRootGameObjects())
                    foreach (var transform in root.GetComponentsInChildren<Transform>(true))
                    {
                        Require(GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(transform.gameObject) == 0, "Missing scene script");
                        presenters += transform.GetComponents<InventoryHudPresenter>().Length;
                    }
                Require(presenters == 1, "Expected exactly one inventory HUD presenter");
            }
            finally { if (opened) EditorSceneManager.CloseScene(scene, true); }
            Debug.Log("Inventory asset configuration checks passed.");
        }
        private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    }
}
