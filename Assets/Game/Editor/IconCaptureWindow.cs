using System.IO;
using FishNet.Component.Prediction;
using UnityEditor;
using UnityEngine;

namespace TwoBirds.Editor
{
    public sealed class IconCaptureWindow : EditorWindow
    {
        private GameObject target;
        private int resolution = 128;
        private string savePath = "Assets/Game/UI/Icons";
        private Color backgroundColor = new(0.15f, 0.15f, 0.15f, 0f);
        private float padding = 0.1f;
        private Texture2D preview;
        private Vector2 cameraAngle = new(25f, -135f);

        [MenuItem("Two Birds/Icon Capture")]
        public static void ShowWindow()
        {
            GetWindow<IconCaptureWindow>("Icon Capture");
        }

        private void OnGUI()
        {
            EditorGUILayout.Space(4);
            EditorGUI.BeginChangeCheck();
            target = (GameObject)EditorGUILayout.ObjectField("Prefab / GameObject", target, typeof(GameObject), true);
            if (EditorGUI.EndChangeCheck())
                RefreshPreview();

            resolution = EditorGUILayout.IntPopup("Resolution", resolution,
                new[] { "64", "128", "256", "512", "1024" },
                new[] { 64, 128, 256, 512, 1024 });

            backgroundColor = EditorGUILayout.ColorField(
                new GUIContent("Background"), backgroundColor, true, true, false);

            padding = EditorGUILayout.Slider("Padding", padding, 0f, 0.5f);
            cameraAngle = EditorGUILayout.Vector2Field("Camera Angle (Pitch, Yaw)", cameraAngle);

            EditorGUILayout.Space(4);

            if (GUILayout.Button("Refresh Preview") && target != null)
                RefreshPreview();

            if (preview != null)
            {
                EditorGUILayout.Space(4);
                float size = Mathf.Min(position.width - 20, 256);
                var rect = GUILayoutUtility.GetRect(size, size, GUILayout.ExpandWidth(false));
                rect.x = (position.width - size) * 0.5f;
                EditorGUI.DrawTextureTransparent(rect, preview, ScaleMode.ScaleToFit);
            }

            EditorGUILayout.Space(4);
            savePath = EditorGUILayout.TextField("Save Folder", savePath);

            EditorGUI.BeginDisabledGroup(target == null);
            if (GUILayout.Button("Capture & Save PNG", GUILayout.Height(28)))
                CaptureAndSave();
            EditorGUI.EndDisabledGroup();
        }

        private void RefreshPreview()
        {
            if (preview != null)
                DestroyImmediate(preview);
            if (target != null)
                preview = Render(target, resolution);
            Repaint();
        }

        private Texture2D Render(GameObject source, int res) =>
            Render(source, res, backgroundColor, padding, cameraAngle);

        private static Texture2D Render(GameObject source, int res, Color backgroundColor,
            float padding, Vector2 cameraAngle)
        {
            var scene = EditorSceneManager_Utility.NewPreviewScene();
            var instance = Instantiate(source, Vector3.zero, Quaternion.identity);
            UnityEditor.SceneManagement.EditorSceneManager.MoveGameObjectToScene(instance, scene);

            StripNonVisual(instance);

            var bounds = CalculateBounds(instance);
            if (bounds.size == Vector3.zero)
            {
                DestroyImmediate(instance);
                UnityEditor.SceneManagement.EditorSceneManager.ClosePreviewScene(scene);
                return null;
            }

            var camGo = new GameObject("CaptureCamera");
            UnityEditor.SceneManagement.EditorSceneManager.MoveGameObjectToScene(camGo, scene);
            var cam = camGo.AddComponent<Camera>();
            cam.scene = scene;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = backgroundColor;
            cam.orthographic = true;
            cam.nearClipPlane = 0.01f;
            cam.farClipPlane = 100f;

            float maxExtent = Mathf.Max(bounds.extents.x, bounds.extents.y, bounds.extents.z);
            cam.orthographicSize = maxExtent * (1f + padding);

            var rotation = Quaternion.Euler(cameraAngle.x, cameraAngle.y, 0f);
            cam.transform.position = bounds.center - rotation * Vector3.forward * (maxExtent * 4f);
            cam.transform.rotation = rotation;

            var lightGo = new GameObject("CaptureLight");
            UnityEditor.SceneManagement.EditorSceneManager.MoveGameObjectToScene(lightGo, scene);
            var light = lightGo.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.2f;
            light.transform.rotation = Quaternion.Euler(50f, cameraAngle.y + 30f, 0f);

            var rt = RenderTexture.GetTemporary(res, res, 24, RenderTextureFormat.ARGB32);
            cam.targetTexture = rt;
            cam.Render();

            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(res, res, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0, 0, res, res), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;

            cam.targetTexture = null;
            RenderTexture.ReleaseTemporary(rt);
            DestroyImmediate(instance);
            DestroyImmediate(camGo);
            DestroyImmediate(lightGo);
            UnityEditor.SceneManagement.EditorSceneManager.ClosePreviewScene(scene);

            return tex;
        }

