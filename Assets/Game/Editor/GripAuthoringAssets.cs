#if UNITY_INCLUDE_INSTRUMENTATION
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace TwoBirds.Editor
{
    public static class GripAuthoringAssets
    {
        public const string Folder = "Assets/Art/Animations/HeldPoses";
        private static readonly Dictionary<Object, Object> snapshots = new();
        private static readonly HashSet<Object> touched = new();
        private static readonly List<string> created = new();
        public static IReadOnlyCollection<Object> Unsaved => touched;
        public static event Action Changed;

        public static void Bake(GripPoseCapture capture)
        {
            var clip = Slot(capture.Class, capture.FirstPerson, capture.Charged, "Bake held pose");
            var bindings = new List<EditorCurveBinding>();
            var curves = new List<AnimationCurve>();
            for (int i = 0; i < HumanTrait.MuscleCount; i++)
                if ((HumanBodyBones)HumanTrait.BoneFromMuscle(i) is HumanBodyBones.LeftShoulder or HumanBodyBones.RightShoulder or
                    HumanBodyBones.LeftUpperArm or HumanBodyBones.RightUpperArm or HumanBodyBones.LeftLowerArm or
                    HumanBodyBones.RightLowerArm or HumanBodyBones.LeftHand or HumanBodyBones.RightHand)
                {
                    bindings.Add(EditorCurveBinding.FloatCurve("", typeof(Animator), HumanTrait.MuscleName[i]));
                    curves.Add(new AnimationCurve(new Keyframe(0f, capture.Muscles[i])));
                }
            AnimationUtility.SetEditorCurves(clip, bindings.ToArray(), curves.ToArray());
            Finish(capture.Class, capture.FirstPerson, capture.Charged, clip, capture.Spread);
        }

        public static void CopyHoldToCharged(HoldClass owner, bool firstPerson)
        {
            var view = owner.View(firstPerson);
            if (!view.Hold) return;
            var hold = view.Hold;
            var clip = Slot(owner, firstPerson, true, "Copy hold pose to charged");
            clip.ClearCurves();
            var bindings = AnimationUtility.GetCurveBindings(hold);
            AnimationUtility.SetEditorCurves(clip, bindings, bindings.Select(binding => AnimationUtility.GetEditorCurve(hold, binding)).ToArray());
            Finish(owner, firstPerson, true, clip, view.HoldSpread);
        }

        public static void Clear(HoldClass owner, bool firstPerson, bool charged)
        {
            Touch(owner);
            Undo.RecordObject(owner, "Clear held pose");
            Assign(owner, firstPerson, charged, null, 0f);
            owner.NotifyContentChanged();
        }

        private static AnimationClip Slot(HoldClass owner, bool firstPerson, bool charged, string undo)
        {
            var view = owner.View(firstPerson);
            var clip = charged ? view.Charged : view.Hold;
            Touch(owner);
            Undo.RegisterCompleteObjectUndo(owner, undo);
            if (clip && AssetDatabase.GetAssetPath(clip).StartsWith(Folder + "/"))
            {
                Touch(clip);
                Undo.RegisterCompleteObjectUndo(clip, undo);
                return clip;
            }
            if (!AssetDatabase.IsValidFolder(Folder)) AssetDatabase.CreateFolder("Assets/Art/Animations", "HeldPoses");
            clip = new AnimationClip { name = $"{owner.name}_{(firstPerson ? "FP" : "TP")}_{(charged ? "Charged" : "Hold")}" };
            string path = AssetDatabase.GenerateUniqueAssetPath($"{Folder}/{clip.name}.anim");
            AssetDatabase.CreateAsset(clip, path);
            created.Add(path);
            touched.Add(clip);
            return clip;
        }

        private static void Finish(HoldClass owner, bool firstPerson, bool charged, AnimationClip clip, float spread)
        {
            Assign(owner, firstPerson, charged, clip, owner.Mode == ItemHoldMode.TwoHand ? spread : 0f);
            EditorUtility.SetDirty(clip);
            owner.NotifyContentChanged();
            Changed?.Invoke();
        }

        private static void Assign(HoldClass owner, bool firstPerson, bool charged, AnimationClip clip, float spread)
        {
            var view = owner.View(firstPerson);
            if (charged) { view.Charged = clip; view.ChargedSpread = spread; }
            else { view.Hold = clip; view.HoldSpread = spread; }
            if (firstPerson) owner.FirstPerson = view; else owner.ThirdPerson = view;
            EditorUtility.SetDirty(owner);
        }

        private static void Snapshot(Object asset)
        {
            if (!asset || snapshots.ContainsKey(asset)) return;
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
                    case HoldClass owner: owner.NotifyContentChanged(); break;
                    case ItemDefinition item: item.NotifyContentChanged(); break;
                    case AvatarSettings settings: settings.NotifyContentChanged(); break;
                }
        }

        public static void SaveAll()
        {
            foreach (var asset in touched) if (asset) AssetDatabase.SaveAssetIfDirty(asset);
            Reset();
        }

        public static void RevertAll()
        {
            foreach (var asset in touched)
                if (asset && snapshots.TryGetValue(asset, out var snapshot))
                {
                    EditorUtility.CopySerialized(snapshot, asset);
                    AssetDatabase.SaveAssetIfDirty(asset);
                }
            foreach (string path in created) AssetDatabase.DeleteAsset(path);
            Notify();
            Reset();
        }

        private static void Reset()
        {
            foreach (var snapshot in snapshots.Values) if (snapshot) Object.DestroyImmediate(snapshot);
            snapshots.Clear(); touched.Clear(); created.Clear();
            Changed?.Invoke();
        }
    }
}
#endif
