#if UNITY_INCLUDE_INSTRUMENTATION
using System;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace TwoBirds
{
    public sealed class GripComparisonViews : IDisposable
    {
        public static readonly Vector2Int[] Presets = { new(1280, 720), new(1920, 1080), new(2560, 1440), new(1920, 1200), new(3440, 1440) };
        public Camera FirstPerson { get; }
        public Camera Observer { get; }
        public RenderTexture FirstTexture { get; private set; }
        public RenderTexture ObserverTexture { get; private set; }
        public int Width { get; private set; } = 1280;
        public int Height { get; private set; } = 720;
        public event Action Changed;
        private readonly RenderTexture initialTexture;
        private readonly int initialMask;
        private readonly float initialFov, initialAspect;
        private float yaw = 155f, pitch = 10f, distance = 3f;
        private Vector3 target;
        public GripComparisonViews(Camera firstPerson, Camera observer)
        {
            FirstPerson = firstPerson; Observer = observer;
            initialTexture = firstPerson.targetTexture; initialMask = firstPerson.cullingMask;
            initialFov = firstPerson.fieldOfView; initialAspect = firstPerson.aspect;
            observer.CopyFrom(firstPerson); observer.orthographic = false;
            var source = firstPerson.GetUniversalAdditionalCameraData(); var output = observer.GetUniversalAdditionalCameraData();
            output.SetRenderer(0); output.renderPostProcessing = source.renderPostProcessing; output.renderShadows = source.renderShadows;
            output.antialiasing = source.antialiasing; output.antialiasingQuality = source.antialiasingQuality;
            output.requiresColorTexture = source.requiresColorTexture; output.requiresDepthTexture = source.requiresDepthTexture;
            output.volumeLayerMask = source.volumeLayerMask; output.volumeTrigger = source.volumeTrigger; output.dithering = source.dithering; output.stopNaN = source.stopNaN;
            firstPerson.cullingMask &= ~LayerMask.GetMask(AvatarEditorPreview.LayerName);
            observer.cullingMask = (initialMask | LayerMask.GetMask(AvatarEditorPreview.LayerName)) &
                ~LayerMask.GetMask("GripAuthoringOwner", "ItemHeld", "Player", "PlayerItemHitbox");
            observer.enabled = true;
            Resize(Width, Height);
        }
        public void Resize(int width, int height)
        {
            width = Mathf.Clamp(width, 64, SystemInfo.maxTextureSize); height = Mathf.Clamp(height, 64, SystemInfo.maxTextureSize);
            if (FirstTexture && Width == width && Height == height) return;
            Release(FirstTexture); Release(ObserverTexture); Width = width; Height = height;
            FirstTexture = Create("Grip First Person"); ObserverTexture = Create("Grip Observer");
            FirstPerson.targetTexture = FirstTexture; Observer.targetTexture = ObserverTexture;
            FirstPerson.aspect = Observer.aspect = (float)Width / Height;
            Changed?.Invoke();
        }
        private RenderTexture Create(string name)
        {
            var texture = new RenderTexture(Width, Height, 24) { name = name };
            texture.Create(); return texture;
        }
        public void SetFov(float degrees) { FirstPerson.fieldOfView = Observer.fieldOfView = Mathf.Clamp(degrees, 15f, 120f); Changed?.Invoke(); }
        public void Orbit(Vector2 delta) { yaw += delta.x * 0.4f; pitch = Mathf.Clamp(pitch + delta.y * 0.4f, -80f, 80f); Place(); }
        public void Zoom(float delta) { distance = Mathf.Clamp(distance * Mathf.Exp(delta * 0.06f), 0.2f, 12f); Place(); }
        public void Follow(Vector3 center) { target = center; Place(); }
        private void Place()
        {
            var rotation = Quaternion.Euler(pitch, yaw, 0f);
            Observer.transform.SetPositionAndRotation(target - rotation * Vector3.forward * distance, rotation);
        }
        private static void Release(RenderTexture texture)
        { if (texture) { texture.Release(); UnityEngine.Object.Destroy(texture); } }
        public void Dispose()
        {
            if (FirstPerson)
            {
                FirstPerson.targetTexture = initialTexture; FirstPerson.cullingMask = initialMask;
                FirstPerson.fieldOfView = initialFov; FirstPerson.aspect = initialAspect;
            }
            if (Observer) { Observer.targetTexture = null; Observer.enabled = false; }
            Release(FirstTexture); Release(ObserverTexture);
        }
    }
}
#endif
