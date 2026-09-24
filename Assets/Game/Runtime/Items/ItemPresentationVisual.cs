using System.Collections.Generic;
using UnityEngine;

namespace TwoBirds
{
    internal static class ItemPresentationVisual
    {
        internal static GameObject Create(ItemDefinition definition, Transform parent, int layer)
        {
            var source = definition.WorldPrefab;
            var map = new Dictionary<Transform, Transform>();
            var root = new GameObject(source.name + " presentation");
            root.SetActive(false);
            root.transform.SetParent(parent, false);
            Copy(source.transform, root.transform);
            void Copy(Transform from, Transform to)
            {
                map.Add(from, to); to.gameObject.layer = layer;
                to.localPosition = from.localPosition; to.localRotation = from.localRotation; to.localScale = from.localScale;
                if (from.TryGetComponent<MeshFilter>(out var mesh)) to.gameObject.AddComponent<MeshFilter>().sharedMesh = mesh.sharedMesh;
                if (from.TryGetComponent<MeshRenderer>(out var renderer)) CopyRenderer(renderer, to.gameObject.AddComponent<MeshRenderer>());
                if (from.TryGetComponent<LineRenderer>(out var line))
                {
                    var copy = to.gameObject.AddComponent<LineRenderer>(); CopyRenderer(line, copy);
                    copy.useWorldSpace = line.useWorldSpace; copy.widthMultiplier = line.widthMultiplier; copy.widthCurve = line.widthCurve;
                    copy.colorGradient = line.colorGradient; copy.numCapVertices = line.numCapVertices; copy.numCornerVertices = line.numCornerVertices;
                    copy.alignment = line.alignment; copy.textureMode = line.textureMode; copy.positionCount = line.positionCount;
                    var positions = new Vector3[line.positionCount]; line.GetPositions(positions); copy.SetPositions(positions);
                }
                foreach (Transform child in from)
                {
                    var next = new GameObject(child.name); next.transform.SetParent(to, false);
                    Copy(child, next.transform); next.SetActive(child.gameObject.activeSelf);
                }
            }
            if (source.TryGetComponent<SlingshotPresentation>(out var sling))
            {
                var copy = root.AddComponent<SlingshotPresentation>();
                copy.LeftFork = map[sling.LeftFork]; copy.RightFork = map[sling.RightFork];
                copy.RestCenter = map[sling.RestCenter]; copy.LoadedPebble = map[sling.LoadedPebble];
                copy.LeftBand = map[sling.LeftBand.transform].GetComponent<LineRenderer>();
                copy.RightBand = map[sling.RightBand.transform].GetComponent<LineRenderer>();
            }
            if (source.TryGetComponent<PotionPresentation>(out var potion)) potion.ApplyVisual(root.transform.Find("VisualRoot").gameObject, definition);
            root.SetActive(true);
            return root;
        }
        private static void CopyRenderer(Renderer from, Renderer to)
        {
            to.sharedMaterials = from.sharedMaterials; to.enabled = from.enabled;
            to.shadowCastingMode = from.shadowCastingMode; to.receiveShadows = from.receiveShadows;
            to.lightProbeUsage = from.lightProbeUsage; to.reflectionProbeUsage = from.reflectionProbeUsage;
        }
    }
}
