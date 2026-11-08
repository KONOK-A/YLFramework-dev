using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace YLFramework.Tests
{
    public class UnityJsonPlayerTests
    {
        [Serializable]
        private sealed class Report
        {
            public string status;
            public string error;
            public string errorLogs;
            public string platform;
            public string unityVersion;
            public string runtimeAssembly;
            public string saveAssembly;
            public string probeAssembly;
            public int pid;
            public int writerPid;
            public int run;
            public bool isEditor;
            public bool isIL2CPP;
            public bool developmentBuild;
            public bool mainPresent;
            public bool savePresent;
            public bool jsonRoundTrip;
            public bool nullableRoots;
            public int componentRejections;
            public bool saveRoundTrip;
            public bool coldProcessRead;
            public bool backupRecovered;
        }

        [Serializable]
        private sealed class BuildEvidence
        {
            public string unityVersion;
            public string target;
            public string backend;
            public string stripping;
            public string options;
            public string extraDefine;
            public string result;
            public string executable;
            public string nativeAssembly;
            public string metadata;
        }

        [Test]
        [Category("YL.Special.UnityJsonPlayer")]
        public void MainAndSaveConsumerRoundTripsUnityValuesAcrossTwoRealMediumStrippedIl2CppProcesses()
        {
            Assert.That(Application.isBatchMode && !EditorApplication.isPlaying && !EditorApplication.isCompiling, Is.True);
            string runOutput = global::YLFramework.Testing.Editor.YLTestEntry.RequireOwnedRunOutput();
            var products = UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages()
                .Where(package => package.name == "com.kano.ylframework" || package.name.StartsWith("com.kano.ylframework.", StringComparison.Ordinal))
                .Select(package => package.name);
            Assert.That(products, Is.EquivalentTo(new[] { "com.kano.ylframework", "com.kano.ylframework.save" }),
                "This consumer must not inherit DataAssets, Localization or another optional product package.");
            string output = Path.Combine(runOutput, "UnityJsonPlayer-IL2CPP");
            Assert.That(Directory.Exists(output) || File.Exists(output), Is.False);
            Directory.CreateDirectory(output);
            string project = Directory.GetParent(Application.dataPath).FullName;
            Assert.That(File.Exists(Path.Combine(project, ".yl-owner.json")), Is.True);
            string player = Path.Combine(project, "P", Guid.NewGuid().ToString("N").Substring(0, 8));
            Assert.That(Directory.Exists(player) || File.Exists(player), Is.False);
            for (var parent = new DirectoryInfo(player); parent != null; parent = parent.Parent)
            {
                if (parent.Exists)
                {
                    Assert.That((parent.Attributes & FileAttributes.ReparsePoint) == 0, Is.True);
                }
            }
            Directory.CreateDirectory(player);
            File.WriteAllText(Path.Combine(output, "unity-json-player-location.txt"), player);

            const string EmptyScene = "Assets/YLTestingProfile/Empty.unity";
            Assert.That(File.Exists(Path.Combine(project, EmptyScene)), Is.True);
            foreach (var opened in Enumerable.Range(0, SceneManager.sceneCount).Select(SceneManager.GetSceneAt))
            {
                Assert.That(opened.isDirty, Is.False, "A build fixture must never save or replace a dirty host scene.");
                Assert.That(opened.rootCount, Is.Zero);
                Assert.That(string.IsNullOrEmpty(opened.path) || opened.path == EmptyScene, Is.True,
                    "Only the owned preparation scene and UTF's empty transient scene may be replaced.");
            }
            var backend = PlayerSettings.GetScriptingBackend(BuildTargetGroup.Standalone);
            var stripping = PlayerSettings.GetManagedStrippingLevel(BuildTargetGroup.Standalone);
            string folder = "Assets/__YLUnityJson_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            Assert.That(Directory.Exists(Path.Combine(project, folder)) || File.Exists(Path.Combine(project, folder)), Is.False);
            string folderGuid = AssetDatabase.CreateFolder("Assets", folder.Substring(7));
            Assert.That(folderGuid, Is.Not.Null.And.Not.Empty);
            string executable = Path.Combine(player, "P.exe");
            try
            {
                var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                var probe = Type.GetType("YLFramework.Tests.DataBatch.UnityJsonPlayerProbe, YLFramework.Tests.Data.Probes", true);
                new GameObject("UnityJsonPlayerProbe").AddComponent(probe);
                string scenePath = folder + "/Player.unity";
                Assert.That(EditorSceneManager.SaveScene(scene, scenePath), Is.True);
                PlayerSettings.SetScriptingBackend(BuildTargetGroup.Standalone, ScriptingImplementation.IL2CPP);
                PlayerSettings.SetManagedStrippingLevel(BuildTargetGroup.Standalone, ManagedStrippingLevel.Medium);
                var build = BuildPipeline.BuildPlayer(new BuildPlayerOptions
                {
                    scenes = new[] { scenePath },
                    locationPathName = executable,
                    target = BuildTarget.StandaloneWindows64,
                    options = BuildOptions.Development,
                    extraScriptingDefines = new[] { "YL_UNITY_JSON_ACCEPTANCE" }
                });
                File.WriteAllText(Path.Combine(output, "build-result.json"), JsonUtility.ToJson(new BuildEvidence
                {
                    unityVersion = Application.unityVersion,
                    target = build.summary.platform.ToString(),
                    backend = PlayerSettings.GetScriptingBackend(BuildTargetGroup.Standalone).ToString(),
                    stripping = PlayerSettings.GetManagedStrippingLevel(BuildTargetGroup.Standalone).ToString(),
                    options = build.summary.options.ToString(),
                    extraDefine = "YL_UNITY_JSON_ACCEPTANCE",
                    result = build.summary.result.ToString(),
                    executable = executable,
                    nativeAssembly = Path.Combine(player, "GameAssembly.dll"),
                    metadata = Path.Combine(player, "P_Data/il2cpp_data/Metadata/global-metadata.dat")
                }, true));
                Assert.That(build.summary.result, Is.EqualTo(UnityEditor.Build.Reporting.BuildResult.Succeeded));
                Assert.That(build.summary.platform, Is.EqualTo(BuildTarget.StandaloneWindows64));
                Assert.That(build.summary.options.HasFlag(BuildOptions.Development), Is.True);
            }
            finally
            {
                try
                {
                    PlayerSettings.SetScriptingBackend(BuildTargetGroup.Standalone, backend);
                }
                finally
                {
                    PlayerSettings.SetManagedStrippingLevel(BuildTargetGroup.Standalone, stripping);
                    EditorSceneManager.OpenScene(EmptyScene, OpenSceneMode.Single);
                }
            }
            Assert.That(PlayerSettings.GetScriptingBackend(BuildTargetGroup.Standalone), Is.EqualTo(backend));
            Assert.That(PlayerSettings.GetManagedStrippingLevel(BuildTargetGroup.Standalone), Is.EqualTo(stripping));
            Assert.That(SceneManager.sceneCount, Is.EqualTo(1));
            Assert.That(SceneManager.GetActiveScene().path, Is.EqualTo(EmptyScene));
            Assert.That(SceneManager.GetActiveScene().isDirty, Is.False);
            Assert.That(SceneManager.GetActiveScene().rootCount, Is.Zero);

            foreach (string artifact in new[] { executable, Path.Combine(player, "GameAssembly.dll"), Path.Combine(player, "UnityPlayer.dll"),
                Path.Combine(player, "P_Data/il2cpp_data/Metadata/global-metadata.dat") })
            {
                Assert.That(File.Exists(artifact), Is.True, "Missing actual native build artifact: " + artifact);
                Assert.That(new FileInfo(artifact).Length, Is.GreaterThan(0));
            }
            Assert.That(Directory.Exists(Path.Combine(player, "MonoBleedingEdge")), Is.False,
                "A Mono output is not evidence of an IL2CPP consumer.");
            int firstPid = 0;
            for (int run = 1; run <= 2; run++)
            {
                string path = Path.Combine(player, "result-" + run + ".json");
                string log = Path.Combine(player, "player-" + run + ".log");
                Assert.That(File.Exists(path) || File.Exists(log), Is.False);
                using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(executable)
                {
                    UseShellExecute = false,
                    WorkingDirectory = player,
                    Arguments = "-batchmode -nographics -ylUnityJsonResult \"" + path + "\" -logFile \"" + log + "\""
                });
                Assert.That(process, Is.Not.Null);
                if (!process.WaitForExit(90000))
                {
                    process.Kill();
                    process.WaitForExit(10000);
                    Assert.Fail("Unity JSON IL2CPP Player timed out; no success or cold-process read is inferred.");
                }
                if (File.Exists(path))
                {
                    File.Copy(path, Path.Combine(output, "UnityJsonPlayer-" + run + ".json"));
                }
                if (File.Exists(log))
                {
                    File.Copy(log, Path.Combine(output, "player-" + run + ".log"));
                }
                Assert.That(File.Exists(path), Is.True, "The actual Player did not produce its report; inspect " + log);
                var report = JsonUtility.FromJson<Report>(File.ReadAllText(path));
                Assert.That(report, Is.Not.Null);
                Assert.That(report.status, Is.EqualTo("Passed"), report.error);
                Assert.That(process.ExitCode, Is.Zero, report.error);
                Assert.That(report.errorLogs, Is.Null.Or.Empty);
                Assert.That(report.pid, Is.EqualTo(process.Id).And.Not.EqualTo(firstPid));
                Assert.That(report.run, Is.EqualTo(run));
                Assert.That(report.isEditor, Is.False);
                Assert.That(report.platform, Is.EqualTo("WindowsPlayer"));
                Assert.That(report.unityVersion, Is.EqualTo(Application.unityVersion));
                Assert.That(report.isIL2CPP && report.developmentBuild, Is.True);
                Assert.That(report.mainPresent && report.savePresent, Is.True);
                Assert.That(report.runtimeAssembly, Is.EqualTo("YLFramework.Runtime"));
                Assert.That(report.saveAssembly, Is.EqualTo("YLFramework.Save"));
                Assert.That(report.probeAssembly, Is.EqualTo("YLFramework.Tests.Data.Probes"));
                Assert.That(report.jsonRoundTrip && report.nullableRoots && report.saveRoundTrip, Is.True);
                Assert.That(report.componentRejections, Is.EqualTo(59));
                Assert.That(report.coldProcessRead, Is.EqualTo(run == 2));
                Assert.That(report.backupRecovered, Is.EqualTo(run == 2));
                if (run == 1)
                {
                    firstPid = process.Id;
                }
                Assert.That(report.writerPid, Is.EqualTo(firstPid));
            }
            Assert.That(AssetDatabase.AssetPathToGUID(folder), Is.EqualTo(folderGuid));
            Assert.That(AssetDatabase.DeleteAsset(folder), Is.True);
        }
    }
}
