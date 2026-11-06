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
using YLFramework.Overlap.Acceptance;
using YLFramework.Resource;
using YLFramework.Testing.EditorFixtures;
using Object = UnityEngine.Object;

namespace YLFramework.Overlap.Tests
{
    [Category("YL.Special.OverlapPlayer")]
    public sealed class OverlapPlayerAcceptanceTests
    {
        private static readonly string[] _assemblies = { "YLFramework.Runtime", "YLFramework.Overlap", "YLFramework.Overlap.Acceptance" };

        [Serializable]
        private sealed class AssetEvidence
        {
            public string path;
            public string guid;
            public string sha256;
            public string metaSha256;
        }

        [Test]
        public void TwoActualMonoPlayersColdLoadAuthored2D3DAndMeasureNativeWorkloadsWithoutResource()
        {
            Assert.That(Application.isBatchMode && !EditorApplication.isPlayingOrWillChangePlaymode &&
                !EditorApplication.isCompiling && !EditorApplication.isUpdating, Is.True);
            string output = global::YLFramework.Testing.Editor.YLTestEntry.RequireOwnedRunOutput();
            Assert.That(ResourceSystem.Current, Is.Null);
            Assert.That(EditorUserBuildSettings.activeBuildTarget, Is.EqualTo(BuildTarget.StandaloneWindows64));
            Assert.That(BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.Standalone, BuildTarget.StandaloneWindows64), Is.True);
            CollectionAssert.AreEquivalent(new[] { "com.kano.ylframework", "com.kano.ylframework.overlap" },
                UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages().Where(package => package.name == "com.kano.ylframework" ||
                    package.name.StartsWith("com.kano.ylframework.", StringComparison.Ordinal)).Select(package => package.name));
            RequireEmpty();
            string token = Guid.NewGuid().ToString("N");
            string folder = "Assets/__YLOverlap_" + token;
            Assert.That(Directory.Exists(folder) || File.Exists(folder + ".meta"), Is.False);
            NoLinks(folder);
            string folderGuid = AssetDatabase.CreateFolder("Assets", folder.Substring(7));
            string folderMeta = Hash(folder + ".meta");
            string prefabPath = folder + "/Scenario.prefab";
            var author = new GameObject("AuthoredOverlap");
            GameObject prefab;
            try
            {
                var ignored = new GameObject("IgnoredRoot") { layer = 11 };
                ignored.transform.SetParent(author.transform, false);
                ignored.transform.localPosition = new Vector3(2, 0, 0);
                var ignored2D = ignored.AddComponent<BoxCollider2D>();
                var child3D = new GameObject("Ignored3D") { layer = 11 };
                child3D.transform.SetParent(ignored.transform, false);
                var ignored3D = child3D.AddComponent<BoxCollider>();
                var two = author.AddComponent<OverlapDetector2D>();
                two.Composite = new OverlapComposite2D(OverlapShape2D.Circle(new Vector2(2, 0), 1.4f), OverlapShape2D.Box(new Vector2(2, 0), Vector2.one * 2));
                two.Filter = new OverlapFilter2D(new ContactFilter2D
                {
                    useLayerMask = true,
                    layerMask = 1 << 11,
                    useDepth = true,
                    minDepth = -2,
                    maxDepth = 2
                }, ignored2D, ignored.transform, sortByDistance: true);
                two.Frequency = 17;
                two.ConfigureCapacity(2, 16, true);
                var three = author.AddComponent<OverlapDetector3D>();
                three.Composite = new OverlapComposite3D(OverlapShape3D.Sphere(new Vector3(2, 0, 0), 1.4f),
                    OverlapShape3D.Box(new Vector3(2, 0, 0), Vector3.one * 2, Quaternion.identity));
                three.Filter = new OverlapFilter3D(1 << 11, ignoredCollider: ignored3D, ignoredRoot: ignored.transform, sortByDistance: true);
                three.Frequency = 17;
                three.ConfigureCapacity(2, 16, true);
                var curved2D = new GameObject("Curved2D");
                curved2D.transform.SetParent(author.transform, false);
                curved2D.transform.localPosition = Vector3.up * 80;
                var newTwo = curved2D.AddComponent<OverlapDetector2D>();
                newTwo.Composite = new OverlapComposite2D(OverlapShape2D.Sector(new Vector2(-4, 0), 3, 120, 0.5f),
                    OverlapShape2D.Sector(new Vector2(4, 0), 3, 360, 1));
                newTwo.Filter = new OverlapFilter2D(new ContactFilter2D { useLayerMask = true, layerMask = 1 << 11 });
                newTwo.ConfigureCapacity(16, 16);
                var curved3D = new GameObject("Curved3D");
                curved3D.transform.SetParent(author.transform, false);
                curved3D.transform.localPosition = Vector3.up * 40;
                var newThree = curved3D.AddComponent<OverlapDetector3D>();
                newThree.Composite = new OverlapComposite3D(OverlapShape3D.Sector(new Vector3(-5, 0, 0), 3, 120, 2, 0.5f),
                    OverlapShape3D.Ring(new Vector3(3, 0, 0), 1, 3, 2), OverlapShape3D.Cone(new Vector3(10, 0, 0), 4, 2));
                newThree.Filter = new OverlapFilter3D(1 << 11);
                newThree.ConfigureCapacity(16, 16);
                newThree.ConfigureGeometryWorkspace(0.02f, 192, 192, 16);
                prefab = PrefabUtility.SaveAsPrefabAsset(author, prefabPath);
                Assert.That(prefab, Is.Not.Null);
            }
            finally
            {
                Object.DestroyImmediate(author);
            }
            var prefabEvidence = Evidence(prefabPath);
            AssetDatabase.ImportAsset(prefabPath, ImportAssetOptions.ForceUpdate);
            ColdReadPrefab(prefabPath);
            AssertEvidence(prefabEvidence);
            AssetEvidence sceneEvidence = null;
            string player = null;
            StandaloneFixturePlayer.BuildAndRun(output, folder, "YLFramework.Overlap", null, scene =>
            {
                player = Path.GetFullPath(File.ReadAllText(Path.Combine(output, "standalone-player-location.txt")));
                NoLinks(player);
                string project = Path.GetFullPath(Directory.GetParent(Application.dataPath).FullName);
                Assert.That(Path.GetDirectoryName(player), Is.EqualTo(Path.Combine(project, "P")).IgnoreCase);
                WriteNew(Path.Combine(player, ".overlap-owner.json"), JsonUtility.ToJson(new OverlapPlayerProbe.Owner { token = token, player = player }, true));
                var host = new GameObject("OwnedOverlapPlayerProbe");
                SceneManager.MoveGameObjectToScene(host, scene);
                var probe = host.AddComponent<OverlapPlayerProbe>();
                probe.scenarioPrefab = prefab;
                probe.runToken = token;
                Assert.That(EditorSceneManager.SaveScene(scene, folder + "/Player.unity"), Is.True);
                sceneEvidence = Evidence(folder + "/Player.unity");
                WriteNew(Path.Combine(output, "Overlap-assets-before.json"), JsonUtility.ToJson(new AssetPair { prefab = prefabEvidence, scene = sceneEvidence }, true));
            });
            CollectionAssert.AreEquivalent(_assemblies.Select(name => name + ".dll"),
                Directory.GetFiles(Path.Combine(player, "P_Data/Managed"), "YLFramework*.dll").Select(Path.GetFileName));
            Assert.That(File.Exists(Path.Combine(player, "P_Data/Managed/nunit.framework.dll")), Is.False);
            Assert.That(Directory.GetFiles(Path.Combine(player, "P_Data/Managed"), "UnityEditor*.dll"), Is.Empty);
            for (int round = 1; round <= 2; round++)
            {
                File.Copy(Path.Combine(player, "player-" + round + ".log"), Path.Combine(output, "OverlapPlayer-" + round + ".log"), false);
                var report = JObject.Parse(File.ReadAllText(Path.Combine(output, "StandalonePlayer-" + round + ".json")),
                    new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
                AssertReport(report, round, token);
            }
            RequireEmpty();
            ColdReadPrefab(prefabPath);
            var cold = EditorSceneManager.OpenScene(sceneEvidence.path, OpenSceneMode.Additive);
            try
            {
                Assert.That(cold.rootCount, Is.EqualTo(1));
                var probe = cold.GetRootGameObjects().Single().GetComponent<OverlapPlayerProbe>();
                Assert.That(probe.scenarioPrefab, Is.Not.Null);
                Assert.That(AssetDatabase.GetAssetPath(probe.scenarioPrefab), Is.EqualTo(prefabPath));
                Assert.That(AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(probe.scenarioPrefab)), Is.EqualTo(prefabEvidence.guid));
                Assert.That(probe.runToken, Is.EqualTo(token));
                Assert.That(cold.isDirty, Is.False);
            }
            finally
            {
                Assert.That(EditorSceneManager.CloseScene(cold, true), Is.True);
            }
            AssertEvidence(prefabEvidence);
            AssertEvidence(sceneEvidence);
            Assert.That(AssetDatabase.AssetPathToGUID(folder), Is.EqualTo(folderGuid));
            Assert.That(Hash(folder + ".meta"), Is.EqualTo(folderMeta));
            WriteNew(Path.Combine(output, "Overlap-assets-after.json"), JsonUtility.ToJson(new AssetPair { prefab = Evidence(prefabPath), scene = Evidence(sceneEvidence.path) }, true));
            Assert.That(ResourceSystem.Current, Is.Null);
            // Only this invocation's GUID- and byte-checked assets are removed. Failure paths remain intact.
            Assert.That(AssetDatabase.DeleteAsset(folder), Is.True);
        }

