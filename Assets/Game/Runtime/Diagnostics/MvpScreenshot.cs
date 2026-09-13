#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.UIElements;

namespace TwoBirds
{
    public sealed class MvpScreenshot : MonoBehaviour
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Initialize()
        {
            if (MvpValidation.Argument("-mvpCapture") == null) return;
            var go = new GameObject("Validation screenshot");
            DontDestroyOnLoad(go);
            go.AddComponent<MvpScreenshot>();
        }

        private IEnumerator Start()
        {
            yield return new WaitForSecondsRealtime(3f);
            string page = MvpValidation.Argument("-mvpPage");
            if (page != null)
            {
                var document = FindAnyObjectByType<UIDocument>();
                var button = document.rootVisualElement.Q<Button>(page);
                button.Focus();
                using var submit = NavigationSubmitEvent.GetPooled();
                button.SendEvent(submit);
            }
            yield return new WaitForSecondsRealtime(3f);
            // Hidden validation windows have no capturable swap-chain buffer. Render the camera
            // and UI panel to textures instead, then composite their actual rendered pixels.
            var sceneTexture = new RenderTexture(1280, 720, 24);
            var uiTexture = new RenderTexture(1280, 720, 0);
            sceneTexture.Create();
            uiTexture.Create();
            var cameras = Camera.allCameras;
            foreach (var camera in cameras) camera.targetTexture = sceneTexture;
            var documents = FindObjectsByType<UIDocument>();
            bool previousClear = documents.Length > 0 && documents[0].panelSettings.clearColor;
            foreach (var document in documents)
            {
                document.panelSettings.clearColor = true;
                document.panelSettings.targetTexture = uiTexture;
            }
            yield return new WaitForSecondsRealtime(1f);
            foreach (var camera in cameras)
                RenderPipeline.SubmitRenderRequest(camera, new UniversalRenderPipeline.SingleCameraRequest { destination = sceneTexture });
            var scene = Read(sceneTexture);
            var ui = Read(uiTexture);
            string capturePath = MvpValidation.Argument("-mvpCapture");
            File.WriteAllBytes(Path.ChangeExtension(capturePath, "scene.png"), scene.EncodeToPNG());
            File.WriteAllBytes(Path.ChangeExtension(capturePath, "ui.png"), ui.EncodeToPNG());
            var scenePixels = scene.GetPixels();
            var uiPixels = ui.GetPixels();
            for (int i = 0; i < scenePixels.Length; i++)
            {
                Color foreground = uiPixels[i];
                // UI Toolkit renders premultiplied alpha into a target texture.
                scenePixels[i] = foreground + scenePixels[i] * (1f - foreground.a);
                scenePixels[i].a = 1f;
            }
            scene.SetPixels(scenePixels);
            scene.Apply();
            File.WriteAllBytes(MvpValidation.Argument("-mvpCapture"), scene.EncodeToPNG());
            foreach (var camera in cameras) if (camera != null) camera.targetTexture = null;
            foreach (var document in documents)
                if (document != null) { document.panelSettings.targetTexture = null; document.panelSettings.clearColor = previousClear; }
            Destroy(scene);
            Destroy(ui);
            sceneTexture.Release();
            uiTexture.Release();
            Destroy(sceneTexture);
            Destroy(uiTexture);
            if (MvpValidation.Argument("-mvpMode") == null) Application.Quit();
            Destroy(gameObject);
        }

        private static Texture2D Read(RenderTexture texture)
        {
            var previous = RenderTexture.active;
            RenderTexture.active = texture;
            var result = new Texture2D(texture.width, texture.height, TextureFormat.RGBA32, false);
            result.ReadPixels(new Rect(0, 0, texture.width, texture.height), 0, 0);
            result.Apply();
            RenderTexture.active = previous;
            return result;
        }
    }
}
#endif
