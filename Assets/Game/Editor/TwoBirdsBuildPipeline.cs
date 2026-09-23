using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace TwoBirds.Editor
{
    public static class TwoBirdsBuildPipeline
    {
        private const string ExeName = "TwoBirds.exe";
        private const string SteamUsername = "spencerobsitnik";
        private const string SteamCmdFallbackPath = @"C:\steamworks_sdk_165\sdk\tools\ContentBuilder\builder\steamcmd.exe";

        [MenuItem("Two Birds/Build/Local Networking", priority = 0)]
        private static void BuildLocalNetworking()
        {
            Build("Build/LocalNetworking", ManagedCodeVariant.Checked, BuildOptions.Development,
                new[] { "TWO_BIRDS_LOCAL_NETWORKING" }, postBuild: outputDir =>
            {
                string exe = Path.GetFileName(ExeName);
                File.WriteAllText(Path.Combine(outputDir, "LocalNetwork.bat"),
                    $"@start \"\" \"%~dp0{exe}\" -localNetworking\r\n");
                Debug.Log("[Build] Created LocalNetwork.bat");
            });
        }

        [MenuItem("Two Birds/Build/Development", priority = 1)]
        private static void BuildDevelopment()
        {
            BuildWithSteamAppId("Build/Development", ManagedCodeVariant.Checked, BuildOptions.Development);
        }

        [MenuItem("Two Birds/Build/Development - Steam", priority = 2)]
        private static void BuildDevelopmentSteam()
        {
            string repoRoot = Directory.GetCurrentDirectory();
            string vdfPath = Path.Combine(repoRoot, "BuildScripts", "Steam", "app_build_5300780.vdf");
            if (!File.Exists(vdfPath))
            {
                Debug.LogError($"[Build] Steam VDF not found: {vdfPath}");
                return;
            }

            bool built = Build("Build/Development-Steam", ManagedCodeVariant.Checked, BuildOptions.Development, null);
            if (!built) return;

            UploadToSteam(vdfPath);
        }

        [MenuItem("Two Birds/Build/Release", priority = 3)]
        private static void BuildRelease()
        {
            BuildWithSteamAppId("Build/Release", ManagedCodeVariant.Release, BuildOptions.None);
        }

        private static void BuildWithSteamAppId(string outputRelPath, ManagedCodeVariant variant, BuildOptions buildOptions)
        {
            string steamAppId = Path.Combine(Directory.GetCurrentDirectory(), "steam_appid.txt");
            if (!File.Exists(steamAppId))
            {
                Debug.LogError("[Build] steam_appid.txt not found at repository root.");
                return;
            }

            Build(outputRelPath, variant, buildOptions, null, postBuild: outputDir =>
            {
                File.Copy(steamAppId, Path.Combine(outputDir, "steam_appid.txt"), true);
                Debug.Log("[Build] Copied steam_appid.txt");
            });
        }

        private static bool Build(string outputRelPath, ManagedCodeVariant variant, BuildOptions buildOptions,
            string[] extraDefines, Action<string> postBuild = null)
        {
            var scenes = EditorBuildSettings.scenes
                .Where(s => s.enabled)
                .Select(s => s.path)
                .ToArray();

            if (scenes.Length == 0)
            {
                Debug.LogError("[Build] No enabled scenes in Build Settings.");
                return false;
            }

            string outputDir = Path.Combine(Directory.GetCurrentDirectory(), outputRelPath);
            Directory.CreateDirectory(outputDir);

            string locationPathName = Path.Combine(outputDir, ExeName);

            var options = new BuildPlayerOptions
            {
                scenes = scenes,
                locationPathName = locationPathName,
                target = BuildTarget.StandaloneWindows64,
                options = buildOptions,
            };

            if (extraDefines != null && extraDefines.Length > 0)
                options.extraScriptingDefines = extraDefines;

            Debug.Log($"[Build] Starting build: {outputRelPath} (managed variant: {variant}, development: {(buildOptions & BuildOptions.Development) != 0})");
            var target = NamedBuildTarget.Standalone;
            var previousVariant = PlayerSettings.GetManagedCodeVariant(target);
            BuildReport report;
            try
            {
                PlayerSettings.SetManagedCodeVariant(target, variant);
                report = BuildPipeline.BuildPlayer(options);
            }
            finally
            {
                PlayerSettings.SetManagedCodeVariant(target, previousVariant);
            }

            if (report.summary.result != BuildResult.Succeeded)
            {
                Debug.LogError($"[Build] Unity build failed: {report.summary.result}");
                return false;
            }

            if (!File.Exists(locationPathName))
            {
                Debug.LogError($"[Build] Build reported success but executable not found: {locationPathName}");
                return false;
            }

            Debug.Log($"[Build] Unity build succeeded: {locationPathName}");

            postBuild?.Invoke(outputDir);
            return true;
        }

        private static void UploadToSteam(string vdfPath)
        {
            string steamCmd = ResolveSteamCmd();
            if (steamCmd == null)
            {
                Debug.LogError("[Build] steamcmd not found in PATH or at fallback location.");
                return;
            }

            string absVdf = Path.GetFullPath(vdfPath);
            string arguments = $"+login {SteamUsername} +run_app_build \"{absVdf}\" +quit";

            Debug.Log($"[Build] Launching SteamCMD: {steamCmd} {arguments}");

            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = steamCmd,
                    Arguments = arguments,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                }
            };

            process.Start();
            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();

            if (!string.IsNullOrEmpty(stdout)) Debug.Log($"[SteamCMD] {stdout}");
            if (!string.IsNullOrEmpty(stderr)) Debug.LogWarning($"[SteamCMD stderr] {stderr}");

            if (process.ExitCode != 0)
            {
                Debug.LogError($"[Build] SteamCMD failed with exit code {process.ExitCode}. Check Build/SteamPipeOutput for logs.");
                return;
            }

            if (stdout.Contains("FAILED") || stdout.Contains("Login Failure"))
            {
                Debug.LogError("[Build] SteamCMD reported a failure. If authentication expired, run steamcmd +login manually outside Unity.");
                return;
            }

            Debug.Log("[Build] SteamPipe upload succeeded.");
        }

        private static string ResolveSteamCmd()
        {
            string inPath = ResolveFromPath("steamcmd");
            if (inPath != null) return inPath;

            if (File.Exists(SteamCmdFallbackPath)) return SteamCmdFallbackPath;

            return null;
        }

        private static string ResolveFromPath(string exe)
        {
            string pathEnv = Environment.GetEnvironmentVariable("PATH");
            if (string.IsNullOrEmpty(pathEnv)) return null;

            string[] extensions = { "", ".exe", ".cmd", ".bat" };
            foreach (string dir in pathEnv.Split(Path.PathSeparator))
            {
                foreach (string ext in extensions)
                {
                    string full = Path.Combine(dir, exe + ext);
                    if (File.Exists(full)) return full;
                }
            }
            return null;
        }
    }
}
