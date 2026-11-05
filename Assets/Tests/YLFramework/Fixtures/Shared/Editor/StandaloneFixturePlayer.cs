using System;
using System.IO;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using YLFramework.Editor.Resource;

namespace YLFramework.Testing.EditorFixtures
{
    public static class StandaloneFixturePlayer
    {
        [Serializable]
        private sealed class Owner
        {
            public int schemaVersion;
            public string purpose;
            public string kind;
            public string path;
            public string workspaceId;
        }

        [Serializable]
        public sealed class Report
        {
            public string status;
            public string error;
            public string unityVersion;
            public string platform;
            public bool isEditor;
            public string runtimeAssembly;
            public string catalogDigest;
            public int pid;
            public int leaseCount;
            public int recordCount;
        }

        public static void BuildAndRun(string output, string assetRoot, string expectedRuntime, ResourceRelease release, Action<Scene> populate,
            string[] extraScriptingDefines = null)
        {
            Assert.That(Application.isBatchMode && !EditorApplication.isPlaying, Is.True);
            string project = Path.GetFullPath(Directory.GetParent(Application.dataPath).FullName);
            var host = JsonUtility.FromJson<Owner>(File.ReadAllText(Path.Combine(project, ".yl-owner.json")));
            Assert.That(host.schemaVersion, Is.EqualTo(1));
            Assert.That(host.purpose, Is.EqualTo("YLTesting.Directory"));
            Assert.That(host.kind, Is.EqualTo("Host"));
            Assert.That(Path.GetFullPath(host.path), Is.EqualTo(project).IgnoreCase);
            var ancestor = new DirectoryInfo(Path.GetFullPath(output));
            while (ancestor != null && !File.Exists(Path.Combine(ancestor.FullName, ".yl-owner.json")))
            {
                ancestor = ancestor.Parent;
            }
            Assert.That(ancestor, Is.Not.Null, "The caller must supply an existing owned result directory.");
            var resultOwner = JsonUtility.FromJson<Owner>(File.ReadAllText(Path.Combine(ancestor.FullName, ".yl-owner.json")));
            Assert.That(resultOwner.schemaVersion, Is.EqualTo(1));
            Assert.That(resultOwner.purpose, Is.EqualTo("YLTesting.Directory"));
            Assert.That(resultOwner.kind, Is.EqualTo("Result"));
            Assert.That(resultOwner.workspaceId, Is.EqualTo(host.workspaceId));
            Assert.That(Path.GetFullPath(resultOwner.path), Is.EqualTo(ancestor.FullName).IgnoreCase);
            Assert.That(assetRoot.StartsWith("Assets/__YL", StringComparison.Ordinal), Is.True);
            Assert.That(Directory.Exists(output), Is.True);
            string player = Path.Combine(project, "P", Guid.NewGuid().ToString("N").Substring(0, 8));
            Assert.That(Directory.Exists(player), Is.False);
            foreach (string path in new[] { player, output })
            {
                for (var parent = new DirectoryInfo(path); parent != null; parent = parent.Parent)
                {
                    if (parent.Exists)
                    {
                        Assert.That((parent.Attributes & FileAttributes.ReparsePoint) == 0, Is.True);
                    }
                }
            }
            Directory.CreateDirectory(player);
            string locator = Path.Combine(output, "standalone-player-location.txt");
            Assert.That(File.Exists(locator), Is.False);
            File.WriteAllText(locator, player);
            var deployed = ResourceBuiltInDeployer.Deploy(ResourceBuiltInDeployer.Preflight(release));
            Assert.That(deployed.Snapshot.Document.CatalogDigest, Is.EqualTo(release.Catalog.CatalogDigest));
            const string Empty = "Assets/YLTestingProfile/Empty.unity";
            Assert.That(File.Exists(PackageTestPaths.Physical(Empty)), Is.True);
            for (int index = 0; index < SceneManager.sceneCount; index++)
            {
                var scene = SceneManager.GetSceneAt(index);
                Assert.That(scene.isDirty, Is.False);
                Assert.That(scene.rootCount, Is.Zero);
                Assert.That(string.IsNullOrEmpty(scene.path) || scene.path == Empty, Is.True);
            }
            var target = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var backend = PlayerSettings.GetScriptingBackend(BuildTargetGroup.Standalone);
            string executable = Path.Combine(player, "P.exe");
            try
            {
                populate(target);
                string scene = assetRoot + "/Player.unity";
                Assert.That(EditorSceneManager.SaveScene(target, scene), Is.True);
                PlayerSettings.SetScriptingBackend(BuildTargetGroup.Standalone, ScriptingImplementation.Mono2x);
                var build = BuildPipeline.BuildPlayer(new BuildPlayerOptions
                {
                    scenes = new[] { scene },
                    locationPathName = executable,
                    target = BuildTarget.StandaloneWindows64,
                    options = BuildOptions.Development,
                    extraScriptingDefines = extraScriptingDefines ?? Array.Empty<string>()
                });
                Assert.That(build.summary.result, Is.EqualTo(UnityEditor.Build.Reporting.BuildResult.Succeeded));
            }
            finally
            {
                PlayerSettings.SetScriptingBackend(BuildTargetGroup.Standalone, backend);
                Assert.That(EditorSceneManager.OpenScene(Empty, OpenSceneMode.Single).path, Is.EqualTo(Empty));
            }
            string managed = Path.Combine(player, "P_Data/Managed");
            Assert.That(File.Exists(Path.Combine(managed, expectedRuntime + ".dll")), Is.True);
            Assert.That(File.Exists(Path.Combine(managed, expectedRuntime + ".Editor.dll")), Is.False);
            int previousPid = 0;
            for (int run = 1; run <= 2; run++)
            {
                string path = Path.Combine(player, "result-" + run + ".json");
                Assert.That(File.Exists(path), Is.False);
                using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(executable)
                {
                    UseShellExecute = false,
                    WorkingDirectory = player,
                    Arguments = "-batchmode -nographics -ylFeatureResult \"" + path + "\" -logFile \"" + Path.Combine(player, "player-" + run + ".log") + "\""
                });
                if (!process.WaitForExit(90000))
                {
                    process.Kill();
                    process.WaitForExit(10000);
                    Assert.Fail("The actual standalone Player timed out; it is not a successful exit.");
                }
                Assert.That(process.ExitCode, Is.Zero);
                var report = JsonUtility.FromJson<Report>(File.ReadAllText(path));
                Assert.That(report.status, Is.EqualTo("Passed"), report.error);
                Assert.That(report.pid, Is.EqualTo(process.Id).And.Not.EqualTo(previousPid));
                Assert.That(report.isEditor, Is.False);
                Assert.That(report.platform, Is.EqualTo("WindowsPlayer"));
                Assert.That(report.unityVersion, Is.EqualTo(Application.unityVersion));
                Assert.That(report.runtimeAssembly, Is.EqualTo(expectedRuntime));
                Assert.That(report.catalogDigest, Is.EqualTo(release.Catalog.CatalogDigest));
                Assert.That(report.leaseCount, Is.Zero);
                Assert.That(report.recordCount, Is.Zero);
                File.Copy(path, Path.Combine(output, "StandalonePlayer-" + run + ".json"));
                previousPid = process.Id;
            }
        }
    }
}
