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
        public override int callbackOrder => 0;
        public override void PrepareForBuild(BuildPlayerContext context)
        {
            if ((context.BuildPlayerOptions.scenes ?? Array.Empty<string>()).Contains(GripAuthoringBuildPolicy.ScenePath))
                throw new BuildFailedException("Apply GripAuthoringBuildPolicy.Apply to BuildPlayerOptions before building: the grip authoring scene is editor-only.");
        }
    }
}
