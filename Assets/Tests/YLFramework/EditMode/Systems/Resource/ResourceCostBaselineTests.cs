using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;
using YLFramework.Editor.Resource;
using YLFramework.Resource;
using YLFramework.Tests.Resource;
using Object = UnityEngine.Object;

namespace YLFramework.Tests.ResourceBuild
{
    // Run the whole fixture through ResourceCostEvidence.Run; B/C consume A's exact exported releases.
    public sealed partial class ResourceCostBaselineTests
    {
        private const string StateKey = "YL.Resource.F03.Baseline";
        private const string AppId = "f03-v1";
        private const string Platform = "StandaloneWindows64";
        private const long MiB = 1024L * 1024;
        private const int Samples = 3;
        private const double TimeoutSeconds = 180;
        private static readonly string[] _modes = { "None", "Encrypted" };
        private static ResourceStartup _activeStartup;
        private static ResourceSystem _lastSystem;

        [Serializable]
        private sealed class FixtureState
        {
            public string Run;
            public string Root;
            public string CacheRoot;
            public string Token;
            public string Folder;
            public string FolderGuid;
            public string ProjectId;
            public string ProjectGuid;
            public string Scene;
            public string Bootstrap;
            public string None;
            public string Encrypted;
            public string NoneText;
            public string EncryptedText;
            public SceneSetup[] Scenes;
            public List<string> Workspaces = new();
            public List<string> CacheStores = new();
            public bool FolderCreated;
            public bool SceneCreated;
            public bool Ready;
            public bool BuildsComplete;
            public bool NetworkComplete;
            public bool BuildFailed;
        }

        private static FixtureState State => JsonUtility.FromJson<FixtureState>(SessionState.GetString(StateKey, "{}"));
        private static void Save(FixtureState state) => SessionState.SetString(StateKey, JsonUtility.ToJson(state));
        private static string Content(FixtureState state) => state.Folder + "/Content";
        private static ResourceProject Project(FixtureState state) => AssetDatabase.LoadAssetAtPath<ResourceProject>(state.Folder + "/Project.asset");
        private static ResourceBuildRecipe Recipe(FixtureState state) => AssetDatabase.LoadAssetAtPath<ResourceBuildRecipe>(state.Folder + "/Recipe.asset");
        private static double Milliseconds(long start, long end) => (end - start) * 1000.0 / Stopwatch.Frequency;
        private static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

