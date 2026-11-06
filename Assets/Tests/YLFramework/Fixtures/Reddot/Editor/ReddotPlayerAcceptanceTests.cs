using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using YLFramework.RedDots.Acceptance;
using YLFramework.Resource;
using YLFramework.Testing.EditorFixtures;

namespace YLFramework.RedDots.Tests
{
    [Category("YL.Special.ReddotPlayer")]
    [Category("YL.Package.ReddotStandalone")]
    public sealed class ReddotPlayerAcceptanceTests
    {
        private const string Empty = "Assets/YLTestingProfile/Empty.unity";
        private static readonly string[] _assemblies = { "YLFramework.Runtime", "YLFramework.RedDots", "YLFramework.RedDots.Acceptance" };

        [Serializable]
        private sealed class OwnedScene
        {
            public int schemaVersion = 1;
            public string token;
            public string player;
            public string folder;
            public string folderGuid;
            public string folderMetaSha256;
            public string sceneGuid;
            public string sceneMetaSha256;
            public string sceneBeforeBuildSha256;
            public string sceneSha256;
        }

        [Test]
        public void TwoActualMonoPlayersExerciseOnlyMainAndReddotWithoutResourceOrSave()
        {
            Assert.That(Application.isBatchMode && !EditorApplication.isPlayingOrWillChangePlaymode &&
                !EditorApplication.isCompiling && !EditorApplication.isUpdating, Is.True);
            string output = global::YLFramework.Testing.Editor.YLTestEntry.RequireOwnedRunOutput();
            Assert.That(Directory.Exists(output), Is.True);
            Assert.That(Application.platform, Is.EqualTo(RuntimePlatform.WindowsEditor));
            Assert.That(EditorUserBuildSettings.activeBuildTarget, Is.EqualTo(BuildTarget.StandaloneWindows64));
            Assert.That(BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.Standalone, BuildTarget.StandaloneWindows64), Is.True);
            Assert.That(ResourceSystem.Current, Is.Null, "Never create or adopt a Resource authority for this feature.");
            CollectionAssert.AreEquivalent(new[] { "com.kano.ylframework", "com.kano.ylframework.reddot" },
                UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages()
                    .Where(package => package.name == "com.kano.ylframework" || package.name.StartsWith("com.kano.ylframework.", StringComparison.Ordinal))
                    .Select(package => package.name));
            RequireEmptyScenes();
            foreach (string name in new[] { "standalone-player-location.txt", "StandalonePlayer-1.json", "StandalonePlayer-2.json",
                "Reddot-owned-prepared.json", "Reddot-owned-built.json", "Reddot-owned-complete.json" })
            {
                Assert.That(File.Exists(Path.Combine(output, name)) || Directory.Exists(Path.Combine(output, name)), Is.False);
            }
            var run = new OwnedScene { token = Guid.NewGuid().ToString("N") };
            run.folder = "Assets/__YLReddot_" + run.token;
            NoLinks(run.folder);
            Assert.That(Directory.Exists(run.folder) || File.Exists(run.folder) || File.Exists(run.folder + ".meta"), Is.False);
            run.folderGuid = AssetDatabase.CreateFolder("Assets", run.folder.Substring("Assets/".Length));
            Assert.That(Guid.TryParseExact(run.folderGuid, "N", out _), Is.True);
            run.folderMetaSha256 = Hash(run.folder + ".meta");