        [Serializable]
        private sealed class AssetPair
        {
            public AssetEvidence prefab;
            public AssetEvidence scene;
        }

        private static void AssertReport(JObject report, int round, string token)
        {
            foreach (string name in new[] { "status", "unityVersion", "platform", "backend", "runtimeAssembly", "probeAssembly", "token", "machine", "operatingSystem", "processor", "clrVersion" })
            {
                Assert.That(report[name]?.Type, Is.EqualTo(JTokenType.String), name);
                Assert.That(report.Value<string>(name), Is.Not.Empty, name);
            }
            Assert.That(report.Value<string>("status"), Is.EqualTo("Passed"));
            Assert.That(report.Value<string>("error"), Is.Null.Or.Empty);
            Assert.That(report.Value<string>("unityVersion"), Is.EqualTo(Application.unityVersion));
            Assert.That(report.Value<string>("platform"), Is.EqualTo("WindowsPlayer"));
            Assert.That(report.Value<string>("backend"), Is.EqualTo("Mono"));
            Assert.That(report.Value<string>("runtimeAssembly"), Is.EqualTo("YLFramework.Overlap"));
            Assert.That(report.Value<string>("probeAssembly"), Is.EqualTo("YLFramework.Overlap.Acceptance"));
            Assert.That(report.Value<string>("token"), Is.EqualTo(token));
            Assert.That(report.Value<bool>("inactiveConvexGate"), Is.False, "Original inactive route stays unsupported.");
            Assert.That(report.Value<bool>("isolatedConvexGate"), Is.EqualTo(Application.unityVersion.StartsWith("2022.3.", StringComparison.Ordinal)));
            Assert.That(report.Value<string>("selectedRoute"), Is.EqualTo("IsolatedSceneTargetProxy"));
            Assert.That(report.Value<int>("targetProxyComparisons"), Is.EqualTo(720));
            Assert.That(report.Value<bool>("fix1ConeBroad"), Is.True);
            Assert.That(report.Value<int>("fix1ConeComparisons"), Is.EqualTo(16));
            Assert.That(report.Value<int>("fix1OldMidpointMisses"), Is.EqualTo(2));
            Assert.That(report.Value<int>("targetProxyPositives"), Is.GreaterThan(0));
            Assert.That(report.Value<int>("targetProxyNegatives"), Is.GreaterThan(0));
            Assert.That(report.Value<float>("targetProxyMaxClosestDelta"), Is.LessThanOrEqualTo(0.00001f));
            foreach (string name in new[] { "resourceWasNull", "serializedPrefab", "queriesAndCasts2D", "queriesAndCasts3D", "partialNoFalseExit", "boundedUnion", "stopPool", "callbackInvalidation", "fakeNull", "legacyBoxSphere", "triggerPrerequisite", "isolatedScenesReleased", "targetProxyGate", "targetProxyScenesReleased", "targetProxyWorldIsolation", "targetProxyNoContactSimulation", "serializedCurvedGeometry", "sectorRing2D", "sectorRingCone3D", "curvedUnsupportedAndPartial", "curvedWorkspaceRelease" })
            {
                Assert.That(report[name]?.Type, Is.EqualTo(JTokenType.Boolean), name);
                Assert.That(report.Value<bool>(name), Is.True, name);
            }
            Assert.That(report["isEditor"]?.Type, Is.EqualTo(JTokenType.Boolean));
            Assert.That(report.Value<bool>("isEditor"), Is.False);
            foreach (var pair in new[] { ("schemaVersion", 1), ("pointerBits", 64), ("round", round), ("leaseCount", 0), ("recordCount", 0), ("convexGatePositives", 0), ("convexGateContacts", 0) })
            {
                Assert.That(report[pair.Item1]?.Type, Is.EqualTo(JTokenType.Integer), pair.Item1);
                Assert.That(report.Value<int>(pair.Item1), Is.EqualTo(pair.Item2), pair.Item1);
            }
            Assert.That(report["catalogDigest"]?.Type, Is.EqualTo(JTokenType.String));
            Assert.That(report.Value<string>("catalogDigest"), Is.Empty);
            Assert.That(report["frameworkAssemblies"]?.Type, Is.EqualTo(JTokenType.Array));
            Assert.That(report["frameworkAssemblies"].All(item => item.Type == JTokenType.String), Is.True);
            CollectionAssert.AreEquivalent(_assemblies, report["frameworkAssemblies"].Values<string>());
            Assert.That(report["measurements"]?.Type, Is.EqualTo(JTokenType.Array));
            var measurements = (JArray)report["measurements"];
            Assert.That(measurements.Count, Is.EqualTo(8), "Both dimensions, two actual dense sizes, unique and repeated/grouped workloads are required.");
            for (int index = 0; index < 8; index++)
            {
                var value = (JObject)measurements[index];
                int density = index % 4 < 2 ? 64 : 256;
                bool repeated = index % 2 == 1;
                int contacts = repeated ? density / 2 - 1 : density;
                foreach (var pair in new[] { ("density", density), ("unionChildren", repeated ? 4 : 1), ("ignoredBodies", repeated ? 1 : 0),
                    ("initialCapacity", 8), ("maxCapacity", density * 2), ("actualCapacity", density * 2), ("iterations", 65), ("warmIterations", 8),
                    ("finalContacts", contacts), ("finalRawCount", density * (repeated ? 4 : 1)), ("entered", contacts * 5),
                    ("stayed", contacts * 28), ("exited", contacts * 5), ("contactsAfterStop", 0), ("layerMask", 1 << 11) })
                {
                    Assert.That(value[pair.Item1]?.Type, Is.EqualTo(JTokenType.Integer), pair.Item1);
                    Assert.That(value.Value<int>(pair.Item1), Is.EqualTo(pair.Item2), pair.Item1);
                }
                foreach (var pair in new[] { ("threeD", index >= 4), ("grouping", repeated), ("sorting", repeated), ("includeTriggers", true) })
                {
                    Assert.That(value[pair.Item1]?.Type, Is.EqualTo(JTokenType.Boolean));
                    Assert.That(value.Value<bool>(pair.Item1), Is.EqualTo(pair.Item2));
                }
                Assert.That(value["allocationSupported"]?.Type, Is.EqualTo(JTokenType.Boolean));
                Assert.That(value["calibrationBytes"]?.Type, Is.EqualTo(JTokenType.Integer));
                Assert.That(value["allocatedBytes"]?.Type, Is.EqualTo(JTokenType.Integer));
                bool supported = value.Value<bool>("allocationSupported");
                Assert.That(supported, Is.EqualTo(value.Value<long>("calibrationBytes") >= 65536));
                Assert.That(value.Value<long>("allocatedBytes"), supported ? Is.GreaterThanOrEqualTo(0) : Is.EqualTo(-1));
                Assert.That(value["milliseconds"]?.Type == JTokenType.Float || value["milliseconds"]?.Type == JTokenType.Integer, Is.True);
                double milliseconds = value.Value<double>("milliseconds");
                Assert.That(double.IsNaN(milliseconds) || double.IsInfinity(milliseconds), Is.False);
                Assert.That(milliseconds, Is.GreaterThanOrEqualTo(0));
            }
            var curved = report["curvedMeasurements"] as JArray;
            Assert.That(curved, Has.Count.EqualTo(2));
            foreach (JObject value in curved)
            {
                Assert.That(value.Value<int>("segments"), Is.GreaterThan(0));
                Assert.That(value.Value<int>("parts"), Is.GreaterThan(0));
                Assert.That(value.Value<int>("cooks"), Is.EqualTo(value.Value<int>("parts")));
                Assert.That(value.Value<int>("steadyAdditionalCooks"), Is.Zero);
                Assert.That(value.Value<int>("copiedTargets"), Is.GreaterThanOrEqualTo(100));
                Assert.That(value.Value<int>("sceneCountAfterConstruct"), Is.EqualTo(value.Value<int>("sceneCountBeforeConstruct") + 1));
                Assert.That(value.Value<float>("generatedError"), Is.LessThanOrEqualTo(value.Value<float>("requestedError")));
                foreach (string timing in new[] { "constructMilliseconds", "buildMilliseconds", "steady100Milliseconds" })
                {
                    double ms = value.Value<double>(timing);
                    Assert.That(double.IsNaN(ms) || double.IsInfinity(ms) || ms < 0, Is.False);
                }
                bool supported = value.Value<bool>("allocationSupported");
                Assert.That(supported, Is.EqualTo(value.Value<long>("calibrationBytes") >= 65536));
                Assert.That(value.Value<long>("allocatedBytes"), supported ? Is.GreaterThanOrEqualTo(0) : Is.EqualTo(-1));
            }
        }

