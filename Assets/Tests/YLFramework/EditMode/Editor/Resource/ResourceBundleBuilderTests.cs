using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;
using YLFramework.Editor.Resource;
using Object = UnityEngine.Object;

namespace YLFramework.Tests.ResourceBuild
{
    public class ResourceBundleBuilderTests
    {
        private string _folder;
        private string _workspace;
        private string _otherWorkspace;
        private string _text;
        private BuildTarget _target;
        private ResourceProject _project;
        private bool _retainWorkspace;
        private Type Builder => typeof(ResourceProject).Assembly.GetType("YLFramework.Editor.Resource.ResourceBundleBuilder");
        private delegate AssetBundleManifest Pipeline(string output, AssetBundleBuild[] builds, BuildAssetBundleOptions options, BuildTarget target);

        [SetUp]
        public void SetUp()
        {
            _target = EditorUserBuildSettings.activeBuildTarget;
            _otherWorkspace = null;
            const string Parent = "Assets/Tests/YLFramework/Fixtures";
            if (!AssetDatabase.IsValidFolder(Parent))
            {
                AssetDatabase.CreateFolder("Assets/Tests/YLFramework", "Fixtures");
            }

            _folder = Parent + "/Builder82-" + Guid.NewGuid().ToString("N");
            Assert.That(AssetDatabase.CreateFolder(Parent, Path.GetFileName(_folder)), Is.Not.Empty);
            AssetDatabase.CreateFolder(_folder, "Content");
            _text = _folder + "/Content/entry.txt";
            File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(_text), "first owned content");
            AssetDatabase.ImportAsset(_text, ImportAssetOptions.ForceSynchronousImport);
            _project = ScriptableObject.CreateInstance<ResourceProject>();
            _project.projectId = "yl-build-" + Guid.NewGuid().ToString("N");
            _project.sourceDirectories.Add(new ResourceSourceDirectory { guid = AssetDatabase.AssetPathToGUID(_folder + "/Content") });
            _project.groups.Add(new ResourceGroup { id = "text", selections = new List<ResourceSelection> { Selection(_text) } });
            AssetDatabase.CreateAsset(_project, _folder + "/Project.asset");
            Assert.That(Builder, Is.Not.Null, "The isolated 8.2a build kernel is missing.");
            _workspace = (string)Builder.GetMethod("GetWorkspacePath").Invoke(null, new object[] { _project, EditorUserBuildSettings.activeBuildTarget });
            Assert.That(Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(Path.GetDirectoryName(_workspace))), Is.False, "A fresh fixture must not take over any existing workspace.");
            _retainWorkspace = false;
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var path in new[] { _workspace, _otherWorkspace }.Where(x => x != null).Distinct())
            {
                if (_retainWorkspace || !Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(Path.GetDirectoryName(path))))
                {
                    continue;
                }

                var root = Path.GetDirectoryName(path);
                var junction = Path.Combine(root, "test-junction");
                if (Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(junction)))
                {
                    Directory.Delete(global::YLFramework.Testing.PackageTestPaths.Physical(junction));
                }

                Directory.Delete(global::YLFramework.Testing.PackageTestPaths.Physical(root), true);
            }
            AssetDatabase.DeleteAsset(_folder);
            _workspace = null;
        }

        [Test]
        [TestCase(0, BuildAssetBundleOptions.ChunkBasedCompression)]
        [TestCase(1, BuildAssetBundleOptions.None)]
        [TestCase(2, BuildAssetBundleOptions.UncompressedAssetBundle)]
        public void CompressionChoicePartitionsWorkspaceAndReachesTheRealNativePipeline(int choice, BuildAssetBundleOptions compression)
        {
            var field = typeof(ResourceProject).GetField("compression");
            Assert.That(field, Is.Not.Null, "The saved project needs a safe compression choice, not raw BuildAssetBundleOptions.");
            field.SetValue(_project, Enum.ToObject(field.FieldType, choice));
            EditorUtility.SetDirty(_project);
            AssetDatabase.SaveAssetIfDirty(_project);
            _otherWorkspace = ResourceBundleBuilder.GetWorkspacePath(_project, _target);
            Assert.That(_otherWorkspace == _workspace, Is.EqualTo(choice == 0), "Compression is part of the compatible Workspace identity.");
            var before = ProtectedSnapshot();
            BuildAssetBundleOptions observed = 0;
            var result = (ResourceBuildResult)Execute((output, builds, options, platform) =>
            {
                observed = options;
                return Native(output, builds, options, platform);
            });
            Assert.That(observed & (BuildAssetBundleOptions.ChunkBasedCompression | BuildAssetBundleOptions.UncompressedAssetBundle), Is.EqualTo(compression));
            Assert.That(observed.HasFlag(BuildAssetBundleOptions.StrictMode), Is.True);
            Assert.That(observed & ~(compression | BuildAssetBundleOptions.StrictMode | BuildAssetBundleOptions.ForceRebuildAssetBundle), Is.EqualTo(BuildAssetBundleOptions.None));
            var evidence = result.Bundles.Single();
            Assert.That(evidence.FileBytes, Is.EqualTo(result.Files.Single(value => value.Name == evidence.Name).Length));
            Assert.That(evidence.SourceBytes, Is.EqualTo(new FileInfo(_text).Length));
            Assert.That(evidence.SourceFileCount, Is.EqualTo(1));
            Assert.That(evidence.Placement, Is.EqualTo(ResourceBundlePlacement.Group));
            var bundle = AssetBundle.LoadFromFile(Path.Combine(result.WorkspacePath, result.Bundles.Single().Name));
            Assert.That(bundle, Is.Not.Null);
            try
            {
                Assert.That(bundle.LoadAsset<TextAsset>(_text).text, Is.EqualTo("first owned content"));
            }
            finally
            {
                bundle.Unload(true);
            }
            CollectionAssert.AreEqual(before, ProtectedSnapshot());
            UnityEngine.Debug.Log($"YL89 compression={choice}, BundleBytes={result.BundleFileBytes}, nativeMs={result.BuildPipelineMilliseconds:F2}");
        }

        [Test]
        public void UnknownCompressionIsRejectedBeforeAnyWorkspaceWrite()
        {
            var field = typeof(ResourceProject).GetField("compression");
            Assert.That(field, Is.Not.Null);
            field.SetValue(_project, Enum.ToObject(field.FieldType, 99));
            Assert.Throws<ArgumentOutOfRangeException>(() => ResourceBundleBuilder.GetWorkspacePath(_project, _target));
            Assert.That(Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(Path.GetDirectoryName(_workspace))), Is.False);
        }

        [Test]
        public void OverlongWindowsWorkspaceFilesAreRejectedBeforeWorkspaceOrNativeWrites()
        {
            if (Path.DirectorySeparatorChar != '\\')
            {
                Assert.Ignore("Unity Mono Windows path boundary only.");
            }

            var type = typeof(ResourceProject).Assembly.GetType("YLFramework.Editor.Resource.ResourceBuildWorkspace", true);
            var constructor = type.GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single();
            var map = ResourceBuildMap.Create(_project);
            var error = Assert.Throws<TargetInvocationException>(() => constructor.Invoke(new object[]
            {
                map, new string('a', 64), new string('p', 160),
                YLFramework.Resource.ResourceContentEncryption.None, null
            }));
            Assert.That(error.InnerException, Is.TypeOf<PathTooLongException>());
            Assert.That(error.InnerException.Message, Does.Contain("shorter Unity project path"));
            Assert.That(Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(Path.GetDirectoryName(_workspace))), Is.False);
        }

        [Test]
        public void CompiledBinaryAndAssemblyConfigurationBytesAreBuildInputs()
        {
            var temp = Path.Combine(Path.GetTempPath(), "kilo", "BuildInputs82-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(global::YLFramework.Testing.PackageTestPaths.Physical(temp));
            try
            {
                var binary = Path.Combine(temp, "Callback.dll");
                var config = Path.Combine(temp, "Callback.asmdef");
                File.Copy(global::YLFramework.Testing.PackageTestPaths.Physical(typeof(ResourceProject).Assembly.Location), global::YLFramework.Testing.PackageTestPaths.Physical(binary));
                File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(config), "{\"name\":\"OwnedInput\"}");
                var method = Builder.GetMethod("InputFilesFingerprint", BindingFlags.Static | BindingFlags.NonPublic);
                Assert.That(method, Is.Not.Null, "Compiled inputs must participate in the settings fingerprint.");
                string Hash() => (string)method.Invoke(null, new object[] { new[] { binary, config }, 0L });
                var first = Hash();
                File.AppendAllText(global::YLFramework.Testing.PackageTestPaths.Physical(binary), "changed compiled callback bytes");
                Assert.That(Hash(), Is.Not.EqualTo(first));
                var second = Hash();
                File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(config), "{\"name\":\"OwnedInput\",\"defineConstraints\":[\"CHANGED\"]}");
                Assert.That(Hash(), Is.Not.EqualTo(second));
                Assert.That(File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(_text)), Is.EqualTo("first owned content"));
            }
            finally
            {
                Directory.Delete(global::YLFramework.Testing.PackageTestPaths.Physical(temp), true);
            }
        }

        [Test]
        public void UntrackedCallbacksCannotUseWholeBuildSkip()
        {
            var method = Builder.GetMethod("CanSkipBuild", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);
            Assert.That(method.Invoke(null, new object[] { false, true, true, new[] { "external callback reading mutable config" } }), Is.False);
            Assert.That(method.Invoke(null, new object[] { false, true, true, Array.Empty<string>() }), Is.True);
            Assert.That(method.Invoke(null, new object[] { false, false, true, Array.Empty<string>() }), Is.False);
            Assert.That(method.Invoke(null, new object[] { false, true, false, Array.Empty<string>() }), Is.False);
            Assert.That(method.Invoke(null, new object[] { true, true, true, Array.Empty<string>() }), Is.False);
        }

        [Test]
        public void WrongTargetAndWorkerThreadAreRejectedBeforeWorkspaceCreation()
        {
            var wrongTarget = EditorUserBuildSettings.activeBuildTarget == BuildTarget.StandaloneWindows64 ? BuildTarget.StandaloneLinux64 : BuildTarget.StandaloneWindows64;
            AssertFailure<InvalidOperationException>(() => Builder.GetMethod("GetWorkspacePath").Invoke(null, new object[] { _project, wrongTarget }));
            System.Threading.Tasks.Task.Run(() => AssertFailure<InvalidOperationException>(() => Execute((output, builds, options, target) => null))).GetAwaiter().GetResult();
            Assert.That(Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(Path.GetDirectoryName(_workspace))), Is.False);
        }

        [Test]
        public void NullAndThrowLeaveAnInvalidOwnedWorkspaceWithoutDeletingPartialFiles()
        {
            string partial = null;
            AssertFailure<InvalidOperationException>(() => Execute((output, builds, options, target) =>
            {
                partial = Path.Combine(output, builds[0].assetBundleName);
                File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(partial), "owned partial");
                return null;
            }));
            Assert.That(File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(partial)), Is.EqualTo("owned partial"));
            AssertFailure<IOException>(() => Execute((output, builds, options, target) =>
            {
                throw new IOException("injected pipeline failure");
            }));
            Assert.That(File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(partial)), Is.EqualTo("owned partial"));
            var marker = File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(Path.GetDirectoryName(_workspace), ".ylr-workspace.json")));
            Assert.That(marker, Does.Contain("\"Complete\": false"));
        }

        [Test]
        public void CancellationBeforeBuildCreatesNothingAndDuringBuildRetainsItsOwnedPartial()
        {
            using var cancel = new CancellationTokenSource();
            cancel.Cancel();
            AssertFailure<OperationCanceledException>(() => Execute((output, builds, options, target) => null, cancel.Token));
            Assert.That(Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(Path.GetDirectoryName(_workspace))), Is.False);
            using var during = new CancellationTokenSource();
            string partial = null;
            AssertFailure<OperationCanceledException>(() => Execute((output, builds, options, target) =>
            {
                partial = Path.Combine(output, builds[0].assetBundleName);
                File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(partial), "cancelled partial");
                during.Cancel();
                return null;
            }, during.Token));
            Assert.That(File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(partial)), Is.EqualTo("cancelled partial"));
        }

        [Test]
        public void UnknownDirectoryIsNeverAdoptedEvenWhenEmpty()
        {
            Directory.CreateDirectory(global::YLFramework.Testing.PackageTestPaths.Physical(Path.GetDirectoryName(_workspace)));
            AssertFailure<InvalidOperationException>(() => Execute((output, builds, options, target) => null));
            Assert.That(Directory.GetFileSystemEntries(global::YLFramework.Testing.PackageTestPaths.Physical(Path.GetDirectoryName(_workspace))), Is.Empty);
        }

        [Test]
        public void UnknownFileAndConflictingOwnerCauseNoWrites()
        {
            AssertFailure<InvalidOperationException>(() => Execute((output, builds, options, target) => null));
            var root = Path.GetDirectoryName(_workspace);
            var unknown = Path.Combine(_workspace, "unknown.txt");
            File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(unknown), "do not adopt");
            var before = Snapshot(root);
            AssertFailure<InvalidOperationException>(() => Execute((output, builds, options, target) => null));
            CollectionAssert.AreEqual(before, Snapshot(root));
            File.Delete(global::YLFramework.Testing.PackageTestPaths.Physical(unknown));
            var marker = Path.Combine(root, ".ylr-workspace.json");
            File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(marker), File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(marker)).Replace(_project.projectId, "foreign-project"));
            before = Snapshot(root);
            AssertFailure<InvalidOperationException>(() => Execute((output, builds, options, target) => null));
            CollectionAssert.AreEqual(before, Snapshot(root));
        }

        [Test]
        public void PersistentFileLockRejectsAnotherBuildWithoutDeletingTheLock()
        {
            AssertFailure<InvalidOperationException>(() => Execute((output, builds, options, target) => null));
            var root = Path.GetDirectoryName(_workspace);
            var lockPath = Path.Combine(root, ".ylr-workspace.lock");
            using (File.Open(global::YLFramework.Testing.PackageTestPaths.Physical(lockPath), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                AssertFailure<IOException>(() => Execute((output, builds, options, target) => null));
            }

            Assert.That(File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(lockPath)), Is.True);
        }

        [Test]
        public void JunctionWorkspaceCannotReachItsTarget()
        {
            AssertFailure<InvalidOperationException>(() => Execute((output, builds, options, target) => null));
            var root = Path.GetDirectoryName(_workspace);
            var junction = Path.Combine(root, "test-junction");
            var sourceSnapshot = Snapshot(_folder);
            var start = new ProcessStartInfo("cmd.exe", "/c mklink /J \"" + junction + "\" \"" + Path.GetFullPath(global::YLFramework.Testing.PackageTestPaths.Physical(_folder)) + "\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using (var process = Process.Start(start))
            {
                process.WaitForExit();
                Assert.That(process.ExitCode, Is.Zero, process.StandardError.ReadToEnd());
            }
            AssertFailure<InvalidOperationException>(() => Execute((output, builds, options, target) => null));
            CollectionAssert.AreEqual(sourceSnapshot, Snapshot(_folder));
            Directory.Delete(global::YLFramework.Testing.PackageTestPaths.Physical(junction));
        }

        [Test]
        public void ReentrantBuildIsRejectedWithoutStartingAnotherAttempt()
        {
            AssertFailure<InvalidOperationException>(() => Execute((output, builds, options, buildTarget) =>
            {
                var before = Snapshot(Path.GetDirectoryName(output));
                AssertFailure<InvalidOperationException>(() => Execute((nestedOutput, nestedBuilds, nestedOptions, nestedTarget) => null));
                var after = Snapshot(Path.GetDirectoryName(output));
                CollectionAssert.AreEqual(before, after);
                return null;
            }));
        }

        [Test]
        public void RealSourceChangeAndCancellationInvalidateCacheWithoutTouchingProtectedFiles()
        {
            var protectedBefore = ProtectedSnapshot();
            AssertFailure<InvalidOperationException>(() => Execute((output, builds, options, buildTarget) =>
            {
                var manifest = Native(output, builds, options, buildTarget);
                File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(_text), "changed during native build");
                AssetDatabase.ImportAsset(_text, ImportAssetOptions.ForceSynchronousImport);
                return manifest;
            }));
            var marker = Path.Combine(Path.GetDirectoryName(_workspace), ".ylr-workspace.json");
            Assert.That(File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(marker)), Does.Contain("\"Complete\": false"));
            using var cancel = new CancellationTokenSource();
            AssertFailure<OperationCanceledException>(() => Execute((output, builds, options, buildTarget) =>
            {
                var manifest = Native(output, builds, options, buildTarget);
                cancel.Cancel();
                return manifest;
            }, cancel.Token));
            Assert.That(File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(marker)), Does.Contain("\"Complete\": false"));
            Assert.That(File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(_workspace, Path.GetFileName(_workspace)))), Is.True);
            var recovered = ResourceBundleBuilder.Build(_project, _target);
            Assert.That(recovered.ForceRebuild && recovered.WasUnityBuildInvoked && !recovered.IsCacheHit, Is.True);
            Assert.That(File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(marker)), Does.Contain("\"Complete\": true"));
            CollectionAssert.AreEqual(protectedBefore, ProtectedSnapshot());
        }

        [Test]
        public void RootManifestObjectNameIsNotItsNativeIsolationIdentity()
        {
            var protectedBefore = ProtectedSnapshot();
            AssetDatabase.CreateFolder(_folder, "OtherContent");
            var otherText = _folder + "/OtherContent/entry.txt";
            File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(otherText), File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(_text)));
            AssetDatabase.ImportAsset(otherText, ImportAssetOptions.ForceSynchronousImport);
            var other = ScriptableObject.CreateInstance<ResourceProject>();
            other.projectId = _project.projectId + "-other";
            other.sourceDirectories.Add(new ResourceSourceDirectory { guid = AssetDatabase.AssetPathToGUID(_folder + "/OtherContent") });
            other.groups.Add(new ResourceGroup { id = "text", selections = new List<ResourceSelection> { Selection(otherText) } });
            AssetDatabase.CreateAsset(other, _folder + "/OtherProject.asset");
            _otherWorkspace = ResourceBundleBuilder.GetWorkspacePath(other, _target);
            Assert.That(Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(Path.GetDirectoryName(_otherWorkspace))), Is.False);
            var aResult = ResourceBundleBuilder.Build(_project, _target);
            AssetBundle a = null, b = null;
            try
            {
                a = AssetBundle.LoadFromFile(Path.Combine(_workspace, aResult.ManifestBundleFile));
                other = AssetDatabase.LoadAssetAtPath<ResourceProject>(_folder + "/OtherProject.asset");
                Assert.Throws<InvalidOperationException>(() => ResourceBundleBuilder.Build(other, _target), "Other project ownership is protected too.");
                Assert.That((bool)a, Is.True);
                a.Unload(true);
                a = null;
                // Native build 会卸载未使用的资产；局部变量不充当 Unity 资产的永久存活根。
                other = AssetDatabase.LoadAssetAtPath<ResourceProject>(_folder + "/OtherProject.asset");
                var bResult = ResourceBundleBuilder.Build(other, _target);
                a = AssetBundle.LoadFromFile(Path.Combine(_workspace, aResult.ManifestBundleFile));
                b = AssetBundle.LoadFromFile(Path.Combine(_otherWorkspace, bResult.ManifestBundleFile));
                Assert.That(a && b, Is.True, "Differently namespaced Manifest payloads must be simultaneously loadable.");
                Assert.That(a.name, Is.Empty);
                Assert.That(b.name, Is.Empty);
                CollectionAssert.AreEqual(aResult.Map.Bundles.Select(x => x.Name), a.LoadAsset<AssetBundleManifest>("AssetBundleManifest").GetAllAssetBundles());
                CollectionAssert.AreEqual(bResult.Map.Bundles.Select(x => x.Name), b.LoadAsset<AssetBundleManifest>("AssetBundleManifest").GetAllAssetBundles());
                var renamed = Path.Combine(Path.GetDirectoryName(_workspace), "renamed-manifest");
                File.Copy(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(_workspace, aResult.ManifestBundleFile)), global::YLFramework.Testing.PackageTestPaths.Physical(renamed));
                LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("another AssetBundle with the same files is already loaded"));
                Assert.That(AssetBundle.LoadFromFile(renamed), Is.Null, "Renaming cannot create a different native identity.");
                UnityEngine.Debug.Log("YL_MANIFEST_IDENTITY: two independently saved ResourceProjects coexist; Object.name is empty; renamed duplicate rejected.");
            }
            finally
            {
                if (b)
                {
                    b.Unload(true);
                }

                if (a)
                {
                    a.Unload(true);
                }
            }
            CollectionAssert.AreEqual(protectedBefore, ProtectedSnapshot());
        }

        [Test]
        public void RealBundlesManifestSharedIdentityAndIncrementalChangesRoundTrip()
        {
            var protectedBefore = ProtectedSnapshot();
            var texture = new Texture2D(2, 2) { name = "Shared" };
            texture.SetPixels(new[] { Color.red, Color.green, Color.blue, Color.white });
            texture.Apply();
            AssetDatabase.CreateAsset(texture, _folder + "/Content/Shared.asset");
            var a = Prefab("A", texture);
            var b = Prefab("B", texture);
            var controllerPath = _folder + "/Content/Owned.controller";
            var controller = UnityEditor.Animations.AnimatorController.CreateAnimatorControllerAtPath(controllerPath);
            var animated = AssetDatabase.LoadAssetAtPath<GameObject>(a);
            var animatedCopy = (GameObject)PrefabUtility.InstantiatePrefab(animated);
            try
            {
                animatedCopy.AddComponent<Animator>().runtimeAnimatorController = controller;
                PrefabUtility.SaveAsPrefabAsset(animatedCopy, a);
            }
            finally
            {
                Object.DestroyImmediate(animatedCopy);
            }
            _project.groups.Add(new ResourceGroup { id = "a", selections = new List<ResourceSelection> { Selection(a) } });
            _project.groups.Add(new ResourceGroup { id = "b", selections = new List<ResourceSelection> { Selection(b) } });
            var setup = EditorSceneManager.GetSceneManagerSetup();
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var scenePath = _folder + "/Content/Owned.unity";
            try
            {
                var sceneTexture = new Texture2D(2, 2) { name = "SceneOnly" };
                AssetDatabase.CreateAsset(sceneTexture, _folder + "/Content/SceneTexture.asset");
                var sceneMaterial = new Material(Shader.Find("Unlit/Texture")) { name = "SceneOnly", mainTexture = sceneTexture };
                AssetDatabase.CreateAsset(sceneMaterial, _folder + "/Content/SceneMaterial.mat");
                var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
                cube.GetComponent<MeshRenderer>().sharedMaterial = sceneMaterial;
                Assert.That(EditorSceneManager.SaveScene(scene, scenePath), Is.True);
            }
            finally
            {
                EditorSceneManager.RestoreSceneManagerSetup(setup);
            }
            _project.groups.Add(new ResourceGroup { id = "scene", selections = new List<ResourceSelection> { Selection(scenePath) } });
            EditorUtility.SetDirty(_project);
            AssetDatabase.SaveAssetIfDirty(_project);
            _workspace = (string)Builder.GetMethod("GetWorkspacePath").Invoke(null, new object[] { _project, EditorUserBuildSettings.activeBuildTarget });
            var first = Execute(Native);
            Assert.That(Value(first, "WasUnityBuildInvoked"), Is.True);
            Assert.That(Value(first, "IsCacheHit"), Is.False);
            Assert.That(Value(first, "ManifestBundleFile").ToString(), Does.StartWith("ylr_" + ResourceBuildMap.Create(_project).ProjectToken + "_"));
            Assert.That(Items(first, "Bundles").Length, Is.EqualTo(6));
            var firstDigests = Digests(first);
            var externallyHeldManifest = AssetBundle.LoadFromFile(Path.Combine(_workspace, (string)Value(first, "ManifestBundleFile")));
            try
            {
                var beforeManifestConflict = Snapshot(Path.GetDirectoryName(_workspace));
                AssertFailure<InvalidOperationException>(() => Execute(Native));
                CollectionAssert.AreEqual(beforeManifestConflict, Snapshot(Path.GetDirectoryName(_workspace)));
                Assert.That((bool)externallyHeldManifest, Is.True);
            }
            finally
            {
                externallyHeldManifest.Unload(true);
            }
            var second = Execute(Native);
            var blocked = Items(second, "CacheReuseBlockedBy").Length != 0;
            Assert.That(Value(second, "IsCacheHit"), Is.EqualTo(!blocked));
            Assert.That(Value(second, "WasUnityBuildInvoked"), Is.EqualTo(blocked));
            Assert.That(Value(second, "ForceRebuild"), Is.EqualTo(blocked));
            CollectionAssert.AreEqual(firstDigests, Digests(second));
            Assert.That(Value(second, "CopiedBytes"), Is.EqualTo(0));
            Assert.That(Value(second, "ArtifactHashBytes"), Is.GreaterThan(0), "Cache metadata/mtime is not integrity evidence.");

            var map = ResourceBuildMap.Create(_project);
            var loaded = new Dictionary<string, AssetBundle>(StringComparer.Ordinal);
            try
            {
                foreach (var bundle in map.Bundles.OrderBy(x => x.Dependencies.Length))
                {
                    loaded.Add(bundle.Name, AssetBundle.LoadFromFile(Path.Combine(_workspace, bundle.Name)));
                }

                var pa = loaded[map.Assets.Single(x => x.SourcePath == a).BundleName].LoadAsset<GameObject>(a);
                var pb = loaded[map.Assets.Single(x => x.SourcePath == b).BundleName].LoadAsset<GameObject>(b);
                Assert.That(pa.GetComponent<Animator>().runtimeAnimatorController, Is.Not.Null);
                Assert.That(pa.GetComponent<MeshRenderer>().sharedMaterial.mainTexture, Is.SameAs(pb.GetComponent<MeshRenderer>().sharedMaterial.mainTexture));
                var blockedBefore = Snapshot(Path.GetDirectoryName(_workspace));
                AssertFailure<InvalidOperationException>(() => Execute(Native));
                CollectionAssert.AreEqual(blockedBefore, Snapshot(Path.GetDirectoryName(_workspace)));
                Assert.That(pa, Is.Not.Null, "The build must not unload externally held native bundles.");
            }
            finally
            {
                foreach (var bundle in loaded.Values.Reverse())
                {
                    if (bundle)
                    {
                        bundle.Unload(true);
                    }
                }
            }

            File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(_text), "second genuinely changed owned content");
            AssetDatabase.ImportAsset(_text, ImportAssetOptions.ForceSynchronousImport);
            var third = Execute(Native);
            Assert.That(Value(third, "WasUnityBuildInvoked"), Is.True);
            Assert.That(Value(third, "InputFingerprint"), Is.Not.EqualTo(Value(first, "InputFingerprint")));
            var textBundle = map.Assets.Single(x => x.SourcePath == _text).BundleName;
            Assert.That(Digest(third, textBundle), Is.Not.EqualTo(Digest(first, textBundle)));
            foreach (var bundle in map.Bundles.Where(x => x.Name != textBundle))
            {
                Assert.That(Digest(third, bundle.Name), Is.EqualTo(Digest(first, bundle.Name)), bundle.Name);
            }

            var nativeText = AssetBundle.LoadFromFile(Path.Combine(_workspace, textBundle));
            try
            {
                Assert.That(nativeText.LoadAsset<TextAsset>(_text).text, Is.EqualTo("second genuinely changed owned content"));
            }
            finally
            {
                nativeText.Unload(true);
            }
            File.AppendAllText(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(_workspace, textBundle)), "corrupt suffix");
            var repaired = Execute(Native);
            Assert.That(Value(repaired, "WasUnityBuildInvoked"), Is.True);
            Assert.That(Value(repaired, "ForceRebuild"), Is.True);
            Assert.That(Digest(repaired, textBundle), Is.EqualTo(Digest(third, textBundle)));
            CollectionAssert.AreEqual(protectedBefore, ProtectedSnapshot());
            _retainWorkspace = true;
            UnityEngine.Debug.Log("REAL_82A workspace=" + _workspace + "\n" + Metrics("initial", first) + "\n" + Metrics("unchanged", second) + "\n" + Metrics("changed", third) + "\n" + Metrics("repaired", repaired));
        }

        private string Prefab(string name, Texture texture)
        {
            var material = new Material(Shader.Find("Unlit/Texture")) { name = name, mainTexture = texture };
            AssetDatabase.CreateAsset(material, _folder + "/Content/" + name + ".mat");
            var go = new GameObject(name);
            try
            {
                go.AddComponent<MeshRenderer>().sharedMaterial = material;
                var path = _folder + "/Content/" + name + ".prefab";
                PrefabUtility.SaveAsPrefabAsset(go, path);
                return path;
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        private static ResourceSelection Selection(string path) => new ResourceSelection { guid = AssetDatabase.AssetPathToGUID(path) };
        private static AssetBundleManifest Native(string output, AssetBundleBuild[] builds, BuildAssetBundleOptions options, BuildTarget target) =>
            BuildPipeline.BuildAssetBundles(output, builds, options, target);

        private object Execute(Pipeline pipeline, CancellationToken cancellation = default)
        {
            var method = Builder.GetMethod("Execute", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);
            var function = new Func<string, AssetBundleBuild[], BuildAssetBundleOptions, BuildTarget, AssetBundleManifest>(pipeline.Invoke);
            return method.Invoke(null, new object[] { _project, _target, false, cancellation, function });
        }
        private static void AssertFailure<T>(Action action) where T : Exception
        {
            var exception = Assert.Throws<TargetInvocationException>(() => action());
            Assert.That(exception.InnerException, Is.TypeOf<T>(), exception.InnerException?.ToString());
        }
        private static object Value(object value, string property) => value.GetType().GetProperty(property).GetValue(value);
        private static object[] Items(object value, string property) => ((IEnumerable)Value(value, property)).Cast<object>().ToArray();
        private static string[] Digests(object result) => Items(result, "Files").Select(x => Value(x, "Name") + ":" + Value(x, "Sha256")).ToArray();
        private static string Digest(object result, string name) => (string)Value(Items(result, "Files").Single(x => (string)Value(x, "Name") == name), "Sha256");
        private static string Metrics(string label, object result) => label + " " + string.Join("; ", new[] { "IsCacheHit", "WasUnityBuildInvoked", "ForceRebuild", "PreflightMilliseconds", "BuildMilliseconds", "ValidationMilliseconds", "SourceHashBytes", "ArtifactHashBytes", "CopiedBytes" }.Select(x => x + "=" + Value(result, x))) + "; untrackedCallbacks=" + Items(result, "CacheReuseBlockedBy").Length;
        private static string[] Snapshot(string root)
        {
            if (!Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(root)))
            {
                return Array.Empty<string>();
            }

            using var sha = SHA256.Create();
            return Directory.GetFiles(global::YLFramework.Testing.PackageTestPaths.Physical(root), "*", SearchOption.AllDirectories).Where(x => Path.GetFileName(x) != ".ylr-workspace.lock").OrderBy(x => x, StringComparer.Ordinal)
                .Select(x =>
                {
                    using var stream = File.OpenRead(global::YLFramework.Testing.PackageTestPaths.Physical(x));
                    return x.Substring(root.Length) + ":" + BitConverter.ToString(sha.ComputeHash(stream));
                }).ToArray();
        }
        private static string[] ProtectedSnapshot()
        {
            var root = Path.GetDirectoryName(Application.dataPath);
            return new[] { "Assets/Games", "Assets/Scenes", "Assets/StreamingAssets", "AssetBundles/MainModule", "Packages", "ProjectSettings" }
                .SelectMany(x => Snapshot(Path.Combine(root, x)).Select(file => x + file)).ToArray();
        }
    }
}
