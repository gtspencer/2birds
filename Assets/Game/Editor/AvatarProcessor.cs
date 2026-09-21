using System;
using System.Collections.Generic;
using System.IO;
using UniGLTF;
using UniVRM10;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace TwoBirds.Editor
{
    public sealed class AvatarProcessor : EditorWindow
    {
        public const string RegistryPath = "Assets/Game/Settings/Avatars/AvatarRegistry.asset";
        public const string AnimationPath = "Assets/Game/Settings/Avatars/AvatarAnimationSet.asset";
        private const string SettingsFolder = "Assets/Game/Settings/Avatars";
        private const string PrefabFolder = "Assets/Game/Prefabs/Avatars";
        private static readonly (string File, int Locomotion)[] ClipFiles =
        {
            ("walking.fbx", 0), ("Walking Backwards.fbx", 1), ("left strafe walking.fbx", 2), ("right strafe walking.fbx", 3),
            ("running.fbx", 4), ("Running Backward.fbx", 5), ("left strafe.fbx", 6), ("right strafe.fbx", 7),
            ("idle.fbx", -1), ("jump.fbx", -1), ("Falling.fbx", -1), ("Seated Idle.fbx", -1)
        };
        private GameObject source;
        private AvatarSettings resolvedSettings;
        private string diagnostics;
        private bool prepareClips;

        [MenuItem("Two Birds/Process Avatar")]
        public static void ShowWindow() => GetWindow<AvatarProcessor>("Process Avatar");

        private void OnGUI()
        {
            var next = (GameObject)EditorGUILayout.ObjectField("Project VRM source", source, typeof(GameObject), false);
            if (next != source)
            {
                source = next; diagnostics = null; resolvedSettings = null;
                if (source)
                    try { resolvedSettings = FindSettings(AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(source))); }
                    catch (Exception exception) { diagnostics = exception.Message; }
            }
            prepareClips = EditorGUILayout.Toggle("Prepare shared clips", prepareClips);
            var settings = resolvedSettings;
            if (settings)
            {
                EditorGUILayout.LabelField("Identity", settings.Id.ToString());
                EditorGUILayout.ObjectField("Settings", settings, typeof(AvatarSettings), false);
                if (GUILayout.Button("Edit Settings / Spring Chains")) Selection.activeObject = settings;
                var registry = AssetDatabase.LoadAssetAtPath<AvatarRegistry>(RegistryPath);
                if (registry && registry.TryResolve(settings.Id, out var entry))
                    EditorGUILayout.ObjectField("Prefab", entry.Prefab, typeof(GameObject), false);
            }
            using (new EditorGUI.DisabledScope(!source || EditorApplication.isPlaying))
            {
                if (GUILayout.Button("Process"))
                {
                    try
                    {
                        var result = Process(AssetDatabase.GetAssetPath(source), prepareClips);
                        resolvedSettings = result.Settings;
                        diagnostics = $"Processed {result.Settings.Id}. {ContentSummary(result.Prefab)}";
                    }
                    catch (Exception exception) { diagnostics = exception.Message; Debug.LogException(exception); }
                }
                if (settings && GUILayout.Button("Assign New Identity") && EditorUtility.DisplayDialog("Assign New Identity",
                    $"Replace identity {settings.Id} for {AssetDatabase.GetAssetPath(source)}? Existing saved/network identities will no longer resolve.", "Assign", "Cancel"))
                {
                    try { AssignNewIdentity(settings); }
                    catch (Exception exception) { diagnostics = exception.Message; }
                }
            }
            if (!string.IsNullOrEmpty(diagnostics)) EditorGUILayout.HelpBox(diagnostics, MessageType.Info);
        }

        public static AvatarRegistry.Entry Process(string sourcePath, bool prepareAnimations = false)
        {
            if (EditorApplication.isPlaying) throw new InvalidOperationException("Process avatars outside Play Mode.");
            string stage = "source import";
            var changes = new ProcessingChanges();
            var scene = EditorSceneManager.NewPreviewScene();
            GameObject root = null;
            AvatarSettings settings = null, draft = null;
            AvatarRegistry registry = null;
            try
            {
                var importer = AssetImporter.GetAtPath(sourcePath);
                if (!sourcePath.StartsWith("Assets/", StringComparison.Ordinal) || !sourcePath.EndsWith(".vrm", StringComparison.OrdinalIgnoreCase) ||
                    !importer || importer.GetType().FullName != "UniVRM10.VrmScriptedImporter")
                    throw new InvalidOperationException("Select a project .vrm source with the installed UniVRM importer.");
                var importerData = new SerializedObject(importer);
                var migrate = importerData.FindProperty("MigrateToVrm1");
                var pipeline = importerData.FindProperty("RenderPipeline");
                int urp = Array.IndexOf(pipeline.enumNames, "UniversalRenderPipeline");
                if (!migrate.boolValue || pipeline.enumValueIndex != urp)
                {
                    changes.Capture(importer);
                    migrate.boolValue = true;
                    pipeline.enumValueIndex = urp;
                    importerData.ApplyModifiedPropertiesWithoutUndo();
                    importer.SaveAndReimport();
                }
                string guid = AssetDatabase.AssetPathToGUID(sourcePath);
                var sourceAsset = AssetDatabase.LoadAssetAtPath<GameObject>(sourcePath);
                stage = "identity resolution";
                settings = FindSettings(guid);
                registry = AssetDatabase.LoadAssetAtPath<AvatarRegistry>(RegistryPath);
                RejectDuplicateIds(registry);
                draft = settings ? Instantiate(settings) : CreateInstance<AvatarSettings>();
                if (!settings) draft.Id = NewId(registry);
                else if (!draft.Id.IsValid) throw new InvalidOperationException("Existing settings have an invalid identity; use Assign New Identity explicitly.");
                if (string.IsNullOrWhiteSpace(draft.DisplayName)) draft.DisplayName = sourceAsset.name;
                stage = "source content checks";
                root = (GameObject)PrefabUtility.InstantiatePrefab(sourceAsset, scene);
                root.SetActive(false);
                var animators = root.GetComponentsInChildren<Animator>(true);
                if (animators.Length != 1 || animators[0].gameObject != root)
                    throw new InvalidOperationException("Source must have exactly one Animator on the VRM root.");
                var animator = animators[0];
                if (!animator.avatar || !animator.avatar.isValid || !animator.avatar.isHuman)
                    throw new InvalidOperationException("Source needs a valid Humanoid Avatar.");
                var vrm = root.GetComponent<Vrm10Instance>();
                if (!vrm || !vrm.Vrm || !root.GetComponent<UniHumanoid.Humanoid>())
                    throw new InvalidOperationException("Source must retain Vrm10Instance, VRM metadata, and UniHumanoid.Humanoid.");
                Vector3 scale = root.transform.localScale;
                if (scale.x <= 0f || !Mathf.Approximately(scale.x, scale.y) || !Mathf.Approximately(scale.x, scale.z))
                    throw new InvalidOperationException("Source root scale must be positive and uniform.");
                root.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                root.transform.localScale = Vector3.one;
                foreach (var bone in AvatarContentValidation.RequiredBones)
                    if (!animator.GetBoneTransform(bone)) throw new InvalidOperationException($"Missing required Humanoid mapping: {bone}.");
                stage = "spring authoring";
                AvatarSpringAuthoring.Apply(vrm, draft);
                CheckWriters(root, animator, vrm);
                stage = "source measurements";
                var renderers = root.GetComponentsInChildren<Renderer>(true);
                draft.Generated = Measure(root, animator, renderers, sourceAsset, guid, draft);
                if (!settings) draft.VisualHeight = draft.Generated.Height;
                if (draft.VisualHeight <= 0f || !float.IsFinite(draft.VisualHeight)) throw new InvalidOperationException("VisualHeight must be positive and finite.");
                stage = "shared animation preparation";
                EnsureFolder(SettingsFolder); EnsureFolder(PrefabFolder);
                var animations = PrepareAnimations(prepareAnimations, changes);
                stage = "presentation prefab preparation";
                PrefabUtility.UnpackPrefabInstance(root, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
                PrepareModel(root, animator, vrm, draft.Generated.Height);
                AvatarContentValidation.Validate(root, draft);
                root.SetActive(true);
                string prefabPath = ExistingPrefabPath(registry, draft.Id) ?? $"{PrefabFolder}/{draft.Id}.prefab";
                string settingsPath = settings ? AssetDatabase.GetAssetPath(settings) : $"{SettingsFolder}/{draft.Id}.asset";
                if (!settings && AssetDatabase.LoadMainAssetAtPath(settingsPath)) throw new InvalidOperationException($"Settings path already occupied: {settingsPath}.");
                bool newPrefab = !AssetDatabase.LoadMainAssetAtPath(prefabPath);
                if (newPrefab) changes.Created.Add(prefabPath);
                else changes.CapturePrefab(prefabPath);
                stage = "prefab save";
                var prefab = PrefabUtility.SaveAsPrefabAsset(root, prefabPath, out bool success);
                if (!success || !prefab) throw new InvalidOperationException($"Could not save {prefabPath}.");
                stage = "settings save";
                if (!settings)
                {
                    settings = CreateInstance<AvatarSettings>();
                    changes.Created.Add(settingsPath);
                    AssetDatabase.CreateAsset(settings, settingsPath);
                }
                else changes.Capture(settings);
                EditorUtility.CopySerialized(draft, settings);
                settings.name = Path.GetFileNameWithoutExtension(settingsPath);
                EditorUtility.SetDirty(settings);
                stage = "registry save";
                if (!registry)
                {
                    registry = CreateInstance<AvatarRegistry>();
                    changes.Created.Add(RegistryPath);
                    AssetDatabase.CreateAsset(registry, RegistryPath);
                }
                else changes.Capture(registry);
                var entry = new AvatarRegistry.Entry { Id = settings.Id, Source = sourceAsset, SourceGuid = guid, Prefab = prefab, Settings = settings };
                int index = registry.Entries.FindIndex(e => e != null && e.SourceGuid == guid);
                if (index < 0) registry.Entries.Add(entry); else registry.Entries[index] = entry;
                registry.Animations = animations;
                if (!registry.DefaultId.IsValid) registry.DefaultId = settings.Id;
                registry.Invalidate();
                EditorUtility.SetDirty(registry);
                AssetDatabase.SaveAssets();
                string summary = ContentSummary(prefab);
                Debug.Log($"Avatar {sourceAsset.name}: {summary}", settings);
                return entry;
            }
            catch (Exception exception)
            {
                changes.Restore();
                throw new InvalidOperationException($"Avatar processing failed during {stage}: {exception.Message}", exception);
            }
            finally
            {
                if (root) DestroyImmediate(root);
                if (draft) DestroyImmediate(draft);
                EditorSceneManager.ClosePreviewScene(scene);
            }
        }

        private static AvatarSettings FindSettings(string guid)
        {
            AvatarSettings found = null;
            foreach (string assetGuid in AssetDatabase.FindAssets("t:AvatarSettings"))
            {
                var candidate = AssetDatabase.LoadAssetAtPath<AvatarSettings>(AssetDatabase.GUIDToAssetPath(assetGuid));
                if (candidate.Generated.SourceGuid != guid) continue;
                if (found) throw new InvalidOperationException($"Multiple settings assets claim source {guid}: {AssetDatabase.GetAssetPath(found)}, {AssetDatabase.GetAssetPath(candidate)}.");
                found = candidate;
            }
            return found;
        }

        private static void RejectDuplicateIds(AvatarRegistry registry)
        {
            var ids = new HashSet<ulong>();
            foreach (string guid in AssetDatabase.FindAssets("t:AvatarSettings"))
            {
                var settings = AssetDatabase.LoadAssetAtPath<AvatarSettings>(AssetDatabase.GUIDToAssetPath(guid));
                if (settings.Id.IsValid && !ids.Add(settings.Id.Value)) throw new InvalidOperationException($"Duplicate settings ID {settings.Id}; remove the copied settings asset.");
            }
            ids.Clear();
            if (!registry) return;
            foreach (var entry in registry.Entries)
            {
                if (entry == null) continue;
                if (!ids.Add(entry.Id.Value)) throw new InvalidOperationException($"Duplicate registry ID {entry.Id}; repair the registry.");
                if (entry.Settings && (entry.Settings.Id != entry.Id || entry.Settings.Generated.SourceGuid != entry.SourceGuid))
                    throw new InvalidOperationException($"Registry entry {entry.Id} disagrees with its canonical settings; repair its identity/source reference.");
            }
        }

        private static AvatarId NewId(AvatarRegistry registry)
        {
            var used = new HashSet<ulong>();
            foreach (string guid in AssetDatabase.FindAssets("t:AvatarSettings"))
                used.Add(AssetDatabase.LoadAssetAtPath<AvatarSettings>(AssetDatabase.GUIDToAssetPath(guid)).Id.Value);
            if (registry) foreach (var entry in registry.Entries) if (entry != null) used.Add(entry.Id.Value);
            AvatarId id;
            do { id = new AvatarId(BitConverter.ToUInt64(Guid.NewGuid().ToByteArray(), 0)); } while (!id.IsValid || used.Contains(id.Value));
            return id;
        }

        private static string ExistingPrefabPath(AvatarRegistry registry, AvatarId id)
        {
            if (registry) foreach (var entry in registry.Entries)
                if (entry != null && entry.Id == id && entry.Prefab) return AssetDatabase.GetAssetPath(entry.Prefab);
            return null;
        }

        private static void CheckWriters(GameObject root, Animator animator, Vrm10Instance vrm)
        {
            AvatarSpringRuntimeProvider.ValidateSpringReferences(vrm);
            var animated = new HashSet<Transform>();
            for (int i = 0; i < (int)HumanBodyBones.LastBone; i++)
            {
                var bone = animator.GetBoneTransform((HumanBodyBones)i);
                if (bone) animated.Add(bone);
            }
            foreach (var behaviour in root.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (behaviour is not IVrm10Constraint constraint) continue;
                foreach (var bone in animated)
                    if (bone == constraint.ConstraintTarget || bone.IsChildOf(constraint.ConstraintTarget))
                        throw new InvalidOperationException($"VRM constraint on {constraint.ConstraintTarget.name} overwrites Humanoid animation/IK; remove or retarget that authored constraint.");
            }
            foreach (var spring in vrm.SpringBone.Springs)
                for (int i = 0; i < spring.Joints.Count - 1; i++)
                {
                    var joint = spring.Joints[i];
                    foreach (var bone in animated)
                        if (bone == joint.transform || bone.IsChildOf(joint.transform))
                            throw new InvalidOperationException($"Spring joint {joint.name} writes a Humanoid bone or ancestor; remove that node from the authored spring chain.");
                }
        }

        private static AvatarSettings.GeneratedSkeleton Measure(GameObject root, Animator animator, Renderer[] renderers, GameObject source, string guid, AvatarSettings settings)
        {
            var wrist = animator.GetBoneTransform(HumanBodyBones.RightHand);
            var follow = animator.GetBoneTransform(settings.RightHandFollowBone);
            if (!follow) throw new InvalidOperationException($"Missing configured hand follow bone: {settings.RightHandFollowBone}.");
            Bounds bounds = default;
            bool found = false;
            foreach (var renderer in renderers)
            {
                Mesh mesh = renderer is SkinnedMeshRenderer skin ? skin.sharedMesh : renderer.GetComponent<MeshFilter>()?.sharedMesh;
                if (!mesh || mesh.vertexCount == 0) continue;
                Bounds local = mesh.bounds;
                if (renderer is SkinnedMeshRenderer skinned)
                {
                    var baked = new Mesh();
                    try { skinned.BakeMesh(baked); local = baked.bounds; }
                    finally { DestroyImmediate(baked); }
                }
                Matrix4x4 matrix = root.transform.worldToLocalMatrix * renderer.transform.localToWorldMatrix;
                for (int corner = 0; corner < 8; corner++)
                {
                    Vector3 point = matrix.MultiplyPoint3x4(local.center + Vector3.Scale(local.extents,
                        new Vector3((corner & 1) == 0 ? -1 : 1, (corner & 2) == 0 ? -1 : 1, (corner & 4) == 0 ? -1 : 1)));
                    if (!found) { bounds = new Bounds(point, Vector3.zero); found = true; } else bounds.Encapsulate(point);
                }
            }
            if (!found) throw new InvalidOperationException("Source contains no usable mesh renderer.");
            Vector3 Position(HumanBodyBones bone) => root.transform.InverseTransformPoint(animator.GetBoneTransform(bone).position);
            Vector2 Lengths(HumanBodyBones upper, HumanBodyBones lower, HumanBodyBones end)
            {
                var value = new Vector2(Vector3.Distance(Position(upper), Position(lower)), Vector3.Distance(Position(lower), Position(end)));
                if (value.x <= 0.0001f || value.y <= 0.0001f) throw new InvalidOperationException($"Degenerate limb: {upper}/{lower}/{end}.");
                return value;
            }
            float sole = Mathf.Min(Position(HumanBodyBones.LeftFoot).y - animator.leftFeetBottomHeight,
                Position(HumanBodyBones.RightFoot).y - animator.rightFeetBottomHeight);
            float height = bounds.max.y - sole;
            if (height <= 0f || !float.IsFinite(height) || bounds.size.x <= 0f || bounds.size.z <= 0f)
                throw new InvalidOperationException("Source renderer dimensions/sole plane are invalid.");
            return new AvatarSettings.GeneratedSkeleton
            {
                Source = source, SourceGuid = guid, FormatVersion = AvatarSettings.CurrentFormatVersion, HumanoidAvatar = animator.avatar,
                RightHandFollowBone = settings.RightHandFollowBone,
                RightWristToFollowPosition = Quaternion.Inverse(wrist.rotation) * (follow.position - wrist.position),
                RightWristToFollowRotation = Quaternion.Inverse(wrist.rotation) * follow.rotation,
                Bounds = bounds, Height = height, SolePlane = sole, HumanScale = animator.humanScale,
                Hips = Position(HumanBodyBones.Hips) - Vector3.up * sole, Head = Position(HumanBodyBones.Head) - Vector3.up * sole,
                LeftShoulder = Position(HumanBodyBones.LeftUpperArm) - Vector3.up * sole,
                RightShoulder = Position(HumanBodyBones.RightUpperArm) - Vector3.up * sole,
                LeftLeg = Lengths(HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftLowerLeg, HumanBodyBones.LeftFoot),
                RightLeg = Lengths(HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg, HumanBodyBones.RightFoot),
                LeftArm = Lengths(HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand),
                RightArm = Lengths(HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand),
                LeftSoleToGoal = Vector3.up * animator.leftFeetBottomHeight,
                RightSoleToGoal = Vector3.up * animator.rightFeetBottomHeight,
                LeftFootRestRotation = Quaternion.Inverse(root.transform.rotation) * animator.GetBoneTransform(HumanBodyBones.LeftFoot).rotation,
                RightFootRestRotation = Quaternion.Inverse(root.transform.rotation) * animator.GetBoneTransform(HumanBodyBones.RightFoot).rotation
            };
        }

        private static void PrepareModel(GameObject root, Animator animator, Vrm10Instance vrm, float height)
        {
            foreach (var t in root.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = LayerMask.NameToLayer("Player");
            foreach (var collider in root.GetComponentsInChildren<Collider>(true)) collider.enabled = false;
            foreach (var body in root.GetComponentsInChildren<Rigidbody>(true)) { body.isKinematic = true; body.detectCollisions = false; }
            foreach (var behaviour in root.GetComponentsInChildren<Behaviour>(true))
            {
                if (behaviour == animator || behaviour == vrm || behaviour is UniHumanoid.Humanoid ||
                    behaviour is IVrm10Constraint || behaviour is VRM10SpringBoneJoint ||
                    behaviour is VRM10SpringBoneCollider || behaviour is VRM10SpringBoneColliderGroup) continue;
                behaviour.enabled = false;
            }
            foreach (var renderer in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                var bounds = renderer.localBounds;
                Vector3 expansion = Vector3.one * (0.15f * height / Mathf.Min(renderer.transform.lossyScale.x,
                    renderer.transform.lossyScale.y, renderer.transform.lossyScale.z));
                bounds.Expand(new Vector3(Mathf.Abs(expansion.x), Mathf.Abs(expansion.y), Mathf.Abs(expansion.z)) * 2f);
                renderer.localBounds = bounds;
                renderer.updateWhenOffscreen = false;
            }
            animator.runtimeAnimatorController = null;
            animator.applyRootMotion = false; animator.fireEvents = false;
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            animator.enabled = false;
            vrm.UpdateType = Vrm10Instance.UpdateTypes.None;
            vrm.LookAtTarget = null; vrm.enabled = false;
            root.AddComponent<AvatarInstance>();
            foreach (var component in root.GetComponents<MonoBehaviour>())
                if (component is IVrm10SpringBoneRuntimeProvider) DestroyImmediate(component);
            root.AddComponent<AvatarSpringRuntimeProvider>();
        }

        public static AvatarAnimationSet PrepareAnimations(bool force = false)
        {
            var changes = new ProcessingChanges();
            try { return PrepareAnimations(force, changes); }
            catch { changes.Restore(); throw; }
        }

        private static AvatarAnimationSet PrepareAnimations(bool force, ProcessingChanges changes)
        {
            var asset = AssetDatabase.LoadAssetAtPath<AvatarAnimationSet>(AnimationPath);
            var draft = asset ? Instantiate(asset) : CreateInstance<AvatarAnimationSet>();
            try
            {
                foreach (var source in ClipFiles)
                {
                    string path = "Assets/Art/Animations/" + source.File;
                    if (AssetImporter.GetAtPath(path) is not ModelImporter importer) throw new InvalidOperationException($"Missing animation source: {path}.");
                    var takes = importer.defaultClipAnimations;
                    if (takes.Length != 1) throw new InvalidOperationException($"{path} needs one unambiguous source take; found {takes.Length}.");
                    var take = takes[0];
                    bool loop = source.File != "jump.fbx";
                    var slot = source.Locomotion >= 0 ? draft.GetLocomotion(source.Locomotion) : default;
                    bool calibrate = source.Locomotion >= 0 && (slot.NominalSpeed <= 0f || slot.ReferenceHumanScale <= 0f);
                    if (calibrate)
                    {
                        changes.Capture(importer);
                        try
                        {
                            ConfigureImporter(importer, take, loop, false);
                            importer.SaveAndReimport();
                            var original = SingleClip(path);
                            Vector3 speed = original.averageSpeed;
                            float horizontal = new Vector2(speed.x, speed.z).magnitude;
                            if (slot.NominalSpeed <= 0f) slot.NominalSpeed = horizontal > 0.05f ? horizontal : source.Locomotion < 4 ? 1.6f : 3.8f;
                            var model = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                            var sourceAnimator = model.GetComponent<Animator>();
                            if (!sourceAnimator || !sourceAnimator.avatar || !sourceAnimator.avatar.isValid || !sourceAnimator.avatar.isHuman)
                                throw new InvalidOperationException($"{path} has no valid source Humanoid Avatar.");
                            if (slot.ReferenceHumanScale <= 0f)
                            {
                                var measurementScene = EditorSceneManager.NewPreviewScene();
                                try
                                {
                                    var measurement = (GameObject)PrefabUtility.InstantiatePrefab(model, measurementScene);
                                    measurement.transform.localScale = Vector3.one;
                                    slot.ReferenceHumanScale = measurement.GetComponent<Animator>().humanScale;
                                }
                                finally { EditorSceneManager.ClosePreviewScene(measurementScene); }
                            }
                        }
                        finally { ConfigureImporter(importer, take, loop, true); importer.SaveAndReimport(); }
                    }
                    if (!calibrate && (force || !ImporterMatches(importer, take, loop)))
                    { changes.Capture(importer); ConfigureImporter(importer, take, loop, true); importer.SaveAndReimport(); }
                    var clip = SingleClip(path);
                    if (!clip.humanMotion || clip.length <= 0f) throw new InvalidOperationException($"{path} must contain a nonempty Humanoid clip.");
                    if (source.Locomotion >= 0) { slot.Clip = clip; draft.SetLocomotion(source.Locomotion, slot); }
                    else switch (source.File)
                    {
                        case "idle.fbx": draft.Idle = clip; break;
                        case "jump.fbx": draft.Jump = clip; break;
                        case "Falling.fbx": draft.Fall = clip; break;
                        case "Seated Idle.fbx": draft.Seated = clip; break;
                        default: throw new InvalidOperationException($"No animation assignment for {source.File}.");
                    }
                }
                if (!draft.IsComplete) throw new InvalidOperationException("Animation set has invalid calibration, jump intervals, or cycle durations incompatible with the shared playback limits.");
                EnsureFolder(SettingsFolder);
                if (!asset)
                {
                    asset = CreateInstance<AvatarAnimationSet>();
                    changes.Created.Add(AnimationPath);
                    AssetDatabase.CreateAsset(asset, AnimationPath);
                }
                else changes.Capture(asset);
                EditorUtility.CopySerialized(draft, asset);
                asset.name = "AvatarAnimationSet";
                EditorUtility.SetDirty(asset);
                AssetDatabase.SaveAssets();
                return asset;
            }
            finally { DestroyImmediate(draft); }
        }

        private sealed class ProcessingChanges
        {
            internal readonly List<string> Created = new();
            private readonly Dictionary<Object, string> assets = new();
            private readonly Dictionary<string, string> importers = new();
            private string prefabPath;
            private byte[] prefab;

            internal void Capture(Object asset)
            {
                if (asset is AssetImporter importer)
                {
                    if (!importers.ContainsKey(importer.assetPath)) importers.Add(importer.assetPath, EditorJsonUtility.ToJson(importer));
                }
                else if (!assets.ContainsKey(asset)) assets.Add(asset, EditorJsonUtility.ToJson(asset));
            }

            internal void CapturePrefab(string path) { prefabPath = path; prefab = File.ReadAllBytes(path); }

            internal void Restore()
            {
                foreach (var pair in importers)
                {
                    var importer = AssetImporter.GetAtPath(pair.Key);
                    EditorJsonUtility.FromJsonOverwrite(pair.Value, importer);
                    importer.SaveAndReimport();
                }
                if (prefab != null)
                {
                    File.WriteAllBytes(prefabPath, prefab);
                    AssetDatabase.ImportAsset(prefabPath, ImportAssetOptions.ForceUpdate);
                }
                foreach (var pair in assets)
                {
                    EditorJsonUtility.FromJsonOverwrite(pair.Value, pair.Key);
                    EditorUtility.SetDirty(pair.Key);
                    if (pair.Key is AvatarRegistry registry) registry.Invalidate();
                }
                for (int i = Created.Count - 1; i >= 0; i--) AssetDatabase.DeleteAsset(Created[i]);
                AssetDatabase.SaveAssets();
            }
        }

        private static AnimationClip SingleClip(string path)
        {
            AnimationClip found = null;
            foreach (var subasset in AssetDatabase.LoadAllAssetsAtPath(path))
            {
                if (subasset is not AnimationClip clip || clip.name.StartsWith("__preview__", StringComparison.Ordinal)) continue;
                if (found) throw new InvalidOperationException($"Ambiguous animation clips in {path}.");
                found = clip;
            }
            return found ? found : throw new InvalidOperationException($"Missing source animation clip in {path}.");
        }

        private static bool ImporterMatches(ModelImporter importer, ModelImporterClipAnimation take, bool loop)
        {
            if (importer.animationType != ModelImporterAnimationType.Human || importer.avatarSetup != ModelImporterAvatarSetup.CreateFromThisModel ||
                !importer.importAnimation || importer.optimizeGameObjects || importer.clipAnimations.Length != 1) return false;
            var clip = importer.clipAnimations[0];
            return clip.takeName == take.takeName && clip.firstFrame == take.firstFrame && clip.lastFrame == take.lastFrame &&
                clip.loopTime == loop && clip.loopPose == loop && clip.lockRootRotation && clip.lockRootPositionXZ && clip.lockRootHeightY &&
                clip.keepOriginalOrientation && clip.keepOriginalPositionXZ && clip.keepOriginalPositionY && clip.events.Length == 0;
        }

        private static void ConfigureImporter(ModelImporter importer, ModelImporterClipAnimation take, bool loop, bool bake)
        {
            importer.animationType = ModelImporterAnimationType.Human;
            importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
            importer.importAnimation = true; importer.optimizeGameObjects = false;
            take.loopTime = loop; take.loopPose = loop;
            take.keepOriginalOrientation = take.keepOriginalPositionXZ = take.keepOriginalPositionY = true;
            take.lockRootRotation = take.lockRootPositionXZ = take.lockRootHeightY = bake;
            take.events = Array.Empty<AnimationEvent>();
            importer.clipAnimations = new[] { take };
        }

        private static string ContentSummary(GameObject prefab)
        {
            int triangles = 0, slots = 0, skinned = 0, joints = 0;
            var renderers = prefab.GetComponentsInChildren<Renderer>(true);
            foreach (var renderer in renderers)
            {
                if (renderer is SkinnedMeshRenderer) skinned++;
                slots += renderer.sharedMaterials.Length;
                Mesh mesh = renderer is SkinnedMeshRenderer skin ? skin.sharedMesh : renderer.GetComponent<MeshFilter>()?.sharedMesh;
                if (mesh) for (int i = 0; i < mesh.subMeshCount; i++) if (mesh.GetTopology(i) == MeshTopology.Triangles) triangles += (int)mesh.GetIndexCount(i) / 3;
            }
            var vrm = prefab.GetComponent<Vrm10Instance>();
            if (vrm) foreach (var spring in vrm.SpringBone.Springs) joints += Mathf.Max(0, spring.Joints.Count - 1);
            string summary = $"{renderers.Length} renderers, {skinned} skinned renderers, {triangles:N0} triangles, {slots} material slots, {joints} spring joints.";
            if (triangles > 100000 || slots > 12 || skinned > 8 || joints > 128) Debug.LogWarning($"Avatar content exceeds a review threshold: {summary}", prefab);
            return summary;
        }

        private static void AssignNewIdentity(AvatarSettings settings)
        {
            if (EditorApplication.isPlaying) throw new InvalidOperationException("Identity assignment is unavailable in Play Mode.");
            var registry = AssetDatabase.LoadAssetAtPath<AvatarRegistry>(RegistryPath);
            if (!registry || !registry.Entries.Exists(entry => entry != null && entry.Settings == settings))
            {
                Process(AssetDatabase.GUIDToAssetPath(settings.Generated.SourceGuid));
                registry = AssetDatabase.LoadAssetAtPath<AvatarRegistry>(RegistryPath);
            }
            RejectDuplicateIds(registry);
            var old = settings.Id;
            var id = NewId(registry);
            string oldSettings = AssetDatabase.GetAssetPath(settings);
            string oldSettingsName = settings.name;
            string oldPrefab = ExistingPrefabPath(registry, old) ?? $"{PrefabFolder}/{old}.prefab";
            string newSettings = $"{SettingsFolder}/{id}.asset", newPrefab = $"{PrefabFolder}/{id}.prefab";
            bool movedSettings = false, movedPrefab = false;
            try
            {
                string error = AssetDatabase.MoveAsset(oldSettings, newSettings);
                if (!string.IsNullOrEmpty(error)) throw new InvalidOperationException(error);
                movedSettings = true;
                if (AssetDatabase.LoadMainAssetAtPath(oldPrefab))
                {
                    error = AssetDatabase.MoveAsset(oldPrefab, newPrefab);
                    if (!string.IsNullOrEmpty(error)) throw new InvalidOperationException(error);
                    movedPrefab = true;
                }
                settings.Id = id;
                settings.name = Path.GetFileNameWithoutExtension(newSettings);
                EditorUtility.SetDirty(settings);
                if (registry)
                {
                    foreach (var entry in registry.Entries) if (entry != null && entry.Settings == settings) entry.Id = id;
                    if (registry.DefaultId == old) registry.DefaultId = id;
                    registry.Invalidate(); EditorUtility.SetDirty(registry);
                }
                AssetDatabase.SaveAssets();
            }
            catch
            {
                settings.Id = old;
                if (registry)
                {
                    foreach (var entry in registry.Entries) if (entry != null && entry.Settings == settings) entry.Id = old;
                    if (registry.DefaultId == id) registry.DefaultId = old;
                    registry.Invalidate();
                }
                if (movedPrefab) AssetDatabase.MoveAsset(newPrefab, oldPrefab);
                if (movedSettings) AssetDatabase.MoveAsset(newSettings, oldSettings);
                settings.name = oldSettingsName;
                throw;
            }
        }

        private static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            string parent = Path.GetDirectoryName(path).Replace('\\', '/');
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
        }
    }
}
