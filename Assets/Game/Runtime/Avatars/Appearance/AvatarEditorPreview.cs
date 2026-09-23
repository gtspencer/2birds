using System;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

namespace TwoBirds
{
    public sealed class AvatarEditorPreview : MonoBehaviour
    {
        public const string LayerName = "AvatarPreview";
        [SerializeField] private AvatarPresentation presentation;
        [SerializeField] private Camera previewCamera;
        private SessionController session;
        private AvatarCosmeticPresentation cosmetics;
        private AvatarAppearance requested;
        private AvatarInstance instance;
        private AvatarTattooSurface surface;
        private RenderTexture texture;
        private Vector3 target;
        private float yaw, pitch = 8f, distance = 3f;
        private bool placement;
        public Camera Camera => previewCamera;
        public RenderTexture Texture => texture;
        public event Action<AvatarId> Accepted;
        internal AvatarBinding Binding => presentation.Binding;
        internal void Initialize(SessionController value)
        {
            session = value;
            presentation.EditorPreview = true;
            presentation.Configure(session.Avatars, false);
            presentation.SetFeatures(true, false, true, false);
            presentation.DidBind += Bound; presentation.WillUnbind += Unbound;
            previewCamera.GetUniversalAdditionalCameraData().SetRenderer(0);
            previewCamera.cullingMask = 1 << LayerMask.NameToLayer(LayerName);
            texture = new RenderTexture(1024, 1024, 24) { name = "Avatar editor preview" }; texture.Create();
            previewCamera.targetTexture = texture; previewCamera.enabled = false;
            SceneManager.sceneLoaded += SceneLoaded;
            ExcludeWorldCameras();
        }
        private void SceneLoaded(Scene scene, LoadSceneMode mode) => ExcludeWorldCameras();
        private void ExcludeWorldCameras()
        {
            int mask = ~(1 << LayerMask.NameToLayer(LayerName));
            foreach (var camera in FindObjectsByType<Camera>(FindObjectsSortMode.None))
                if (camera != previewCamera) camera.cullingMask &= mask;
        }
        public void Show(AvatarAppearance value)
        {
            requested = value.Clone();
            if (session.Avatars.TryResolve(value.Avatar, out var entry))
                presentation.EditorLookTarget = transform.position + Vector3.up * entry.Settings.VisualHeight +
                    transform.forward * 3f;
            presentation.SetInput(new AvatarPresentationInput { Facing = new Pose(transform.position, transform.rotation),
                SolePosition = transform.position, Grounded = true, WalkSpeed = 1, SprintSpeed = 2 });
            presentation.SetVisual(true); previewCamera.enabled = true;
            if (presentation.Binding?.Id == value.Avatar) { SetAppearance(value); return; }
            presentation.RequestAvatar(value.Avatar);
        }
        private void Bound(AvatarBinding binding)
        {
            instance = binding.Animator.GetComponent<AvatarInstance>();
            AvatarCosmeticPresentation.SetLayer(binding.Animator.gameObject, LayerMask.NameToLayer(LayerName));
            foreach (var collider in binding.Animator.GetComponentsInChildren<Collider>(true)) collider.enabled = false;
            cosmetics = new AvatarCosmeticPresentation(binding, session.Hats, session.Tattoos);
            cosmetics.Apply(requested);
            if (placement) PreparePlacement();
            ResetView(); Accepted?.Invoke(binding.Id);
        }
        private void Unbound(AvatarBinding binding) { cosmetics?.Dispose(); cosmetics = null; instance = null; surface = null; }
        public void SetAppearance(AvatarAppearance value) { requested = value.Clone(); cosmetics?.Apply(value); }
        public void SetPlacement(bool value, int tattooIndex = -1)
        {
            if (placement == value) return;
            bool preserveFraming = TattooPoint(tattooIndex, out var previousPoint);
            placement = value;
            if (!value) surface = null;
            presentation.SetFeatures(!value, false, !value, false);
            if (value) PreparePlacement();
            else if (instance) instance.Evaluate(0f);
            if (preserveFraming && TattooPoint(tattooIndex, out var point))
            {
                target += point - previousPoint;
                UpdateCamera();
            }
        }
        private void PreparePlacement()
        {
            if (!instance) return;
            instance.Evaluate(0f);
            surface = new AvatarTattooSurface(instance.Binding);
        }
        public void Look(Vector2 uv)
        {
            if (placement || Binding == null) return;
            var ray = previewCamera.ViewportPointToRay(uv);
            presentation.EditorLookTarget = ray.GetPoint(distance * 0.45f);
        }
        public void Orbit(Vector2 delta)
        { yaw += delta.x; pitch = Mathf.Clamp(pitch + delta.y, -35, 45); UpdateCamera(); }
        public void Zoom(float amount)
        { distance = Mathf.Clamp(distance * Mathf.Exp(-amount), 0.2f, 8f); UpdateCamera(); }
        public void ResetView()
        {
            if (Binding == null) return;
            target = transform.position + Vector3.up * (Binding.Settings.VisualHeight * 0.5f);
            distance = Binding.Settings.VisualHeight * 1.7f; yaw = 0; pitch = 8; UpdateCamera();
        }
        private void UpdateCamera()
        {
            var rotation = Quaternion.Euler(pitch, yaw + 180f, 0);
            previewCamera.transform.SetPositionAndRotation(target - rotation * Vector3.forward * distance, rotation);
        }
        public void Resize(int width, int height)
        {
            width = Mathf.Clamp(width, 64, 2048); height = Mathf.Clamp(height, 64, 2048);
            if (texture.width == width && texture.height == height) return;
            texture.Release(); texture.width = width; texture.height = height; texture.Create();
            previewCamera.aspect = (float)width / height;
        }
        public bool Place(Vector2 uv, TattooAppearance value, out TattooAppearance result)
        {
            result = value;
            return placement && surface != null && surface.Cast(previewCamera.ViewportPointToRay(uv), value, out result);
        }
        public bool CentralTattoo(TattooAppearance value, out TattooAppearance result)
        {
            result = value;
            if (!placement || surface == null || Binding == null) return false;
            foreach (var bone in new[] { HumanBodyBones.Chest, HumanBodyBones.Spine, HumanBodyBones.Hips })
            {
                var region = Array.Find(Binding.Settings.TattooRegions, r => r.Key == AvatarTattooPlacement.Key(bone));
                if (region == null) continue;
                var attachment = region.Attachment(Binding);
                var center = attachment.TransformPoint(region.Denormalize(Vector3.zero));
                var forward = Binding.Animator.transform.forward;
                if (surface.Cast(new Ray(center + forward * Binding.Settings.VisualHeight, -forward), value, out result) &&
                    result.Region == region.Key) return true;
            }
            return false;
        }
        public bool TattooPoint(int index, out Vector3 point)
        {
            point = default;
            if (Binding == null || requested == null || index < 0 || index >= requested.Tattoos.Length ||
                !AvatarTattooPlacement.Resolve(Binding.Settings.TattooRegions, requested.Tattoos[index], out var region, out var resolved)) return false;
            point = region.Attachment(Binding).TransformPoint(region.Denormalize(resolved.Position)); return true;
        }
        public void FocusTattoo(int index) { if (TattooPoint(index, out var point)) { target = point; UpdateCamera(); } }
        public void Hide() { previewCamera.enabled = false; presentation.SetVisual(false); requested = null; }
        private void OnDestroy()
        {
            SceneManager.sceneLoaded -= SceneLoaded;
            cosmetics?.Dispose();
            if (texture) { texture.Release(); Destroy(texture); }
        }
    }
}
