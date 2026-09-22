using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace TwoBirds.Editor
{
    public sealed class HatSetup : EditorWindow
    {
        public const string CatalogPath = "Assets/Game/Settings/Cosmetics/HatCatalog.asset";
        private GameObject source;
        private HatDefinition definition;
        private string displayName = "";
        private bool unlocked = true;
        private CosmeticFit fit = CosmeticFit.Identity;
        [MenuItem("Two Birds/Hat Setup")]
        public static void ShowWindow() => GetWindow<HatSetup>("Hat Setup");
        private void OnGUI()
        {
            var selected = (HatDefinition)EditorGUILayout.ObjectField("Existing hat", definition, typeof(HatDefinition), false);
            var next = (GameObject)EditorGUILayout.ObjectField("Source prefab", source, typeof(GameObject), false);
            if (selected != definition) Load(selected);
            else if (next != source)
            {
                source = next; Load(Find(source)); source = next;
                if (!definition) displayName = source ? source.name : "";
            }
            displayName = EditorGUILayout.TextField("Library name", displayName);
            unlocked = EditorGUILayout.Toggle("Unlocked by default", unlocked);
            fit.Position = EditorGUILayout.Vector3Field("Fit position", fit.Position);
            fit.Rotation = EditorGUILayout.Vector3Field("Fit rotation", fit.Rotation);
            fit.Scale = EditorGUILayout.Vector3Field("Fit scale", fit.Scale);
            using (new EditorGUI.DisabledScope(!source || EditorApplication.isPlaying))
                if (GUILayout.Button("Process and capture icon"))
                {
                    definition = Process(source, definition, displayName, unlocked, fit);
                    Selection.activeObject = definition;
                }
            using (new EditorGUI.DisabledScope(!definition || !definition.Visual))
                if (GUILayout.Button("Recapture icon")) Capture(definition);
        }
        private void Load(HatDefinition value)
        {
            definition = value;
            if (!value) { fit = CosmeticFit.Identity; unlocked = true; return; }
            source = value.Source; displayName = value.DisplayName; unlocked = value.UnlockedByDefault; fit = value.Fit;
        }
        private static HatDefinition Find(GameObject source)
        {
            if (!source) return null;
            foreach (var guid in AssetDatabase.FindAssets("t:HatDefinition"))
            {
                var definition = AssetDatabase.LoadAssetAtPath<HatDefinition>(AssetDatabase.GUIDToAssetPath(guid));
                if (definition.Source == source) return definition;
            }
            return null;
        }
        public static HatDefinition Process(GameObject source, HatDefinition existing = null, string name = null,
            bool? unlocked = null, CosmeticFit? fit = null)
        {
            var definition = existing ? existing : Find(source);
            if (!definition)
            {
                definition = CreateInstance<HatDefinition>();
                definition.Id = new HatId(BitConverter.ToUInt64(Guid.NewGuid().ToByteArray(), 0));
                Folder("Assets/Game/Settings/Cosmetics/Hats");
                AssetDatabase.CreateAsset(definition, $"Assets/Game/Settings/Cosmetics/Hats/{definition.Id}.asset");
                definition.DisplayName = source.name;
            }
            definition.Source = source;
            if (name != null) definition.DisplayName = name;
            if (unlocked.HasValue) definition.UnlockedByDefault = unlocked.Value;
            if (fit.HasValue) definition.Fit = fit.Value;
            string folder = $"Assets/Game/Generated/Cosmetics/Hats/{definition.Id}";
            Folder(folder);
            var scene = EditorSceneManager.NewPreviewScene();
            var root = new GameObject(definition.Id.ToString());
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(root, scene);
            try
            {
                var visual = (GameObject)PrefabUtility.InstantiatePrefab(source, scene);
                PrefabUtility.UnpackPrefabInstance(visual, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
                visual.transform.SetParent(root.transform, false);
                foreach (var script in root.GetComponentsInChildren<MonoBehaviour>(true)) DestroyImmediate(script);
                foreach (var collider in root.GetComponentsInChildren<Collider>(true)) DestroyImmediate(collider);
                foreach (var body in root.GetComponentsInChildren<Rigidbody>(true)) DestroyImmediate(body);
                foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
                {
                    var materials = renderer.sharedMaterials;
                    for (int i = 0; i < materials.Length; i++)
                    {
                        var original = materials[i];
                        if (!original || original.shader.name.StartsWith("Universal Render Pipeline/")) continue;
                        AssetDatabase.TryGetGUIDAndLocalFileIdentifier(original, out string guid, out long fileId);
                        string path = $"{folder}/{guid}-{fileId}.mat";
                        var material = AssetDatabase.LoadAssetAtPath<Material>(path);
                        if (!material) { material = new Material(Shader.Find("Universal Render Pipeline/Lit")); AssetDatabase.CreateAsset(material, path); }
                        material.CopyPropertiesFromMaterial(original);
                        material.shader = Shader.Find("Universal Render Pipeline/Lit");
                        var texture = original.HasProperty("_BaseMap") ? original.GetTexture("_BaseMap") : original.mainTexture;
                        var color = original.HasProperty("_BaseColor") ? original.GetColor("_BaseColor") : original.HasProperty("_Color") ? original.color : Color.white;
                        material.SetTexture("_BaseMap", texture); material.SetColor("_BaseColor", color);
                        if (original.HasProperty("_Glossiness")) material.SetFloat("_Smoothness", original.GetFloat("_Glossiness"));
                        if (original.HasProperty("_MainTex"))
                        { material.SetTextureScale("_BaseMap", original.GetTextureScale("_MainTex")); material.SetTextureOffset("_BaseMap", original.GetTextureOffset("_MainTex")); }
                        EditorUtility.SetDirty(material); materials[i] = material;
                    }
                    renderer.sharedMaterials = materials;
                }
                definition.Visual = PrefabUtility.SaveAsPrefabAsset(root, $"{folder}/{definition.Id}.prefab");
            }
            finally { DestroyImmediate(root); EditorSceneManager.ClosePreviewScene(scene); }
            var catalog = AssetDatabase.LoadAssetAtPath<HatCatalog>(CatalogPath);
            if (!catalog)
            { Folder("Assets/Game/Settings/Cosmetics"); catalog = CreateInstance<HatCatalog>(); AssetDatabase.CreateAsset(catalog, CatalogPath); }
            if (!catalog.Entries.Contains(definition)) catalog.Entries.Add(definition);
            EditorUtility.SetDirty(catalog); Capture(definition); AssetDatabase.SaveAssets(); return definition;
        }
        public static void Capture(HatDefinition definition)
        {
            definition.Icon = IconCaptureWindow.CaptureAndSave(definition.Visual, 256,
                $"Assets/Game/UI/Icons/Hats/{definition.Id}", Color.clear, 0.15f, new Vector2(15, 25));
            EditorUtility.SetDirty(definition); AssetDatabase.SaveAssetIfDirty(definition);
        }
        public static void CaptureAvatar(AvatarRegistry.Entry entry)
        {
            entry.Settings.Icon = IconCaptureWindow.CaptureAndSave(entry.Prefab, 256,
                $"Assets/Game/UI/Icons/Avatars/{entry.Id}", Color.clear, 0.15f, new Vector2(5, 180));
            EditorUtility.SetDirty(entry.Settings); AssetDatabase.SaveAssetIfDirty(entry.Settings);
        }
        internal static void Folder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            string parent = Path.GetDirectoryName(path).Replace('\\', '/');
            Folder(parent); AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
        }
    }
}