        private static void ColdReadPrefab(string path)
        {
            var loaded = PrefabUtility.LoadPrefabContents(path);
            try
            {
                var two = loaded.GetComponent<OverlapDetector2D>();
                var three = loaded.GetComponent<OverlapDetector3D>();
                Assert.That(two.Composite.GetShape(0).Center, Is.EqualTo(new Vector2(2, 0)));
                Assert.That(three.Composite.GetShape(0).Center, Is.EqualTo(new Vector3(2, 0, 0)));
                Assert.That(two.Filter.ContactFilter.useDepth && two.Filter.SortByDistance, Is.True);
                Assert.That(two.Filter.ContactFilter.minDepth, Is.EqualTo(-2));
                Assert.That(two.Filter.ContactFilter.maxDepth, Is.EqualTo(2));
                Assert.That(three.Filter.TriggerInteraction, Is.EqualTo(QueryTriggerInteraction.Ignore));
                Assert.That(two.Filter.IgnoredRoot, Is.SameAs(loaded.transform.GetChild(0)));
                Assert.That(three.Filter.IgnoredCollider, Is.SameAs(loaded.transform.GetChild(0).GetComponentInChildren<Collider>()));
                Assert.That(two.BufferCapacity + three.BufferCapacity, Is.Zero, "Cold read must not query or create detector buffers.");
                var newTwo = loaded.transform.Find("Curved2D").GetComponent<OverlapDetector2D>();
                var newThree = loaded.transform.Find("Curved3D").GetComponent<OverlapDetector3D>();
                Assert.That(newTwo.Composite.Count, Is.EqualTo(2));
                Assert.That(newTwo.Composite.GetShape(0).InnerRadius, Is.EqualTo(0.5f));
                Assert.That(newTwo.Composite.GetShape(1).AngleDegrees, Is.EqualTo(360));
                Assert.That(newThree.Composite.Count, Is.EqualTo(3));
                Assert.That(newThree.Composite.GetShape(0).Height, Is.EqualTo(2));
                Assert.That(newThree.Composite.GetShape(1).InnerRadius, Is.EqualTo(1));
                Assert.That(newThree.Composite.GetShape(2).Kind, Is.EqualTo(OverlapShapeKind3D.Cone));
                Assert.That(newThree.Composite.GetShape(2).Height, Is.EqualTo(4));
                Assert.That(newThree.RequestedBoundaryError, Is.EqualTo(0.02f));
                Assert.That(newThree.MaxSegments, Is.EqualTo(192));
                Assert.That(newThree.MaxParts, Is.EqualTo(192));
                Assert.That(newThree.MaxHierarchyDepth, Is.EqualTo(16));
                Assert.That(newTwo.BufferCapacity + newThree.BufferCapacity, Is.Zero);
                Assert.That(newThree.HasGeometryWorkspace, Is.False, "Cold prefab inspection must not create a private scene.");
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(loaded);
            }
        }

