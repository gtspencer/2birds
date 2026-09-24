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
            bool instrumentation = IncludesInstrumentation(options);
            int first = scenes.IndexOf(ScenePath);
            for (int index = scenes.Count - 1; index >= 0; index--)
                if (scenes[index] == ScenePath && (!instrumentation || index != first)) scenes.RemoveAt(index);
            if (instrumentation && first < 0) scenes.Add(ScenePath);
            options.scenes = scenes.ToArray();
            return options;
        }

        internal static bool IncludesInstrumentation(BuildPlayerOptions options)
        {
            var target = NamedBuildTarget.FromBuildTargetGroup(BuildPipeline.GetBuildTargetGroup(options.target));
            return PlayerSettings.GetManagedCodeVariant(target) != ManagedCodeVariant.Release ||
                (options.options & (BuildOptions.Development | BuildOptions.AllowDebugging)) != 0 ||
                PlayerSettings.GetScriptingDefineSymbols(target).Split(';').Contains("UNITY_INCLUDE_INSTRUMENTATION") ||
                options.extraScriptingDefines?.Contains("UNITY_INCLUDE_INSTRUMENTATION") == true;
        }
    }

    public sealed class GripAuthoringBuildGuard : BuildPlayerProcessor
    {
        public override int callbackOrder => 0;
        public override void PrepareForBuild(BuildPlayerContext context)
        {
            var options = context.BuildPlayerOptions;
            bool instrumentation = GripAuthoringBuildPolicy.IncludesInstrumentation(options);
            int count = (options.scenes ?? Array.Empty<string>()).Count(scene => scene == GripAuthoringBuildPolicy.ScenePath);
            if (count != (instrumentation ? 1 : 0))
                throw new BuildFailedException("Apply GripAuthoringBuildPolicy.Apply to BuildPlayerOptions before building: instrumentation requires one authoring scene; builds without instrumentation exclude it.");
        }
    }
}
