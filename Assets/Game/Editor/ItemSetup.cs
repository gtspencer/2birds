using System.IO;
using FishNet.Component.Prediction;
using UnityEditor;
using UnityEngine;

namespace TwoBirds.Editor
{
    public sealed class ItemSetup : EditorWindow
    {
        private const string PrefabFolder = "Assets/Game/Prefabs/Items";
        private const string DefinitionFolder = "Assets/Game/ScriptableObjects/Items";
        private const string RegistryPath = "Assets/Game/ScriptableObjects/ItemRegistry.asset";
        private const string IconFolder = "Assets/Game/UI/Icons";

        private enum SetupMode { Item, Potion }
        private SetupMode mode;
        private PotionEffect effect;
        private Color potionColor = Color.red;
        private GameObject source;
        private string itemName;
        private bool addThrowable = true;
        private bool captureIcon = true;
        private int iconResolution = 128;
        private ItemDefinition existingItem;

        [MenuItem("Two Birds/Item Setup")]
        public static void ShowWindow() => GetWindow<ItemSetup>("Item Setup").mode = SetupMode.Item;

        [MenuItem("Two Birds/Potion Setup")]
        public static void ShowPotionWindow() => GetWindow<ItemSetup>("Item Setup").mode = SetupMode.Potion;

        private void OnGUI()
        {
            mode = (SetupMode)EditorGUILayout.EnumPopup("Mode", mode);
            if (mode == SetupMode.Potion)
            {
                source = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabFolder + "/Potion.prefab");
                effect = (PotionEffect)EditorGUILayout.EnumPopup("Effect", effect);
                potionColor = EditorGUILayout.ColorField("Color", potionColor);
            }
            else source = (GameObject)EditorGUILayout.ObjectField("Source", source, typeof(GameObject), true);

            if (source != null && string.IsNullOrEmpty(itemName)) itemName = source.name;
            itemName = EditorGUILayout.TextField("Item Name", itemName);
            if (mode == SetupMode.Item) addThrowable = EditorGUILayout.Toggle("Add ThrowableItemUse", addThrowable);
            captureIcon = EditorGUILayout.Toggle("Capture Screenshot Icon", captureIcon);
            if (captureIcon)
                iconResolution = EditorGUILayout.IntPopup("Icon Resolution", iconResolution,
                    new[] { "64", "128", "256", "512" }, new[] { 64, 128, 256, 512 });

            EditorGUILayout.Space(6);
            EditorGUI.BeginDisabledGroup(source == null || string.IsNullOrWhiteSpace(itemName));
            if (GUILayout.Button("Create Item", GUILayout.Height(28))) CreateItem();
            EditorGUI.EndDisabledGroup();

            EditorGUILayout.Space(6);
            existingItem = (ItemDefinition)EditorGUILayout.ObjectField("Existing Item", existingItem, typeof(ItemDefinition), false);
            using (new EditorGUI.DisabledScope(!existingItem || !existingItem.WorldPrefab || existingItem.HoldMode == ItemHoldMode.Heavy))
                if (GUILayout.Button("Recompute Held Offset"))
                {
                    Undo.RecordObject(existingItem, "Recompute Held Offset");
                    if (GenerateHeldOffset(existingItem.WorldPrefab, existingItem)) AssetDatabase.SaveAssetIfDirty(existingItem);
                }
        }

        private void CreateItem()
        {
            CreateDefinition(itemName, source, mode == SetupMode.Potion, effect, potionColor, addThrowable, captureIcon, iconResolution);
        }

        public static ItemDefinition CreateDefinition(string itemName, GameObject source, bool potion, PotionEffect effect,
            Color color, bool addThrowable = true, bool captureIcon = true, int iconResolution = 128)
        {
            byte id = GetNextItemId();
            if (id == 0) { Debug.LogError("All nonzero item IDs are in use."); return null; }
            if (potion) source = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabFolder + "/Potion.prefab");
            if (!source) return null;
            EnsureFolder(PrefabFolder);
            EnsureFolder(DefinitionFolder);
            EnsureFolder(IconFolder);