            // ResourceRelease is explicitly null. All ownership, build and actual dual-PID checks stay in the shared helper.
            StandaloneFixturePlayer.BuildAndRun(output, run.folder, "YLFramework.RedDots", null, scene =>
            {
                run.player = Path.GetFullPath(File.ReadAllText(Path.Combine(output, "standalone-player-location.txt")));
                string project = Path.GetFullPath(Directory.GetParent(Application.dataPath).FullName);
                Assert.That(Path.GetDirectoryName(run.player), Is.EqualTo(Path.Combine(project, "P")).IgnoreCase);
                NoLinks(run.player);
                WriteNew(Path.Combine(run.player, ".reddot-owner.json"), JsonUtility.ToJson(new ReddotPlayerProbe.Owner
                {
                    token = run.token,
                    player = run.player
                }, true));
                var host = new GameObject("OwnedReddotPlayerProbe");
                host.SetActive(false);
                SceneManager.MoveGameObjectToScene(host, scene);
                var probe = host.AddComponent<ReddotPlayerProbe>();
                probe.runToken = run.token;
                var view = new GameObject("SubscribedView");
                view.transform.SetParent(host.transform, false);
                var indicator = new GameObject("ExplicitIndicator");
                indicator.transform.SetParent(view.transform, false);
                indicator.SetActive(false);
                probe.binding = view.AddComponent<Reddot>();
                probe.binding.Path = "UI/Mail";
                probe.binding.Indicator = indicator;
                host.SetActive(true);
                Assert.That(EditorSceneManager.SaveScene(scene, run.folder + "/Player.unity"), Is.True);
                run.sceneGuid = AssetDatabase.AssetPathToGUID(run.folder + "/Player.unity");
                Assert.That(Guid.TryParseExact(run.sceneGuid, "N", out _), Is.True);
                run.sceneMetaSha256 = Hash(run.folder + "/Player.unity.meta");
                run.sceneBeforeBuildSha256 = Hash(run.folder + "/Player.unity");
                WriteNew(Path.Combine(output, "Reddot-owned-prepared.json"), JsonUtility.ToJson(run, true));
            });
            string managed = Path.Combine(run.player, "P_Data/Managed");
            NoLinks(managed);
            CollectionAssert.AreEquivalent(_assemblies.Select(name => name + ".dll"),
                Directory.GetFiles(managed, "YLFramework*.dll").Select(Path.GetFileName), "The real Player must not force Save, Editor or test assemblies.");
            Assert.That(File.Exists(Path.Combine(managed, "nunit.framework.dll")), Is.False);
            Assert.That(Directory.GetFiles(managed, "UnityEditor*.dll"), Is.Empty);
            for (int round = 1; round <= 2; round++)
            {
                string log = Path.Combine(run.player, "player-" + round + ".log");
                NoLinks(log);
                File.Copy(log, Path.Combine(output, "ReddotPlayer-" + round + ".log"), false);
                string reportPath = Path.Combine(output, "StandalonePlayer-" + round + ".json");
                NoLinks(reportPath);
                var report = JObject.Parse(File.ReadAllText(reportPath), new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
                AssertReport(report, round, run.token);
            }
            RequireEmptyScenes();
            AssertOwnedAssets(run, false);
            var builtScene = EditorSceneManager.OpenScene(run.folder + "/Player.unity", OpenSceneMode.Additive);
            try
            {
                Assert.That(builtScene.rootCount, Is.EqualTo(1));
                var probe = builtScene.GetRootGameObjects().Single().GetComponent<ReddotPlayerProbe>();
                Assert.That((bool)probe && (bool)probe.binding && (bool)probe.binding.Indicator, Is.True);
                Assert.That(probe.runToken, Is.EqualTo(run.token));
                Assert.That(probe.binding.Path, Is.EqualTo("UI/Mail"));
                Assert.That(probe.binding.Indicator.transform.parent, Is.EqualTo(probe.binding.transform));
                Assert.That(builtScene.isDirty, Is.False, "Read-only verification must not alter the built scene.");
            }
            finally
            {
                Assert.That(EditorSceneManager.CloseScene(builtScene, true), Is.True);
            }
            // BuildPipeline may reserialize this one owned scene. Preserve the before/after hashes, never rebaseline its GUID/meta.
            run.sceneSha256 = Hash(run.folder + "/Player.unity");
            WriteNew(Path.Combine(output, "Reddot-owned-built.json"), JsonUtility.ToJson(run, true));
            AssertOwnedAssets(run, true);
            Assert.That(AssetDatabase.DeleteAsset(run.folder + "/Player.unity"), Is.True);
            Assert.That(Directory.GetFileSystemEntries(run.folder), Is.Empty);
            Assert.That(AssetDatabase.DeleteAsset(run.folder), Is.True);
            Assert.That(ResourceSystem.Current, Is.Null);
            WriteNew(Path.Combine(output, "Reddot-owned-complete.json"), JsonUtility.ToJson(run, true));
        }