        private void CaptureAndSave()
        {
            var sprite = CaptureAndSave(target, resolution, savePath, backgroundColor, padding, cameraAngle);
            if (sprite == null) return;

            if (preview != null)
                DestroyImmediate(preview);
            preview = Render(target, resolution);

            EditorGUIUtility.PingObject(sprite);
        }

        public static Sprite CaptureAndSave(GameObject target, int resolution, string savePath,
            Color backgroundColor, float padding, Vector2 cameraAngle, ItemDefinition definition = null)
        {
            if (target == null) return null;

            var tex = Render(target, resolution, backgroundColor, padding, cameraAngle);
            if (tex == null)
            {
                Debug.LogWarning("Icon Capture: no renderable geometry found on target.");
                return null;
            }

            if (!Directory.Exists(savePath)) Directory.CreateDirectory(savePath);
            string fullPath = Path.Combine(savePath, $"{target.name}_icon.png").Replace('\\', '/');
            File.WriteAllBytes(fullPath, tex.EncodeToPNG());
            DestroyImmediate(tex);

            AssetDatabase.Refresh();
            var importer = AssetImporter.GetAtPath(fullPath) as TextureImporter;
            if (importer != null)
            {
                importer.textureType = TextureImporterType.Sprite;
                importer.spriteImportMode = SpriteImportMode.Single;
                importer.mipmapEnabled = false;
                importer.alphaIsTransparency = true;
                importer.maxTextureSize = resolution;
                importer.SaveAndReimport();
            }

            var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(fullPath);
            if (definition != null && sprite != null)
            {
                Undo.RecordObject(definition, "Assign Item Icon");
                definition.Icon = sprite;
                EditorUtility.SetDirty(definition);
            }
            Debug.Log($"Icon saved: {fullPath}");
            return sprite;
        }

        private static void StripNonVisual(GameObject go)
        {
            DestroyComponents<ItemUseBehaviour>(go);
            DestroyComponents<WorldItem>(go);
            DestroyComponents<OfflineRigidbody>(go);
            DestroyComponents<BakedPickup>(go);

            foreach (var c in go.GetComponentsInChildren<Component>(true))
            {
                if (c == null) continue;
                if (c is Transform or MeshFilter or MeshRenderer or SkinnedMeshRenderer) continue;
                DestroyImmediate(c);
            }
        }

        private static void DestroyComponents<T>(GameObject go) where T : Component
        {
            foreach (var component in go.GetComponentsInChildren<T>(true))
                if (component) DestroyImmediate(component);
        }

        private static Bounds CalculateBounds(GameObject go)
        {
            var renderers = go.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length == 0) return new Bounds();

            var bounds = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++)
                bounds.Encapsulate(renderers[i].bounds);
            return bounds;
        }

        private void OnDestroy()
        {
            if (preview != null)
                DestroyImmediate(preview);
        }
    }

    static class EditorSceneManager_Utility
    {
        public static UnityEngine.SceneManagement.Scene NewPreviewScene()
        {
            return UnityEditor.SceneManagement.EditorSceneManager.NewPreviewScene();
        }
    }
}