            string safeName = MakeAssetName(itemName);
            string definitionPath = AssetDatabase.GenerateUniqueAssetPath($"{DefinitionFolder}/{safeName}.asset");
            ItemDefinition definition = potion ? CreateInstance<PotionDefinition>() : CreateInstance<ItemDefinition>();
            if (definition is PotionDefinition dose)
            {
                dose.Effect = effect; dose.Color = color;
                dose.Application = effect == PotionEffect.Bouncy ? PotionApplication.Pulse : PotionApplication.Zone;
            }
            definition.name = safeName;
            definition.ItemName = itemName;
            definition.ItemId = id;
            AssetDatabase.CreateAsset(definition, definitionPath);
            EditorUtility.SetDirty(definition);

            AddToRegistry(definition);
            GameObject prefab;
            if (potion)
            {
                prefab = source;
                GenerateHeldOffset(prefab, definition);
            }
            else
            {
                var root = InstantiateSource(source);
                root.name = safeName;
                PrepareVisualRoot(root);
                definition.GripEuler = root.transform.localEulerAngles;
                GenerateHeldOffset(root, definition);
                AddComponents(root, definition, addThrowable);
                root.hideFlags = HideFlags.None;
    
                string prefabPath = AssetDatabase.GenerateUniqueAssetPath($"{PrefabFolder}/{safeName}.prefab");
                prefab = PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
                DestroyImmediate(root);
    
            }

            definition.WorldPrefab = prefab;
            EditorUtility.SetDirty(definition);
            AssetDatabase.SaveAssets();

            if (captureIcon)
            {
                IconCaptureWindow.CaptureAndSave(prefab, iconResolution, IconFolder,
                    new Color(0.15f, 0.15f, 0.15f, 0f), 0.1f, new Vector2(25f, -135f), definition);
                EditorUtility.SetDirty(definition);
                AssetDatabase.SaveAssets();
            }

