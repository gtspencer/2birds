#if UNITY_EDITOR
using System;
using System.IO;
using Unity.ProjectAuditor.Editor;
using UnityEngine;

namespace AgentTools
{
    public static class ProjectAuditorCli
    {
        public static void Run()
        {
            var outputDirectory = Path.GetFullPath(Path.Combine(Application.dataPath, "..", ".lint"));
            Directory.CreateDirectory(outputDirectory);

            var timestamp = Environment.GetEnvironmentVariable("UNITY_LINT_TIMESTAMP")
                ?? DateTime.Now.ToString("yyyy_MM_dd_HH-mm");
            var outputPath = Path.Combine(outputDirectory, $"project-auditor_{timestamp}.projectauditor");
            var auditor = new ProjectAuditor();
            var report = auditor.Audit();
            report.Save(outputPath);

            Debug.Log($"Project Auditor completed with {report.NumTotalIssues} total report items. Saved to: {outputPath}");
        }
    }
}
#endif
