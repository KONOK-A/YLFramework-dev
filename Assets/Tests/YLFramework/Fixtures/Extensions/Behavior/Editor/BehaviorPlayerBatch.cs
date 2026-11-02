using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace YLFramework.Behavior.Acceptance
{
    public static class BehaviorPlayerBatch
    {
        [Serializable]
        private sealed class Result
        {
            public string utc;
            public string unity;
            public string requestedBackend;
            public string actualBackend;
            public string stripping;
            public string output;
            public string status;
            public string error;
            public string guid;
            public string company;
            public string product;
            public bool development;
            public ulong size;
            public int errors;
            public int warnings;
            public double seconds;
        }

        public static void BuildMono() => Build(ScriptingImplementation.Mono2x);
        public static void BuildIL2CPP() => Build(ScriptingImplementation.IL2CPP);

        private static void Build(ScriptingImplementation expected)
        {
            string output = BehaviorUnityBatch.Output();
            var actual = PlayerSettings.GetScriptingBackend(BuildTargetGroup.Standalone);
            var stripping = PlayerSettings.GetManagedStrippingLevel(BuildTargetGroup.Standalone);
            var result = new Result { utc = DateTime.UtcNow.ToString("o"), unity = Application.unityVersion, requestedBackend = expected.ToString(), actualBackend = actual.ToString(), stripping = stripping.ToString(), output = output };
            if (actual != expected || (expected == ScriptingImplementation.IL2CPP && stripping != ManagedStrippingLevel.Low))
            {
                result.status = "BlockedBeforeBuild";
                result.error = "Current standalone backend/stripping does not match. No PlayerSettings backend/map mutation is authorized: API default restoration can leave new Standalone keys. No build or Player run was performed.";
                Write(output, result);
                Debug.LogWarning(result.error);
                return;
            }
            string company = PlayerSettings.companyName;
            string product = PlayerSettings.productName;
            BuildReport report = null;
            try
            {
                // 两个既有字符串临时隔离Player持久化目录；finally原值恢复并由外层SHA核实。
                PlayerSettings.companyName = "YLFramework.Behavior.Acceptance";
                PlayerSettings.productName = "A_" + Path.GetFileName(output);
                result.company = PlayerSettings.companyName;
                result.product = PlayerSettings.productName;
                report = BehaviorAcceptanceBuilder.Build(expected, false, Path.Combine(output, "player"));
                Fill(result, report);
                if (report.summary.result != BuildResult.Succeeded || result.development)
                {
                    throw new InvalidOperationException("Expected successful non-Development build: " + report.summary.result);
                }

                result.status = "BuildSucceeded_PlayerNotRun";
            }
            catch (Exception error)
            {
                report = report ? report : BuildReport.GetLatestReport();
                if (report)
                {
                    Fill(result, report);
                }

                result.status = "BuildFailed";
                result.error = error.ToString();
                throw;
            }
            finally
            {
                PlayerSettings.companyName = company;
                PlayerSettings.productName = product;
                Write(output, result);
            }
        }

        private static void Fill(Result result, BuildReport report)
        {
            var summary = report.summary;
            result.output = summary.outputPath;
            result.guid = summary.guid.ToString();
            result.development = (summary.options & BuildOptions.Development) != 0;
            result.errors = summary.totalErrors;
            result.warnings = summary.totalWarnings;
            result.size = summary.totalSize;
            result.seconds = summary.totalTime.TotalSeconds;
        }

        private static void Write(string output, Result result)
            => File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(output, "build-results.json")), JsonUtility.ToJson(result, true), new UTF8Encoding(false));
    }
}
