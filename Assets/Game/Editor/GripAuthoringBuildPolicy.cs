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
            var scenes = (options.scenes ?? Array.Empty<string>()).ToList();
            bool development = (options.options & BuildOptions.Development) != 0;
            int first = scenes.IndexOf(ScenePath);
            for (int index = scenes.Count - 1; index >= 0; index--)
                if (scenes[index] == ScenePath && (!development || index != first)) scenes.RemoveAt(index);
            if (development && first < 0) scenes.Add(ScenePath);
            options.scenes = scenes.ToArray();
            return options;
        }
    }

    public sealed class GripAuthoringBuildGuard : BuildPlayerProcessor
    {
        public override int callbackOrder => 0;
        public override void PrepareForBuild(BuildPlayerContext context)
        {
            var options = context.BuildPlayerOptions;
            bool development = (options.options & BuildOptions.Development) != 0;
            int count = (options.scenes ?? Array.Empty<string>()).Count(scene => scene == GripAuthoringBuildPolicy.ScenePath);
            if (count != (development ? 1 : 0))
                throw new BuildFailedException("Apply GripAuthoringBuildPolicy.Apply to BuildPlayerOptions before building: development requires one authoring scene; release excludes it.");
        }
    }
}