        [OneTimeSetUp]
        public void CreateOnlyOwnedInputsAndDefinitions()
        {
            var previous = State;
            if (previous.Ready)
            {
                Assert.That(previous.Run, Is.EqualTo(ResourceCostEvidence.DirectoryPath), "A leftover fixture must not be adopted by a different run.");
                return;
            }
            Assert.That(string.IsNullOrEmpty(previous.Root), Is.True, "An incomplete previous fixture requires explicit diagnosis, not an automatic retry.");
            Assert.That(ResourceCostEvidence.DirectoryPath, Is.Not.Empty, "Use ResourceCostEvidence.Run with the whole fixture name.");
            Assert.That(Application.platform, Is.EqualTo(RuntimePlatform.WindowsEditor));
            Assert.That(EditorUserBuildSettings.activeBuildTarget, Is.EqualTo(BuildTarget.StandaloneWindows64));
            Assert.That(EditorSettings.enterPlayModeOptionsEnabled, Is.False, "Do not change the user's domain reload settings.");
            Assert.That(ResourceSystem.Current, Is.Null);
            Assert.That(AssetBundle.GetAllLoadedAssetBundles(), Is.Empty);
            ResourceSessionTests.EnsureRestorableScenes();
            var state = new FixtureState
            {
                Run = ResourceCostEvidence.DirectoryPath,
                Root = Path.Combine(ResourceCostEvidence.DirectoryPath, "f03"),
                Token = Guid.NewGuid().ToString("N").Substring(0, 8),
                Scenes = EditorSceneManager.GetSceneManagerSetup()
            };
            state.ProjectId = "f3-" + state.Token;
            state.Folder = "Assets/Tests/YLFramework/Fixtures/F03-" + state.Token;
            state.Scene = "Temp/F03-" + state.Token + ".unity";
            state.CacheRoot = Path.Combine(Path.GetTempPath(), "kilo", "f3-" + state.Token);
            Assert.That(Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(state.Root)) || File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(state.Root)), Is.False);
            ResourceLocalSnapshot.RejectLinks(state.Root);
            Directory.CreateDirectory(global::YLFramework.Testing.PackageTestPaths.Physical(state.Root));
            Save(state);
            File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(state.Root, "owner.json")), JsonUtility.ToJson(state));
            Assert.That(Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(Path.GetDirectoryName(state.CacheRoot))), Is.True);
            Assert.That(Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(state.CacheRoot)) || File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(state.CacheRoot)), Is.False);
            ResourceLocalSnapshot.RejectLinks(state.CacheRoot);
            Directory.CreateDirectory(global::YLFramework.Testing.PackageTestPaths.Physical(state.CacheRoot));
            File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(state.CacheRoot, "owner.json")), new JObject
            {
                ["Kind"] = "F03BaselineCache",
                ["Run"] = state.Run,
                ["Root"] = state.CacheRoot,
                ["Token"] = state.Token
            }.ToString());
            const string Parent = "Assets/Tests/YLFramework/Fixtures";
            if (!AssetDatabase.IsValidFolder(Parent))
            {
                AssetDatabase.CreateFolder("Assets/Tests/YLFramework", "Fixtures");
            }

            Assert.That(Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(state.Folder)) || File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(state.Folder)) || File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(state.Folder + ".meta")), Is.False);
            Assert.That(AssetDatabase.CreateFolder(Parent, Path.GetFileName(state.Folder)), Is.Not.Empty);
            state.FolderCreated = true;
            state.FolderGuid = AssetDatabase.AssetPathToGUID(state.Folder);
            Save(state);
            Assert.That(AssetDatabase.CreateFolder(state.Folder, "Content"), Is.Not.Empty);

            // Only the fixture's clean empty scene is saved. Never save or replace a dirty user scene.
            Assert.That(Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical("Temp")), Is.True);
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            Assert.That(File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(state.Scene)), Is.False);
            state.SceneCreated = true;
            Save(state);
            Assert.That(EditorSceneManager.SaveScene(scene, state.Scene), Is.True);
            CreateTexture(Content(state) + "/Large.asset", 2048, 890301);
            CreateTexture(Content(state) + "/Medium.asset", 1024, 890302);
            var shader = Shader.Find("Unlit/Texture");
            Assert.That(shader, Is.Not.Null);
            var material = new Material(shader) { name = "F03Shared", mainTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(Content(state) + "/Large.asset") };
            AssetDatabase.CreateAsset(material, Content(state) + "/Shared.mat");
            foreach (var name in new[] { "A", "B", "C" })
            {
                var go = new GameObject("F03-" + name);
                try
                {
                    go.AddComponent<MeshRenderer>().sharedMaterial = material;
                    Assert.That(PrefabUtility.SaveAsPrefabAsset(go, Content(state) + "/" + name + ".prefab"), Is.Not.Null);
                }
                finally
                {
                    Object.DestroyImmediate(go);
                }
            }
            WritePcm(Content(state) + "/Owned.wav");
            ChangeEntry(state, 0);
            var project = ScriptableObject.CreateInstance<ResourceProject>();
            project.projectId = state.ProjectId;
            project.sourceDirectories.Add(new ResourceSourceDirectory { guid = AssetDatabase.AssetPathToGUID(Content(state)) });
            project.compression = ResourceCompression.Uncompressed;
            project.groups.Add(Group(state, "entry", "entry.txt", "entry"));
            AssetDatabase.CreateAsset(project, state.Folder + "/Project.asset");
            state.ProjectGuid = AssetDatabase.AssetPathToGUID(state.Folder + "/Project.asset");
            Save(state);
            var recipe = ScriptableObject.CreateInstance<ResourceBuildRecipe>();
            recipe.project = project;
            recipe.target = BuildTarget.StandaloneWindows64;
            recipe.appContentCompatibilityId = AppId;
            recipe.action = ResourceBuildAction.BuildAndPublish;
            recipe.generateReferences = false;
            AssetDatabase.CreateAsset(recipe, state.Folder + "/Recipe.asset");
            Assert.That(Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(PublisherRoot(state))), Is.False);
            Assert.That(scene.rootCount, Is.Zero);
            Assert.That(EditorSceneManager.SaveScene(scene, state.Scene), Is.True);
            state.Ready = true;
            Save(state);
            ResourceCostEvidence.Write("f03-fixture", new
            {
                state.Folder,
                state.ProjectGuid,
                state.ProjectId,
                state.Root,
                state.CacheRoot,
                Compression = "Uncompressed + StrictMode",
                Texture = "2048x2048 and 1024x1024 RGBA32, no mipmaps, deterministic byte noise",
                Audio = "20 seconds, 44100 Hz, stereo 16-bit PCM, DecompressOnLoad; no audio-underrun counter",
                Shared = "Three separately packed Prefabs reference the same Material and Large Texture",
                WarmupPerMode = 1,
                NewWorkspaceSamples = Samples,
                NoSourceChangeSamples = Samples,
                LocalChangeSamples = Samples,
                LocalChange = "Only the independent 16 KiB entry TextAsset changes; source import is outside operation timing",
                Boundaries = "Editor baseline, not batchmode/CI or OS-cold disk. New-workspace resets raw only; the owned Publisher persists and IsRepeat says whether publication copied. No thresholds or extrapolation to G3 Player.",
                Sources = SourceFacts(state)
            });
        }

        private static void CreateTexture(string path, int size, int seed)
        {
            var data = new byte[size * size * 4];
            new System.Random(seed).NextBytes(data);
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false, true) { name = Path.GetFileNameWithoutExtension(path) };
            texture.LoadRawTextureData(data);
            texture.Apply(false, false);
            AssetDatabase.CreateAsset(texture, path);
        }

        private static void WritePcm(string path)
        {
            const int Frames = 44100 * 20;
            using (var output = new BinaryWriter(new FileStream(global::YLFramework.Testing.PackageTestPaths.Physical(path), FileMode.CreateNew, FileAccess.Write)))
            {
                output.Write(Encoding.ASCII.GetBytes("RIFF"));
                output.Write(36 + Frames * 4);
                output.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
                output.Write(16);
                output.Write((short)1);
                output.Write((short)2);
                output.Write(44100);
                output.Write(44100 * 4);
                output.Write((short)4);
                output.Write((short)16);
                output.Write(Encoding.ASCII.GetBytes("data"));
                output.Write(Frames * 4);
                for (var i = 0; i < Frames; i++)
                {
                    output.Write((short)(Math.Sin(i * 2 * Math.PI * 437 / 44100) * 12000));
                    output.Write((short)(Math.Sin(i * 2 * Math.PI * 659 / 44100) * 12000));
                }
            }
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            var importer = (AudioImporter)AssetImporter.GetAtPath(path);
            var settings = importer.defaultSampleSettings;
            settings.compressionFormat = AudioCompressionFormat.PCM;
            settings.loadType = AudioClipLoadType.DecompressOnLoad;
            settings.sampleRateSetting = AudioSampleRateSetting.PreserveSampleRate;
            settings.preloadAudioData = false;
            importer.defaultSampleSettings = settings;
            importer.SaveAndReimport();
        }

        private static string ChangeEntry(FixtureState state, int version)
        {
            var text = "f03-local-" + version + "\n" + new string('x', 16 * 1024);
            File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(Content(state) + "/entry.txt"), text, new UTF8Encoding(false));
            AssetDatabase.ImportAsset(Content(state) + "/entry.txt", ImportAssetOptions.ForceSynchronousImport);
            return text;
        }

        private static ResourceGroup Group(FixtureState state, string id, string file, string address) => new()
        {
            id = id,
            selections = new List<ResourceSelection> { new() { guid = AssetDatabase.AssetPathToGUID(Content(state) + "/" + file), address = address } }
        };

        // 21 次真实 Recipe 采样包含完整指纹/认证；执行预算不是单次构建的性能门槛。
        [Test, Order(1), Timeout(15 * 60 * 1000)]
        public void ARecipeBuildAndPublicationBaseline()
        {
            var state = State;
            Assert.That(state.BuildsComplete, Is.False, "Use a fresh explicitly armed run for a rerun.");
            using var keys = new FixtureKeys(state.ProjectId);
            TrackWorkspace(state);
            var bootstrap = BuildSample(state, "bootstrap", "setup", 0, keys);
            state.Bootstrap = Export(state, bootstrap.Release, "bootstrap");
            Save(state);
            // A real, one-way definition evolution, not a temporary execution override or Project clone.
            var project = Project(state);
            foreach (var name in new[] { "A", "B", "C" })
            {
                project.groups.Add(Group(state, "prefab-" + name.ToLowerInvariant(), name + ".prefab", name));
            }

            project.groups.Add(Group(state, "large", "Large.asset", "large"));
            project.groups.Add(Group(state, "medium", "Medium.asset", "medium"));
            project.groups.Add(Group(state, "audio", "Owned.wav", "audio"));
            EditorUtility.SetDirty(project);
            ResourceEditorOperations.SaveProject(project);
            foreach (var mode in _modes)
            {
                // 两种交付模式从完全相同的资源字节开始，局部变更序列也相同。
                var version = 0;
                ChangeEntry(state, version);
                if (mode == "Encrypted")
                {
                    project = Project(state);
                    project.contentEncryption = ResourceContentEncryption.Aes256GcmHkdfSegmentedV1;
                    project.contentKeyId = "f03-owned";
                    EditorUtility.SetDirty(project);
                    ResourceEditorOperations.SaveProject(project);
                }
                var workspace = TrackWorkspace(state);
                BuildSample(state, mode, "warmup", 0, keys);
                ResourceEditorBuildReport previous = null;
                for (var i = 1; i <= Samples; i++)
                {
                    DeleteWorkspace(state, workspace);
                    Assert.That(Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(workspace)), Is.False);
                    previous = BuildSample(state, mode, "new-workspace", i, keys);
                    Assert.That(previous.Build.WasUnityBuildInvoked, Is.True);
                }
                for (var i = 1; i <= Samples; i++)
                {
                    var next = BuildSample(state, mode, "no-source-change", i, keys);
                    Assert.That(next.Build.InputFingerprint, Is.EqualTo(previous.Build.InputFingerprint));
                    CollectionAssert.AreEqual(previous.Build.DeliveryFiles.Select(FileIdentity), next.Build.DeliveryFiles.Select(FileIdentity));
                    Assert.That(next.Release.Catalog.CatalogDigest, Is.EqualTo(previous.Release.Catalog.CatalogDigest));
                    Assert.That(next.Release.IsRepeat, Is.True);
                    if (mode == "Encrypted")
                    {
                        Assert.That(next.Build.EncodedDeliveryBytes, Is.Zero);
                    }

                    previous = next;
                }
                for (var i = 1; i <= Samples; i++)
                {
                    var text = ChangeEntry(state, ++version);
                    var next = BuildSample(state, mode, "local-change", i, keys);
                    Assert.That(next.Build.InputFingerprint, Is.Not.EqualTo(previous.Build.InputFingerprint));
                    Assert.That(next.Release.Catalog.CatalogDigest, Is.Not.EqualTo(previous.Release.Catalog.CatalogDigest));
                    var textBundle = next.Build.Map.Assets.Single(x => x.Address.Path == "entry").BundleName;
                    CollectionAssert.AreEquivalent(new[] { textBundle }, next.Build.BundleChanges.Where(x => x.ContentChanged == true).Select(x => x.Name));
                    if (mode == "None")
                    {
                        state.NoneText = text;
                    }
                    else
                    {
                        state.EncryptedText = text;
                    }

                    previous = next;
                }
                var path = Export(state, previous.Release, mode.ToLowerInvariant());
                if (mode == "None")
                {
                    state.None = path;
                }
                else
                {
                    state.Encrypted = path;
                }

                Save(state);
            }
            state.BuildsComplete = true;
            Save(state);
            ResourceCostEvidence.Write("f03-releases", new
            {
                state.Bootstrap,
                state.None,
                state.Encrypted,
                state.ProjectId,
                state.ProjectGuid
            });
        }

        private static string TrackWorkspace(FixtureState state)
        {
            var root = Path.GetDirectoryName(ResourceBundleBuilder.GetWorkspacePath(Project(state), BuildTarget.StandaloneWindows64));
            if (!state.Workspaces.Contains(root))
            {
                Assert.That(Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(root)) || File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(root)), Is.False, "Never adopt an earlier Workspace.");
                state.Workspaces.Add(root);
                Save(state);
            }
            return root;
        }

        private static ResourceEditorBuildReport BuildSample(FixtureState state, string mode, string kind, int sample, FixtureKeys keys)
        {
            var started = Stopwatch.GetTimestamp();
            var report = ResourceEditorOperations.ExecuteRecipe(Recipe(state), provider: keys);
            var finished = Stopwatch.GetTimestamp();
            // Serialization, assertions and extra hashes are deliberately outside the performance window.
            ResourceCostEvidence.Write("f03-a-" + mode.ToLowerInvariant() + "-" + kind + "-" + sample, new
            {
                Mode = mode,
                Kind = kind,
                Sample = sample,
                Warmup = kind == "warmup" || kind == "setup",
                WallMilliseconds = Milliseconds(started, finished),
                Invocation = report.Invocation == null ? null : new
                {
                    report.Invocation.RecipePath,
                    report.Invocation.ProjectGuid,
                    report.Invocation.ProjectId,
                    report.Invocation.Target,
                    report.Invocation.AppContentCompatibilityId,
                    report.Invocation.Action,
                    report.Invocation.ForceRebuild,
                    report.Invocation.Compression,
                    report.Invocation.ContentEncryption,
                    report.Invocation.GenerateReferences
                },
                report.ElapsedMilliseconds,
                report.PreflightMilliseconds,
                report.PublicationMilliseconds,
                Build = BuildFacts(report.Build),
                Release = report.Release == null ? null : new
                {
                    report.Release.DirectoryPath,
                    report.Release.Catalog.BuildId,
                    report.Release.Catalog.CatalogDigest,
                    report.Release.IsRepeat,
                    report.Release.CopiedBytes,
                    report.Release.ElapsedMilliseconds,
                    report.Release.CopyMilliseconds,
                    report.Release.StagingValidationMilliseconds
                },
                Errors = new[] { report.BuildError, report.PublicationError, report.GenerationError }.Where(x => x != null).Select(x => x.ToString()).ToArray(),
                Sources = SourceFacts(state),
                TimingBoundary = "ExecuteRecipe, including original Preflight/Builder/Publisher and all full checks. Nested timings overlap; do not sum them. No source import or evidence IO."
            });
            if (report.BuildError != null || report.PublicationError != null || report.GenerationError != null)
            {
                state.BuildFailed = true;
                Save(state);
                ResourceCostEvidence.Write("f03-build-failure-state", new
                {
                    State = state,
                    Errors = new[] { report.BuildError, report.PublicationError, report.GenerationError }.Where(x => x != null)
                        .Select(x => new { Type = x.GetType().FullName, x.HResult, Win32LowWord = x.HResult & 0xffff, Detail = x.ToString() }),
                    Metadata = Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(PublisherRoot(state))) ? Directory.GetFiles(global::YLFramework.Testing.PackageTestPaths.Physical(PublisherRoot(state)), "*", SearchOption.TopDirectoryOnly)
                        .Select(x => new { Path = x, Attributes = File.GetAttributes(global::YLFramework.Testing.PackageTestPaths.Physical(x)).ToString(), Length = new FileInfo(x).Length }).ToArray() : null
                });
            }
            Assert.That(report.BuildError, Is.Null, report.BuildError?.ToString());
            Assert.That(report.PublicationError, Is.Null, report.PublicationError?.ToString());
            Assert.That(report.GenerationError, Is.Null, report.GenerationError?.ToString());
            Assert.That(report.Release, Is.Not.Null);
            if (report.Build.CacheReuseBlockedBy.Count != 0)
            {
                Assert.That(report.Build.WasUnityBuildInvoked && report.Build.ForceRebuild, Is.True);
                Assert.That(report.Build.IsCacheHit, Is.False, "Never bypass the callback safety gate for a performance result.");
            }
            if (mode != "bootstrap")
            {
                Assert.That(report.Build.DeliveryBytes, Is.InRange(20 * MiB, 40 * MiB));
                Assert.That(report.Release.Catalog.Bundles.Max(x => x.Length), Is.GreaterThanOrEqualTo(16 * MiB));
                Assert.That(report.Build.Bundles.Count, Is.GreaterThan(3));
                Assert.That(report.Build.Bundles.Any(x => x.Placement == ResourceBundlePlacement.SharedDependencies && x.Dependents.Count >= 2), Is.True);
            }
            return report;
        }

        private static object BuildFacts(ResourceBuildResult build) => build == null ? null : new
        {
            build.WorkspacePath,
            build.InputFingerprint,
            build.BuildSettingsFingerprint,
            build.Platform,
            build.UnityCompatibility,
            build.Compression,
            build.ContentEncryption,
            build.IsCacheHit,
            UnityBuildInvoked = build.WasUnityBuildInvoked,
            ForcedRebuild = build.ForceRebuild,
            build.CacheReuseBlockedBy,
            build.ExecutionReasons,
            build.PreflightMilliseconds,
            build.BuildMilliseconds,
            build.ValidationMilliseconds,
            build.ElapsedMilliseconds,
            build.MapMilliseconds,
            build.InputFingerprintMilliseconds,
            build.BuildPipelineMilliseconds,
            build.ArtifactHashMilliseconds,
            build.ReadbackMilliseconds,
            build.ContentEncodingMilliseconds,
            build.SourceHashBytes,
            build.ArtifactHashBytes,
            build.BundleFileBytes,
            build.DeliveryBytes,
            build.EncodedDeliveryBytes,
            build.ReusedDeliveryBytes,
            build.RetainedObsoleteFiles,
            RawFiles = build.Files,
            DeliveryFiles = build.DeliveryFiles,
            build.BundleChanges,
            build.Bundles,
            Assets = build.Map.Assets.Select(x => new { x.Guid, x.LocalId, x.SourcePath, Address = x.Address.Path, x.BundleName }),
            Note = "Raw/source/artifact logical bytes are not native or process memory. Original timings are hierarchical, not additive."
        };

        private static string FileIdentity(ResourceBuiltFile file) => file.Name + ":" + file.Length + ":" + file.Sha256;
        private static string Hash(string path)
        {
            ResourceLocalSnapshot.RejectLinks(path);
            using var input = File.OpenRead(global::YLFramework.Testing.PackageTestPaths.Physical(path));
            using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(input)).Replace("-", "").ToLowerInvariant();
        }

        private static object SourceFacts(FixtureState state) => Directory.GetFiles(global::YLFramework.Testing.PackageTestPaths.Physical(Content(state)), "*", SearchOption.AllDirectories)
            .OrderBy(x => x, StringComparer.Ordinal).Select(x => new { Path = x.Replace('\\', '/'), Length = new FileInfo(x).Length, Sha256 = Hash(x) }).ToArray();

        private static string Export(FixtureState state, ResourceRelease release, string label)
        {
            var parent = Path.Combine(state.Root, "exports", label);
            Assert.That(Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(parent)), Is.False);
            Directory.CreateDirectory(global::YLFramework.Testing.PackageTestPaths.Physical(parent));
            var path = Path.Combine(parent, release.Catalog.CatalogDigest);
            Assert.That(Path.Combine(path, release.Catalog.Bundles.OrderByDescending(x => x.RelativeFile.Length).First().RelativeFile).Length, Is.LessThan(255));
            var bytes = ResourceEditorOperations.ExportRelease(release, path);
            ResourceCostEvidence.Write("f03-export-" + label, new
            {
                Directory = path,
                release.Catalog.CatalogDigest,
                release.Catalog.BuildId,
                CopiedBytes = bytes,
                Files = Directory.GetFiles(global::YLFramework.Testing.PackageTestPaths.Physical(path)).Select(x => new { Name = Path.GetFileName(x), Length = new FileInfo(x).Length, Sha256 = Hash(x) }).ToArray()
            });
            return path;
        }

        private static ResourceLocalSnapshot Snapshot(FixtureState state, string mode) => OpenSnapshot(state, mode == "None" ? state.None : state.Encrypted);
        private static ResourceLocalSnapshot OpenSnapshot(FixtureState state, string path)
        {
            Assert.That(path, Is.Not.Null.And.Not.Empty, "Run A before B/C, preferably by selecting the whole fixture.");
            return ResourceLocalSnapshot.Open(path, state.ProjectId, Path.GetFileName(path), Platform, AppId);
        }

        private sealed class FixtureKeys : IResourceContentKeyProvider, IDisposable
        {
            private readonly string _project;
            private readonly int _thread = System.Threading.Thread.CurrentThread.ManagedThreadId;
            private bool _isDisposed;
            internal FixtureKeys(string project) => this._project = project;
            public bool TryCopyKey(string projectId, string keyId, byte[] destination)
            {
                if (_isDisposed || projectId != _project || keyId != "f03-owned" || destination.Length != 32 || System.Threading.Thread.CurrentThread.ManagedThreadId != _thread)
                {
                    throw new InvalidOperationException("Invalid owned fixture key request.");
                }

                for (var i = 0; i < destination.Length; i++)
                {
                    destination[i] = (byte)(113 + i);
                }

                return true;
            }
            public void Dispose() => _isDisposed = true;
        }

        private static string PublisherRoot(FixtureState state) => Path.GetFullPath(global::YLFramework.Testing.PackageTestPaths.Physical("AssetBundles/YLFramework/" + state.ProjectId + "/" + Platform));

        private static void DeleteWorkspace(FixtureState state, string root)
        {
            if (!Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(root)))
            {
                return;
            }

            Assert.That(Path.GetFileName(root), Does.StartWith("YLR_" + state.ProjectGuid + "_"));
            var marker = JObject.Parse(File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(root, ".ylr-workspace.json"))));
            Assert.That((string)marker["ProjectGuid"], Is.EqualTo(state.ProjectGuid));
            Assert.That((string)marker["ProjectId"], Is.EqualTo(state.ProjectId));
            Assert.That((string)marker["ProjectRoot"], Is.EqualTo(Path.GetFullPath(global::YLFramework.Testing.PackageTestPaths.Physical("."))));
            var known = new HashSet<string>(new[] { ".ylr-workspace.json", ".ylr-workspace.next", ".ylr-workspace.lock" }, StringComparer.Ordinal);
            foreach (var file in (JArray)marker["KnownFiles"])
            {
                known.Add((string)marker["Leaf"] + "/" + (string)file);
            }

            if (marker["KnownDeliveryFiles"] is JArray deliveries)
            {
                foreach (var file in deliveries)
                {
                    known.Add((string)file);
                }
            }

            RejectTreeLinks(root);
            var directories = new HashSet<string>(known.Where(x => x.Contains('/')).Select(x => x.Substring(0, x.IndexOf('/'))), StringComparer.Ordinal);
            foreach (var directory in Directory.GetDirectories(global::YLFramework.Testing.PackageTestPaths.Physical(root), "*", SearchOption.AllDirectories))
            {
                Assert.That(directories.Contains(Path.GetRelativePath(root, directory).Replace('\\', '/')), Is.True, "Do not delete an unregistered Workspace directory: " + directory);
            }

            foreach (var file in Directory.GetFiles(global::YLFramework.Testing.PackageTestPaths.Physical(root), "*", SearchOption.AllDirectories))
            {
                Assert.That(known.Contains(Path.GetRelativePath(root, file).Replace('\\', '/')), Is.True, "Do not delete an unregistered Workspace file: " + file);
            }

            Directory.Delete(global::YLFramework.Testing.PackageTestPaths.Physical(root), true);
        }

        private static void RejectTreeLinks(string root)
        {
            ResourceLocalSnapshot.RejectLinks(root);
            foreach (var path in Directory.GetFileSystemEntries(global::YLFramework.Testing.PackageTestPaths.Physical(root)))
            {
                ResourceLocalSnapshot.RejectLinks(path);
                if (Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(path)))
                {
                    RejectTreeLinks(path);
                }
            }
        }

        [OneTimeTearDown]
        public void RestoreScenesAndRemoveOnlyVerifiedOwnedBuildInputs()
        {
            var state = State;
            if (string.IsNullOrEmpty(state.Root))
            {
                return;
            }

            Assert.That(Application.isPlaying, Is.False, "A failed runtime cleanup must be diagnosed before removing its fixture.");
            var failures = new List<Exception>();
            void Clean(Action action)
            {
                try
                {
                    action();
                }
                catch (Exception error)
                {
                    failures.Add(error);
                }
            }
            Clean(() =>
            {
                Assert.That(Application.isPlaying, Is.False);
                ResourceSessionTests.EnsureRestorableScenes();
                if (state.Scenes != null)
                {
                    EditorSceneManager.RestoreSceneManagerSetup(state.Scenes);
                }

                if (state.SceneCreated && File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(state.Scene)))
                {
                    File.Delete(global::YLFramework.Testing.PackageTestPaths.Physical(state.Scene));
                }
            });
            if (state.BuildFailed)
            {
                ResourceCostEvidence.Write("f03-preserved-build-failure", new
                {
                    State = state,
                    Note = "The failed raw/Publisher/source roots remain owned for diagnosis. No retry, recovery, overwrite or deletion was performed.",
                    SceneRestoreFailures = failures.Select(x => x.ToString()).ToArray()
                });
                if (failures.Count != 0)
                {
                    throw new AggregateException("Scene restoration failed after owned build failure.", failures);
                }

                SessionState.EraseString(StateKey);
                return;
            }
            foreach (var root in state.Workspaces)
            {
                Clean(() => DeleteWorkspace(state, root));
            }

            Clean(() =>
            {
                var root = PublisherRoot(state);
                if (!Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(root)))
                {
                    return;
                }

                var owner = JObject.Parse(File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(root, ".ylr-release-owner.json"))));
                Assert.That((string)owner["ProjectGuid"], Is.EqualTo(state.ProjectGuid));
                Assert.That((string)owner["ProjectId"], Is.EqualTo(state.ProjectId));
                Assert.That((string)owner["ProjectRoot"], Is.EqualTo(Path.GetFullPath(global::YLFramework.Testing.PackageTestPaths.Physical("."))));
                ResourcePublisher.ReadHistory(Project(state), BuildTarget.StandaloneWindows64);
                RejectTreeLinks(root);
                Directory.Delete(global::YLFramework.Testing.PackageTestPaths.Physical(root), true);
                var parent = Path.GetDirectoryName(root);
                if (Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(parent)) && !Directory.EnumerateFileSystemEntries(global::YLFramework.Testing.PackageTestPaths.Physical(parent)).Any())
                {
                    Directory.Delete(global::YLFramework.Testing.PackageTestPaths.Physical(parent));
                }
            });
            Clean(() =>
            {
                if (!state.FolderCreated || !AssetDatabase.IsValidFolder(state.Folder))
                {
                    return;
                }

                Assert.That(AssetDatabase.AssetPathToGUID(state.Folder), Is.EqualTo(state.FolderGuid));
                RejectTreeLinks(Path.GetFullPath(global::YLFramework.Testing.PackageTestPaths.Physical(state.Folder)));
                Assert.That(AssetDatabase.DeleteAsset(state.Folder), Is.True);
            });
            Clean(() =>
            {
                if (!state.NetworkComplete || !Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(state.CacheRoot)))
                {
                    return;
                }

                RejectTreeLinks(state.CacheRoot);
                var marker = JObject.Parse(File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(state.CacheRoot, "owner.json"))));
                Assert.That((string)marker["Kind"], Is.EqualTo("F03BaselineCache"));
                Assert.That((string)marker["Run"], Is.EqualTo(state.Run));
                Assert.That((string)marker["Root"], Is.EqualTo(state.CacheRoot));
                Assert.That((string)marker["Token"], Is.EqualTo(state.Token));
                foreach (var path in Directory.GetFileSystemEntries(global::YLFramework.Testing.PackageTestPaths.Physical(state.CacheRoot)))
                {
                    Assert.That(Path.GetFileName(path) == "owner.json" || state.CacheStores.Contains(path), Is.True, "An unregistered cache child must not be deleted.");
                }

                foreach (var root in state.CacheStores.Where(Directory.Exists))
                {
                    var owner = JObject.Parse(File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(root, ".ylr-store-owner.json"))));
                    Assert.That((string)owner["Kind"], Is.EqualTo("YLFramework.Resource.ArtifactStore"));
                    Assert.That((string)owner["Root"], Is.EqualTo(root));
                    Assert.That((string)owner["Projects"].Single()["ProjectId"], Is.EqualTo(state.ProjectId));
                }
                Directory.Delete(global::YLFramework.Testing.PackageTestPaths.Physical(state.CacheRoot), true);
            });
            Clean(() => ResourceCostEvidence.Write("f03-owned-cleanup", new
            {
                Preserved = state.Root,
                FailedCachePreserved = state.NetworkComplete ? null : state.CacheRoot,
                Note = "Original JSON and fixed exported releases remain. Successful cache roots, owned sources/raw/Publisher/runner scene are reclaimed; failed network cache is retained.",
                Failures = failures.Select(x => x.ToString()).ToArray()
            }));
            if (failures.Count != 0)
            {
                throw new AggregateException("Owned F03 fixture cleanup failed; do not accept a leaf-only gate.", failures);
            }

            SessionState.EraseString(StateKey);
        }
    }
}
