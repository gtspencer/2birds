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
            if (report.summary.platform != BuildTarget.StandaloneWindows64) return;
            string dir = Path.GetDirectoryName(report.summary.outputPath);
            File.Copy("steam_appid.txt", Path.Combine(dir, "steam_appid.txt"), true);
            if ((report.summary.options & BuildOptions.Development) == 0) return;
            string exe = Path.GetFileName(report.summary.outputPath);
            File.WriteAllText(Path.Combine(dir, "LocalNetwork.bat"), $"@start \"\" \"%~dp0{exe}\" -localNetworking\r\n");
        }
    }
}