            Selection.activeObject = prefab;
            EditorGUIUtility.PingObject(prefab);
            return definition;
        }

        private static GameObject InstantiateSource(GameObject source)
        {
            var root = PrefabUtility.IsPartOfPrefabAsset(source)
                ? (GameObject)PrefabUtility.InstantiatePrefab(source)
                : Object.Instantiate(source);
            root.hideFlags = HideFlags.HideInHierarchy;
            return root;
        }

        private static void PrepareVisualRoot(GameObject root)
        {
            var visual = root.transform.Find("VisualRoot");
            if (visual == null)
            {
                visual = new GameObject("VisualRoot").transform;
                visual.SetParent(root.transform, false);
                CopyVisualComponent<MeshFilter>(root, visual.gameObject);
                CopyVisualComponent<MeshRenderer>(root, visual.gameObject);
                CopyVisualComponent<SkinnedMeshRenderer>(root, visual.gameObject);
            }

            for (int i = root.transform.childCount - 1; i >= 0; i--)
            {
                var child = root.transform.GetChild(i);
                if (child != visual && child.GetComponentInChildren<Collider>(true) == null)
                    child.SetParent(visual, true);
            }
        }

        private static void CopyVisualComponent<T>(GameObject source, GameObject destination) where T : Component
        {
            var component = source.GetComponent<T>();
            if (component == null) return;
            var copy = destination.AddComponent<T>();
            EditorUtility.CopySerialized(component, copy);
            DestroyImmediate(component);
        }

        private static void AddComponents(GameObject root, ItemDefinition definition, bool addThrowable)
        {
            foreach (var child in root.GetComponentsInChildren<Transform>(true)) child.gameObject.layer = 9;

            var pickup = root.GetComponent<BakedPickup>() ?? root.AddComponent<BakedPickup>();
            pickup.Item = definition;

            if (root.GetComponent<Collider>() == null)
            {
                var bounds = CalculateBounds(root);
                if (bounds.size != Vector3.zero)
                {
                    var collider = root.AddComponent<BoxCollider>();
                    collider.center = root.transform.InverseTransformPoint(bounds.center);
                    collider.size = bounds.size;
                }
            }

            var body = root.GetComponent<Rigidbody>();
            if (!body) body = root.AddComponent<Rigidbody>();
            if (!body)
            {
                Debug.LogError($"Item Setup could not add a Rigidbody to '{root.name}'.");
                return;
            }
            body.mass = definition.Mass;
            body.linearDamping = definition.LinearDamping;
            body.angularDamping = definition.AngularDamping;
            body.collisionDetectionMode = definition.CollisionDetection;
            body.isKinematic = false;

            if (root.GetComponent<OfflineRigidbody>() == null) root.AddComponent<OfflineRigidbody>();
            var item = root.GetComponent<WorldItem>() ?? root.AddComponent<WorldItem>();
            var serialized = new SerializedObject(item);
            serialized.FindProperty("visualRoot").objectReferenceValue = root.transform.Find("VisualRoot");
            serialized.ApplyModifiedPropertiesWithoutUndo();

            var throwable = root.GetComponent<ThrowableItemUse>();
            if (addThrowable && throwable == null) root.AddComponent<ThrowableItemUse>();
            if (!addThrowable && throwable) DestroyImmediate(throwable);
        }

        private static byte GetNextItemId()
        {
            var registry = AssetDatabase.LoadAssetAtPath<ItemRegistry>(RegistryPath);
            if (registry == null) return 1;
            var used = new bool[256];
            foreach (var item in registry.Items) if (item) used[item.ItemId] = true;
            foreach (string guid in AssetDatabase.FindAssets("t:ItemDefinition"))
            {
                var item = AssetDatabase.LoadAssetAtPath<ItemDefinition>(AssetDatabase.GUIDToAssetPath(guid));
                if (item) used[item.ItemId] = true;
            }
            for (int id = 1; id <= byte.MaxValue; id++) if (!used[id]) return (byte)id;
            return 0;
        }

        private static void AddToRegistry(ItemDefinition definition)
        {
            var registry = AssetDatabase.LoadAssetAtPath<ItemRegistry>(RegistryPath);
            if (registry == null) return;

            var items = registry.Items;
            var updated = new ItemDefinition[items.Length + 1];
            items.CopyTo(updated, 0);
            updated[^1] = definition;
            registry.Items = updated;
            EditorUtility.SetDirty(registry);
        }

        public static bool GenerateHeldOffset(GameObject root, ItemDefinition definition)
        {
            if (definition.HoldMode == ItemHoldMode.Heavy) return false;
            Matrix4x4 toPalm = Matrix4x4.TRS(Vector3.zero, Quaternion.Euler(definition.GripEuler),
                root.transform.localScale) * root.transform.worldToLocalMatrix;
            Bounds bounds = default;
            bool found = false;
            foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                if (!renderer.enabled) continue;
                Mesh mesh = renderer is SkinnedMeshRenderer skin ? skin.sharedMesh : renderer.GetComponent<MeshFilter>()?.sharedMesh;
                if (!mesh || mesh.vertexCount == 0) continue;
                Bounds local = renderer is SkinnedMeshRenderer skinned ? skinned.localBounds : mesh.bounds;
                Matrix4x4 matrix = toPalm * renderer.transform.localToWorldMatrix;
                for (int i = 0; i < 8; i++)
                {
                    Vector3 corner = local.center + Vector3.Scale(local.extents,
                        new Vector3((i & 1) == 0 ? -1f : 1f, (i & 2) == 0 ? -1f : 1f, (i & 4) == 0 ? -1f : 1f));
                    Vector3 point = matrix.MultiplyPoint3x4(corner);
                    if (found) bounds.Encapsulate(point);
                    else { bounds = new Bounds(point, Vector3.zero); found = true; }
                }
            }
            if (!found)
            {
                Debug.LogWarning($"No mesh bounds for '{root.name}'; held offset was kept.", definition);
                return false;
            }
            definition.GripPosition = -bounds.center + Vector3.up * (bounds.extents.y + 0.006f);
            EditorUtility.SetDirty(definition);
            return true;
        }

        private static Bounds CalculateBounds(GameObject root)
        {
            var renderers = root.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length == 0) return new Bounds();
            var bounds = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++) bounds.Encapsulate(renderers[i].bounds);
            return bounds;
        }

        private static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            Directory.CreateDirectory(path);
            AssetDatabase.Refresh();
        }

        private static string MakeAssetName(string value)
        {
            foreach (char character in Path.GetInvalidFileNameChars()) value = value.Replace(character, '_');
            return value.Trim();
        }
    }
}
