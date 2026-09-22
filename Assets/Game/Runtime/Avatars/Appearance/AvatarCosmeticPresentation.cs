using System;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace TwoBirds
{
    public sealed class AvatarCosmeticPresentation : IDisposable
    {
        private readonly AvatarBinding binding;
        private readonly HatCatalog hats;
        private readonly TattooCatalog tattoos;
        private readonly bool firstPerson;
        private readonly TattooRegion[] regions;
        private GameObject hat;
        private DecalProjector[] projectors = Array.Empty<DecalProjector>();
        private Material[] materials = Array.Empty<Material>();
        private AvatarAppearance appearance;
        public AvatarCosmeticPresentation(AvatarBinding binding, HatCatalog hats, TattooCatalog tattoos, bool firstPerson = false)
        {
            this.binding = binding; this.hats = hats; this.tattoos = tattoos; this.firstPerson = firstPerson;
            regions = firstPerson ? AvatarTattooPlacement.FirstPersonRegions(binding.Settings) : binding.Settings.TattooRegions;
        }

        public void Apply(AvatarAppearance value)
        {
            if (value == null || value.Avatar != binding.Id || value.Equals(appearance)) return;
            bool rebuild = appearance == null || appearance.Hat != value.Hat || appearance.Tattoos.Length != value.Tattoos.Length;
            if (!rebuild)
                for (int i = 0; i < value.Tattoos.Length; i++)
                    if (value.Tattoos[i].Design != appearance.Tattoos[i].Design) { rebuild = true; break; }
            if (rebuild)
            {
                Dispose();
                if (!firstPerson && hats && hats.TryResolve(value.Hat, out var definition) && definition.Visual)
                {
                    var head = binding.GetBone(HumanBodyBones.Head);
                    if (head)
                    {
                        hat = new GameObject("Hat fit"); hat.transform.SetParent(head, false);
                        binding.Settings.HeadFit.Apply(hat.transform);
                        var visual = UnityEngine.Object.Instantiate(definition.Visual, hat.transform);
                        definition.Fit.Apply(visual.transform);
                        SetLayer(hat, binding.Animator.gameObject.layer);
                    }
                }
                projectors = new DecalProjector[value.Tattoos.Length]; materials = new Material[projectors.Length];
            }
            for (int i = 0; i < value.Tattoos.Length; i++)
                if (rebuild || !value.Tattoos[i].Equals(appearance.Tattoos[i])) UpdateTattoo(i, value.Tattoos[i]);
            appearance = value.Clone();
        }
        private void UpdateTattoo(int index, TattooAppearance value)
        {
            if (!tattoos || !tattoos.DecalMaterial || !tattoos.TryResolve(value.Design, out var definition) ||
                !AvatarTattooPlacement.Resolve(regions, value, out var region, out var resolved, firstPerson ? 0.0125f : 0.35f))
            { if (projectors[index]) projectors[index].gameObject.SetActive(false); return; }
            var bone = region.Attachment(binding);
            if (!bone) return;
            var projector = projectors[index];
            if (!projector)
            {
                var node = new GameObject("Tattoo"); node.SetActive(false); node.layer = binding.Animator.gameObject.layer;
                materials[index] = new Material(tattoos.DecalMaterial);
                materials[index].SetTexture("_Artwork", definition.Artwork);
                materials[index].SetColor("_Ink", value.Ink);
                materials[index].SetFloat("_DrawOrder", index);
                projector = node.AddComponent<DecalProjector>(); projectors[index] = projector;
                projector.material = materials[index];
                projector.scaleMode = DecalScaleMode.InheritFromHierarchy;
                projector.startAngleFade = 50f; projector.endAngleFade = 80f;
            }
            projector.transform.SetParent(bone, false);
            AvatarTattooPlacement.Tangents(resolved.Normal, out _, out var up);
            var normal = region.Frame * resolved.Normal;
            projector.transform.localPosition = region.Denormalize(resolved.Position);
            projector.transform.localRotation = Quaternion.LookRotation(-normal, region.Frame * up) *
                Quaternion.AngleAxis(value.Rotation, Vector3.forward);
            projector.transform.localScale = Vector3.one;
            float size = value.Size * AvatarTattooPlacement.PlaneSize(region, resolved.Normal);
            float aspect = Mathf.Sqrt(definition.AspectRatio);
            projector.size = new Vector3(size * aspect, size / aspect, Mathf.Max(0.003f, region.Dimensions.magnitude * 0.025f));
            projector.pivot = Vector3.zero;
            materials[index].SetColor("_Ink", value.Ink);
            projector.gameObject.SetActive(true);
        }
        internal static void SetLayer(GameObject root, int layer)
        { foreach (var node in root.GetComponentsInChildren<Transform>(true)) node.gameObject.layer = layer; }
        public void Dispose()
        {
            if (hat) UnityEngine.Object.Destroy(hat);
            foreach (var projector in projectors) if (projector) { projector.gameObject.SetActive(false); UnityEngine.Object.Destroy(projector.gameObject); }
            foreach (var material in materials) if (material) UnityEngine.Object.Destroy(material);
            hat = null; appearance = null; projectors = Array.Empty<DecalProjector>(); materials = Array.Empty<Material>();
        }
    }
}
