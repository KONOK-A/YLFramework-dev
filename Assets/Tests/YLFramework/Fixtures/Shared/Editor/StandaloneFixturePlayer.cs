using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AddressableAssets;
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
            public int schemaVersion;
            public string status;
            public string error;
            public string unityVersion;
            public string platform;
            public bool isEditor;
            public string runtimeAssembly;
            public string catalogDigest;
            public string contentHash;
            public bool ledgerMeasured;
            public string resourceState;
            public int pid;
            public int leaseCount;
            public int recordCount;
            public int scopeCount;
            public int requestCount;
            public int pendingWorkCount;
            public int drainingHandleCount;
        }

        public static void BuildAndRun(string output, string assetRoot, string expectedRuntime, ResourceEditorBuildReport content, Action<Scene> populate,
            string[] extraScriptingDefines = null, bool graphics = false,
            ScriptingImplementation scriptingBackend = ScriptingImplementation.Mono2x, bool developmentBuild = true)
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
            if (content != null)
            {
                ResourceEditorOperations.ValidateBuild(AddressableAssetSettingsDefaultObject.GetSettings(false));
            }
            var contentFiles = content == null ? null : ReadContent(content);
            var contentHash = contentFiles == null ? null : HashContent(contentFiles);
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
                PlayerSettings.SetScriptingBackend(BuildTargetGroup.Standalone, scriptingBackend);
                var build = BuildPipeline.BuildPlayer(new BuildPlayerOptions
                {
                    scenes = new[] { scene },
                    locationPathName = executable,
                    target = BuildTarget.StandaloneWindows64,
                    options = developmentBuild ? BuildOptions.Development : BuildOptions.None,
                    extraScriptingDefines = extraScriptingDefines ?? Array.Empty<string>()
                });
                Assert.That(build.summary.result, Is.EqualTo(UnityEditor.Build.Reporting.BuildResult.Succeeded));
            }
            finally
            {
                PlayerSettings.SetScriptingBackend(BuildTargetGroup.Standalone, backend);
                Assert.That(EditorSceneManager.OpenScene(Empty, OpenSceneMode.Single).path, Is.EqualTo(Empty));
            }
            if (contentFiles != null)
            {
                CollectionAssert.AreEquivalent(contentFiles, ContentFiles(Path.Combine(player, "P_Data/StreamingAssets/aa")),
                    "The Player must contain the exact official AA bytes that were built and frozen.");
                CollectionAssert.AreEquivalent(contentFiles, ReadContent(content), "Official content changed during the Player build.");
            }
            string managed = Path.Combine(player, "P_Data/Managed");
            if (scriptingBackend == ScriptingImplementation.Mono2x)
            {
                Assert.That(File.Exists(Path.Combine(managed, expectedRuntime + ".dll")), Is.True);
                Assert.That(File.Exists(Path.Combine(managed, expectedRuntime + ".Editor.dll")), Is.False);
            }
            else
            {
                Assert.That(scriptingBackend, Is.EqualTo(ScriptingImplementation.IL2CPP));
                Assert.That(File.Exists(Path.Combine(player, "GameAssembly.dll")), Is.True);
                Assert.That(Directory.Exists(Path.Combine(player, "P_Data/il2cpp_data")), Is.True);
            }
            int previousPid = 0;
            for (int run = 1; run <= 2; run++)
            {
                string path = Path.Combine(player, "result-" + run + ".json");
                Assert.That(File.Exists(path), Is.False);
                using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(executable)
                {
                    UseShellExecute = false,
                    WorkingDirectory = player,
                    Arguments = "-batchmode " + (graphics ? "" : "-nographics ") + "-ylFeatureResult \"" + path + "\" -logFile \"" + Path.Combine(player, "player-" + run + ".log") + "\""
                });
                bool exited = process.WaitForExit(90000);
                if (!exited)
                {
                    process.Kill();
                    Assert.That(process.WaitForExit(10000), Is.True, "The timed out Player did not terminate after cleanup.");
                }
                foreach (string extension in new[] { ".json", ".log" })
                {
                    string artifact = extension == ".json" ? path : Path.Combine(player, "player-" + run + extension);
                    if (!File.Exists(artifact))
                    {
                        continue;
                    }
                    foreach (string check in new[] { artifact, output })
                    {
                        for (string cursor = Path.GetFullPath(check); cursor != null; cursor = Path.GetDirectoryName(cursor))
                        {
                            Assert.That((File.GetAttributes(cursor) & FileAttributes.ReparsePoint) == 0, Is.True,
                                "Player evidence cannot be archived through linked paths.");
                        }
                    }
                    File.Copy(artifact, Path.Combine(output, "StandalonePlayer-" + run + extension));
                }
                Assert.That(exited, Is.True, "The actual standalone Player timed out; it is not a successful exit.");
                Assert.That(process.ExitCode, Is.Zero);
                var report = JsonUtility.FromJson<Report>(File.ReadAllText(path));
                Assert.That(report.status, Is.EqualTo("Passed"), report.error);
                Assert.That(report.pid, Is.EqualTo(process.Id).And.Not.EqualTo(previousPid));
                Assert.That(report.isEditor, Is.False);
                Assert.That(report.platform, Is.EqualTo("WindowsPlayer"));
                Assert.That(report.unityVersion, Is.EqualTo(Application.unityVersion));
                Assert.That(report.runtimeAssembly, Is.EqualTo(expectedRuntime));
                Assert.That(report.catalogDigest, Is.Null.Or.Empty, "No private publication digest is accepted.");
                if (content == null)
                {
                    Assert.That(report.recordCount, Is.Zero, "The existing resource-free process result contract is unchanged.");
                }
                else
                {
                    Assert.That(report.schemaVersion, Is.EqualTo(2));
                    Assert.That(report.contentHash, Is.EqualTo(contentHash));
                    Assert.That(report.ledgerMeasured, Is.True);
                    Assert.That(report.resourceState, Is.EqualTo("Closed"));
                    Assert.That(report.scopeCount, Is.Zero);
                    Assert.That(report.requestCount, Is.Zero);
                    Assert.That(report.pendingWorkCount, Is.Zero);
                    Assert.That(report.drainingHandleCount, Is.Zero);
                }
                Assert.That(report.leaseCount, Is.Zero);
                previousPid = process.Id;
            }
        }

        /// <summary>Fingerprint actual official local content, not a private publication protocol.</summary>
        public static string ContentHash(ResourceEditorBuildReport content) => HashContent(ReadContent(content));

        private static Dictionary<string, string> ReadContent(ResourceEditorBuildReport content)
        {
            Assert.That(content, Is.Not.Null);
            var settings = AddressableAssetSettingsDefaultObject.GetSettings(false);
            Assert.That(settings, Is.Not.Null);
            Assert.That(content.Target, Is.EqualTo(BuildTarget.StandaloneWindows64));
            Assert.That(content.IsContentUpdate, Is.False, "A new fixture Player needs a complete official content build.");
            Assert.That(content.SettingsGuid, Is.EqualTo(AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(settings))));
            Assert.That(content.ProfileId, Is.EqualTo(settings.activeProfileId));
            Assert.That(settings.BuildAddressablesWithPlayerBuild, Is.EqualTo(AddressableAssetSettings.PlayerBuildOption.DoNotBuildWithPlayer),
                "Explicitly disable a second automatic content build before freezing the Player's content.");
            Assert.That(content.Result, Is.Not.Null);
            Assert.That(content.Result.Error, Is.Null.Or.Empty);
            Assert.That(File.Exists(content.Result.OutputPath), Is.True);
            Assert.That(File.Exists(content.ContentStatePath), Is.True);
            return ContentFiles(Path.GetFullPath(Addressables.BuildPath));
        }

        private static Dictionary<string, string> ContentFiles(string directory)
        {
            Assert.That(Directory.Exists(directory), Is.True, directory);
            var result = Directory.GetFiles(directory, "*", SearchOption.AllDirectories).ToDictionary(
                path => path.Substring(directory.TrimEnd('/', '\\').Length + 1).Replace('\\', '/'),
                path =>
                {
                    for (var cursor = path; cursor != null; cursor = Path.GetDirectoryName(cursor))
                    {
                        Assert.That((File.GetAttributes(cursor) & FileAttributes.ReparsePoint) == 0, Is.True);
                    }
                    using var stream = File.OpenRead(path);
                    using var sha = SHA256.Create();
                    return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
                }, StringComparer.Ordinal);
            Assert.That(result.Count, Is.GreaterThan(0));
            Assert.That(result.Keys.Any(path => path.EndsWith("settings.json", StringComparison.Ordinal)), Is.True);
            return result;
        }

        private static string HashContent(Dictionary<string, string> files)
        {
            using var algorithm = SHA256.Create();
            var bytes = Encoding.UTF8.GetBytes(string.Join("\n", files.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => pair.Key + "=" + pair.Value)));
            return BitConverter.ToString(algorithm.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }
    }
}
