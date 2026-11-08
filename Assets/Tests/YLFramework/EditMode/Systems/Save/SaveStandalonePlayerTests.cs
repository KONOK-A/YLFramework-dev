using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace YLFramework.Tests.SavePackage
{
    public class SaveStandalonePlayerTests
    {
        [Serializable]
        private sealed class Report
        {
            public string status;
            public string error;
            public string platform;
            public bool isEditor;
            public string unityVersion;
            public int pid;
            public int run;
            public string runtimeAssembly;
            public bool mainAbsent;
            public bool coldReopened;
            public bool backupRecovered;
            public bool isIL2CPP;
            public bool developmentBuild;
        }

        [TestCase(ScriptingImplementation.Mono2x)]
        [TestCase(ScriptingImplementation.IL2CPP)]
        [Category("YL.Package.SaveStandalone")]
        public void NoMainPackagePlayerPersistsAcrossProcessesAndExplicitlyRecoversBackup(ScriptingImplementation implementation)
        {
            Assert.That(Application.isBatchMode && !EditorApplication.isPlaying, Is.True);
            string output = Path.Combine(global::YLFramework.Testing.Editor.YLTestEntry.RequireOwnedRunOutput(), "SavePlayer-" + implementation);
            Assert.That(Directory.Exists(output), Is.False);
            Directory.CreateDirectory(output);
            string project = Directory.GetParent(Application.dataPath).FullName;
            Assert.That(File.Exists(Path.Combine(project, ".yl-owner.json")), Is.True);
            string player = Path.Combine(project, "P", Guid.NewGuid().ToString("N").Substring(0, 8));
            Assert.That(Directory.Exists(player), Is.False);
            for (var parent = new DirectoryInfo(player); parent != null; parent = parent.Parent)
            {
                if (parent.Exists)
                {
                    Assert.That((parent.Attributes & FileAttributes.ReparsePoint) == 0, Is.True);
                }
            }
            Directory.CreateDirectory(player);
            string locator = Path.Combine(output, "save-player-location.txt");
            Assert.That(File.Exists(locator), Is.False);
            File.WriteAllText(locator, player);
            string folder = "Assets/__YLSaveStandalone_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            AssetDatabase.CreateFolder("Assets", folder.Substring(7));
            for (int index = 0; index < SceneManager.sceneCount; index++)
            {
                var empty = SceneManager.GetSceneAt(index);
                Assert.That(empty.isDirty, Is.False);
                if (empty.rootCount != 0)
                {
                    Assert.That(string.IsNullOrEmpty(empty.path), Is.True);
                    var defaults = empty.GetRootGameObjects();
                    Assert.That(defaults.Select(root => root.name), Is.EquivalentTo(new[] { "Main Camera", "Directional Light" }),
                        "Only the fresh Unity factory scene, never an arbitrary populated scene, may be replaced in this owned host.");
                    foreach (var root in defaults)
                    {
                        var components = root.GetComponents<Component>().Select(component => component.GetType()).ToArray();
                        Assert.That(components, root.name == "Main Camera"
                            ? Is.EquivalentTo(new[] { typeof(Transform), typeof(Camera), typeof(AudioListener) })
                            : Is.EquivalentTo(new[] { typeof(Transform), typeof(Light) }));
                    }
                }
                Assert.That(string.IsNullOrEmpty(empty.path) || empty.path == "Assets/YLTestingProfile/Empty.unity", Is.True);
            }
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var backend = PlayerSettings.GetScriptingBackend(BuildTargetGroup.Standalone);
            var stripping = PlayerSettings.GetManagedStrippingLevel(BuildTargetGroup.Standalone);
            string executable = Path.Combine(player, "P.exe");
            try
            {
                var type = Type.GetType("YLFramework.Tests.SavePackage.SavePlayerProbe, YLFramework.Save.Acceptance", true);
                new GameObject("StandaloneSaveProbe").AddComponent(type);
                string scenePath = folder + "/Player.unity";
                Assert.That(EditorSceneManager.SaveScene(scene, scenePath), Is.True);
                PlayerSettings.SetScriptingBackend(BuildTargetGroup.Standalone, implementation);
                if (implementation == ScriptingImplementation.IL2CPP)
                {
                    PlayerSettings.SetManagedStrippingLevel(BuildTargetGroup.Standalone, ManagedStrippingLevel.Medium);
                }
                var build = BuildPipeline.BuildPlayer(new BuildPlayerOptions
                {
                    scenes = new[] { scenePath },
                    locationPathName = executable,
                    target = BuildTarget.StandaloneWindows64,
                    options = BuildOptions.Development,
                    extraScriptingDefines = new[] { "YL_SAVE_ACCEPTANCE" }
                });
                Assert.That(build.summary.result, Is.EqualTo(UnityEditor.Build.Reporting.BuildResult.Succeeded));
            }
            finally
            {
                PlayerSettings.SetScriptingBackend(BuildTargetGroup.Standalone, backend);
                PlayerSettings.SetManagedStrippingLevel(BuildTargetGroup.Standalone, stripping);
                const string Empty = "Assets/YLTestingProfile/Empty.unity";
                if (File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(Empty)))
                {
                    EditorSceneManager.OpenScene(Empty, OpenSceneMode.Single);
                }
                else
                {
                    EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                }
            }
            string managed = Path.Combine(player, "P_Data/Managed");
            if (implementation == ScriptingImplementation.Mono2x)
            {
                Assert.That(File.Exists(Path.Combine(managed, "YLFramework.Save.dll")), Is.True);
            }
            else
            {
                Assert.That(File.Exists(Path.Combine(player, "GameAssembly.dll")), Is.True);
            }
            Assert.That(File.Exists(Path.Combine(managed, "YLFramework.Runtime.dll")), Is.False);
            int previousPid = 0;
            for (int run = 1; run <= 2; run++)
            {
                string path = Path.Combine(player, "result-" + run + ".json");
                Assert.That(File.Exists(path), Is.False);
                using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(executable)
                {
                    UseShellExecute = false,
                    WorkingDirectory = player,
                    Arguments = "-batchmode -nographics -ylSaveResult \"" + path + "\" -logFile \"" + Path.Combine(player, "player-" + run + ".log") + "\""
                });
                if (!process.WaitForExit(90000))
                {
                    process.Kill();
                    process.WaitForExit(10000);
                    Assert.Fail("Save Player timed out; no cold reopen success is inferred.");
                }
                Assert.That(process.ExitCode, Is.Zero);
                var report = JsonUtility.FromJson<Report>(File.ReadAllText(path));
                Assert.That(report.status, Is.EqualTo("Passed"), report.error);
                Assert.That(report.pid, Is.EqualTo(process.Id).And.Not.EqualTo(previousPid));
                Assert.That(report.run, Is.EqualTo(run));
                Assert.That(report.isEditor, Is.False);
                Assert.That(report.platform, Is.EqualTo("WindowsPlayer"));
                Assert.That(report.unityVersion, Is.EqualTo(Application.unityVersion));
                Assert.That(report.runtimeAssembly, Is.EqualTo("YLFramework.Save"));
                Assert.That(report.mainAbsent && report.coldReopened, Is.True);
                Assert.That(report.isIL2CPP, Is.EqualTo(implementation == ScriptingImplementation.IL2CPP));
                Assert.That(report.developmentBuild, Is.True);
                if (run == 2)
                {
                    Assert.That(report.backupRecovered, Is.True);
                }
                File.Copy(path, Path.Combine(output, "SavePlayer-" + run + ".json"));
                previousPid = process.Id;
            }
            Assert.That(AssetDatabase.DeleteAsset(folder), Is.True);
        }
    }
}
