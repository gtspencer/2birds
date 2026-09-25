using System;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;

namespace TwoBirds.Editor
{
    [InitializeOnLoad]
    public static class GripAuthoringBuildPolicy
    {
        public const string ScenePath = "Assets/Scenes/AvatarPresentationDemo.unity";
        static GripAuthoringBuildPolicy() => BuildPlayerWindow.RegisterBuildPlayerHandler(options =>
            BuildPlayerWindow.DefaultBuildMethods.BuildPlayer(Apply(options)));

        public static BuildPlayerOptions Apply(BuildPlayerOptions options)
        {
            options.scenes = (options.scenes ?? Array.Empty<string>()).Where(scene => scene != ScenePath).ToArray();
            return options;
        }
    }

    public sealed class GripAuthoringBuildGuard : BuildPlayerProcessor
    {
        private const string ItemRegistryPath = "Assets/Game/ScriptableObjects/ItemRegistry.asset";
        public override int callbackOrder => 0;
        public override void PrepareForBuild(BuildPlayerContext context)
        {
            if ((context.BuildPlayerOptions.scenes ?? Array.Empty<string>()).Contains(GripAuthoringBuildPolicy.ScenePath))
                throw new BuildFailedException("Apply GripAuthoringBuildPolicy.Apply to BuildPlayerOptions before building: the grip authoring scene is editor-only.");
            var items = AssetDatabase.LoadAssetAtPath<ItemRegistry>(ItemRegistryPath);
            var missing = items ? items.Items.Where(item => item && !item.HoldClass).Select(item => item.name).ToArray() : Array.Empty<string>();
            if (missing.Length > 0) throw new BuildFailedException("Items without a Hold Class: " + string.Join(", ", missing));
        }
    }
}
