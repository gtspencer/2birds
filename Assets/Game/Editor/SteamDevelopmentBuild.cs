using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;

namespace TwoBirds.Editor
{
    public sealed class SteamDevelopmentBuild : IPostprocessBuildWithReport
    {
        public int callbackOrder => 0;

        public void OnPostprocessBuild(BuildReport report)
        {
            if (report.summary.platform != BuildTarget.StandaloneWindows64 ||
                (report.summary.options & BuildOptions.Development) == 0) return;
            File.Copy("steam_appid.txt", Path.Combine(Path.GetDirectoryName(report.summary.outputPath), "steam_appid.txt"), true);
        }
    }
}