        private static void AssertReport(JObject report, int round, string token)
        {
            foreach (var key in new[] { "status", "unityVersion", "platform", "backend", "runtimeAssembly", "probeAssembly", "token", "machine", "operatingSystem", "processor", "clrVersion" })
            {
                Assert.That(report[key]?.Type, Is.EqualTo(JTokenType.String), key);
                Assert.That(report.Value<string>(key), Is.Not.Empty, key);
            }
            Assert.That(report["catalogDigest"]?.Type == JTokenType.Null || report["catalogDigest"]?.Type == JTokenType.String, Is.True);
            Assert.That(report.Value<string>("catalogDigest"), Is.Null.Or.Empty);
            Assert.That(report.Value<string>("error"), Is.Null.Or.Empty);
            Assert.That(report.Value<string>("status"), Is.EqualTo("Passed"));
            Assert.That(report.Value<string>("unityVersion"), Is.EqualTo(Application.unityVersion));
            Assert.That(report.Value<string>("platform"), Is.EqualTo("WindowsPlayer"));
            Assert.That(report.Value<string>("backend"), Is.EqualTo("Mono"));
            Assert.That(report.Value<string>("runtimeAssembly"), Is.EqualTo("YLFramework.RedDots"));
            Assert.That(report.Value<string>("probeAssembly"), Is.EqualTo("YLFramework.RedDots.Acceptance"));
            Assert.That(report.Value<string>("token"), Is.EqualTo(token));
            foreach (var key in new[] { "isEditor", "resourceWasNull", "serializedIndicator", "indicatorLifecycle", "aggregationAndBatch", "readStateRoundTrip", "oldGenerationRejected" })
            {
                Assert.That(report[key]?.Type, Is.EqualTo(JTokenType.Boolean), key);
                Assert.That(report.Value<bool>(key), Is.EqualTo(key != "isEditor"), key);
            }
            foreach (var key in new[] { "schemaVersion", "pid", "round", "processorCount", "pointerBits", "generation", "captureSequence", "diagnosticNodes" })
            {
                Assert.That(report[key]?.Type, Is.EqualTo(JTokenType.Integer), key);
                Assert.That(report.Value<long>(key), Is.GreaterThan(0), key);
            }
            Assert.That(report.Value<int>("schemaVersion"), Is.EqualTo(1));
            Assert.That(report.Value<int>("round"), Is.EqualTo(round));
            Assert.That(report.Value<int>("pointerBits"), Is.EqualTo(64));
            foreach (var key in new[] { "leaseCount", "recordCount", "diagnosticSources", "diagnosticSubscribers", "dirtyNodeCount", "batchDepth" })
            {
                Assert.That(report[key]?.Type, Is.EqualTo(JTokenType.Integer), key);
                Assert.That(report.Value<int>(key), Is.Zero, key);
            }
            Assert.That(report["frameworkAssemblies"]?.Type, Is.EqualTo(JTokenType.Array));
            Assert.That(report["frameworkAssemblies"].All(item => item.Type == JTokenType.String), Is.True);
            CollectionAssert.AreEquivalent(_assemblies, report["frameworkAssemblies"].Values<string>());
            Assert.That(report["measurements"]?.Type, Is.EqualTo(JTokenType.Array));
            var measurements = (JArray)report["measurements"];
            Assert.That(measurements.Count, Is.EqualTo(2));
            for (int index = 0; index < 2; index++)
            {
                Assert.That(measurements[index], Is.InstanceOf<JObject>());
                var measurement = (JObject)measurements[index];
                Assert.That(measurement["batch"]?.Type, Is.EqualTo(JTokenType.Boolean));
                Assert.That(measurement.Value<bool>("batch"), Is.EqualTo(index == 1));
                Assert.That(measurement["allocationCounterReliable"]?.Type, Is.EqualTo(JTokenType.Boolean));
                Assert.That(measurement["allocationCalibrationBytes"]?.Type, Is.EqualTo(JTokenType.Integer));
                bool allocationCounterReliable = measurement.Value<bool>("allocationCounterReliable");
                long calibration = measurement.Value<long>("allocationCalibrationBytes");
                Assert.That(allocationCounterReliable, Is.EqualTo(calibration >= 65536), "Method presence cannot validate a Mono allocation counter.");
                foreach (var pair in new[] { ("groups", 4L), ("branchesPerGroup", 4L), ("leavesPerBranch", 4L), ("pathDepth", 4L),
                    ("sourcesPerLeaf", 3L), ("echoSources", 1L), ("warmIterations", 8L), ("iterations", 128L), ("updates", 24576L),
                    ("warmedReadPairs", 100000L), ("finalValue", 24577L), ("readChecksum", 76800000L), ("readAllocatedBytes", allocationCounterReliable ? 0L : -1L),
                    ("maximumCallbackDepth", 1L), ("diagnosticNodes", 86L), ("diagnosticSources", 193L), ("diagnosticSubscribers", 65L),
                    ("rootNotifications", index == 0 ? 24577L : 129L), ("globalNotifications", index == 0 ? 24577L : 129L),
                    ("leafNotifications", index == 0 ? 24576L : 8192L) })
                {
                    Assert.That(measurement[pair.Item1]?.Type, Is.EqualTo(JTokenType.Integer), pair.Item1);
                    Assert.That(measurement.Value<long>(pair.Item1), Is.EqualTo(pair.Item2), pair.Item1);
                }
                foreach (string key in new[] { "updateMilliseconds", "readMilliseconds", "diagnosticMilliseconds" })
                {
                    Assert.That(measurement[key]?.Type == JTokenType.Float || measurement[key]?.Type == JTokenType.Integer, Is.True, key);
                    var value = measurement.Value<double>(key);
                    Assert.That(double.IsNaN(value) || double.IsInfinity(value), Is.False, key);
                    Assert.That(value, Is.GreaterThanOrEqualTo(0), key);
                }
                foreach (string key in new[] { "updateAllocatedBytes", "diagnosticAllocatedBytes" })
                {
                    Assert.That(measurement[key]?.Type, Is.EqualTo(JTokenType.Integer), key);
                    if (allocationCounterReliable)
                    {
                        Assert.That(measurement.Value<long>(key), Is.GreaterThan(0), key);
                    }
                    else
                    {
                        Assert.That(measurement.Value<long>(key), Is.EqualTo(-1), "Unavailable allocation counters must never report a false zero.");
                    }
                }
            }
        }

