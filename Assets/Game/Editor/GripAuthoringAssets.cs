#if UNITY_INCLUDE_INSTRUMENTATION
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace TwoBirds.Editor
{
    public static class GripAuthoringAssets
    {
        private static readonly Dictionary<Object, Object> snapshots = new();
        private static readonly Dictionary<Object, string> componentSnapshots = new();
        private static readonly HashSet<Object> touched = new();
        private static readonly List<(AvatarHandContact asset, AvatarHandContact live)> pairs = new();
        public static IReadOnlyCollection<Object> Unsaved => touched;
        public static event Action Changed;

        public static void Edit(Object asset, string undo, Action change)
        {
            Touch(asset);
            Undo.RecordObject(asset, undo);
            change();
            EditorUtility.SetDirty(asset);
            switch (asset)
            {
                case HoldSlot slot: slot.NotifyContentChanged(); break;
                case ItemDefinition item: item.NotifyContentChanged(); break;
            }
        }

        public static void Pair(AvatarHandContact asset, AvatarHandContact live)
        {
            if (!pairs.Contains((asset, live))) pairs.Add((asset, live));
        }

        public static void SyncPairs()
        {
            foreach (var (asset, live) in pairs) if (asset && live) live.CopyPoses(asset);
        }

        private static void Snapshot(Object asset)
        {
            if (!asset || snapshots.ContainsKey(asset) || componentSnapshots.ContainsKey(asset)) return;
            if (asset is Component component) { componentSnapshots.Add(asset, EditorJsonUtility.ToJson(component)); return; }
            var copy = Object.Instantiate(asset);
            copy.name = asset.name; copy.hideFlags = HideFlags.HideAndDontSave;
            snapshots.Add(asset, copy);
        }

        public static void Touch(Object asset)
        {
            Snapshot(asset);
            EditorUtility.SetDirty(asset);
            if (touched.Add(asset)) Changed?.Invoke();
        }

        private static void Notify()
        {
            foreach (var asset in touched)
                switch (asset)
                {
                    case HoldSlot slot: slot.NotifyContentChanged(); break;
                    case ItemDefinition item: item.NotifyContentChanged(); break;
                    case AvatarSettings settings: settings.NotifyContentChanged(); break;
                }
        }

        private static void Persist(Object asset)
        {
            if (asset is Component component) PrefabUtility.SavePrefabAsset(component.transform.root.gameObject);
            else AssetDatabase.SaveAssetIfDirty(asset);
        }

        public static void SaveAll()
        {
            foreach (var asset in touched) if (asset) Persist(asset);
            Reset();
        }

        public static void RevertAll()
        {
            foreach (var asset in touched)
            {
                if (!asset) continue;
                if (asset is Component component && componentSnapshots.TryGetValue(asset, out var json))
                    EditorJsonUtility.FromJsonOverwrite(json, component);
                else if (snapshots.TryGetValue(asset, out var snapshot)) EditorUtility.CopySerialized(snapshot, asset);
                else continue;
                Persist(asset);
            }
            SyncPairs();
            Notify();
            Reset();
        }

        private static void Reset()
        {
            foreach (var snapshot in snapshots.Values) if (snapshot) Object.DestroyImmediate(snapshot);
            snapshots.Clear(); componentSnapshots.Clear(); touched.Clear(); pairs.Clear();
            Changed?.Invoke();
        }
    }
}
#endif