        private static void RequireEmpty()
        {
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                Assert.That(scene.isDirty, Is.False);
                Assert.That(scene.rootCount, Is.Zero);
                Assert.That(string.IsNullOrEmpty(scene.path) || scene.path == "Assets/YLTestingProfile/Empty.unity", Is.True);
            }
        }

        private static AssetEvidence Evidence(string path) => new AssetEvidence
        {
            path = path,
            guid = AssetDatabase.AssetPathToGUID(path),
            sha256 = Hash(path),
            metaSha256 = Hash(path + ".meta")
        };

        private static void AssertEvidence(AssetEvidence value)
        {
            NoLinks(value.path);
            NoLinks(value.path + ".meta");
            Assert.That(Guid.TryParseExact(value.guid, "N", out _), Is.True);
            Assert.That(AssetDatabase.AssetPathToGUID(value.path), Is.EqualTo(value.guid));
            Assert.That(Hash(value.path), Is.EqualTo(value.sha256), "Build/cold reopen must not rewrite authored bytes.");
            Assert.That(Hash(value.path + ".meta"), Is.EqualTo(value.metaSha256));
        }

        private static string Hash(string path)
        {
            using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(path))).Replace("-", string.Empty).ToLowerInvariant();
        }

        private static void WriteNew(string path, string content)
        {
            Assert.That(Directory.Exists(Path.GetDirectoryName(path)), Is.True);
            NoLinks(path);
            using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            using var writer = new StreamWriter(file, new UTF8Encoding(false));
            writer.Write(content);
        }

        private static void NoLinks(string path)
        {
            if (File.Exists(path))
                Assert.That((File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0, Is.True);
            for (var parent = new DirectoryInfo(File.Exists(path) ? Path.GetDirectoryName(path) : path); parent != null; parent = parent.Parent)
            {
                if (parent.Exists)
                    Assert.That((parent.Attributes & FileAttributes.ReparsePoint) == 0, Is.True);
            }
        }
    }
}