        private static void RequireEmptyScenes()
        {
            for (var index = 0; index < SceneManager.sceneCount; index++)
            {
                var scene = SceneManager.GetSceneAt(index);
                Assert.That(scene.isDirty, Is.False, "Never save an unknown dirty scene.");
                Assert.That(scene.rootCount, Is.Zero);
                Assert.That(string.IsNullOrEmpty(scene.path) || scene.path == Empty, Is.True);
            }
        }

        private static void AssertOwnedAssets(OwnedScene run, bool includeSceneHash)
        {
            Assert.That(run.folder, Is.EqualTo("Assets/__YLReddot_" + run.token));
            NoLinks(run.folder);
            NoLinks(run.folder + ".meta");
            NoLinks(run.folder + "/Player.unity");
            NoLinks(run.folder + "/Player.unity.meta");
            Assert.That(AssetDatabase.AssetPathToGUID(run.folder), Is.EqualTo(run.folderGuid));
            Assert.That(Hash(run.folder + ".meta"), Is.EqualTo(run.folderMetaSha256));
            Assert.That(AssetDatabase.AssetPathToGUID(run.folder + "/Player.unity"), Is.EqualTo(run.sceneGuid));
            Assert.That(Hash(run.folder + "/Player.unity.meta"), Is.EqualTo(run.sceneMetaSha256));
            Assert.That(Directory.GetDirectories(run.folder), Is.Empty);
            CollectionAssert.AreEquivalent(new[] { "Player.unity", "Player.unity.meta" }, Directory.GetFiles(run.folder).Select(Path.GetFileName));
            if (includeSceneHash)
            {
                Assert.That(Hash(run.folder + "/Player.unity"), Is.EqualTo(run.sceneSha256));
            }
        }

        private static string Hash(string path)
        {
            using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(path))).Replace("-", string.Empty).ToLowerInvariant();
        }

        private static void WriteNew(string path, string text)
        {
            Assert.That(Directory.Exists(Path.GetDirectoryName(path)), Is.True);
            NoLinks(path);
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            writer.Write(text);
        }

        private static void NoLinks(string path)
        {
            if (File.Exists(path))
            {
                Assert.That((File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0, Is.True, path);
            }
            for (var parent = new DirectoryInfo(File.Exists(path) ? Path.GetDirectoryName(path) : path); parent != null; parent = parent.Parent)
            {
                if (parent.Exists)
                {
                    Assert.That((parent.Attributes & FileAttributes.ReparsePoint) == 0, Is.True, path);
                }
            }
        }
    }
}
