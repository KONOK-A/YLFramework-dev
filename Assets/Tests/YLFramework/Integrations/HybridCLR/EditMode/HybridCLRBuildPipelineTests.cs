using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading;
using dnlib.DotNet;
using Newtonsoft.Json;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using YLFramework.Editor.Resource;
using YLFramework.Integrations.HybridCLR;
using YLFramework.Integrations.HybridCLR.Editor;

namespace YLFramework.Tests.Integrations.HybridCLR
{
    public class HybridCLRBuildPipelineTests
    {
        private string _root;
        private string _rootGuid;
        private string _temporary;
        private HybridCLRBuildProfile _profile;
        private string _workspaceRoot;
        private string _releaseRoot;

        [SetUp]
        public void SetUp()
        {
            var id = Guid.NewGuid().ToString("N");
            _root = "Assets/Tests/YLFramework/Fixtures/HybridCLRBuild-" + id;
            Assert.That(Directory.Exists(_root) || File.Exists(_root + ".meta"), Is.False);
            _rootGuid = AssetDatabase.CreateFolder("Assets/Tests/YLFramework/Fixtures", "HybridCLRBuild-" + id);
            _temporary = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "kilo", "YLHybridCLRBuild-" + id);
            Directory.CreateDirectory(global::YLFramework.Testing.PackageTestPaths.Physical(_temporary));
            var project = ScriptableObject.CreateInstance<ResourceProject>();
            project.projectId = "hot-" + id;
            AssetDatabase.CreateAsset(project, _root + "/Project.asset");
            var recipe = ScriptableObject.CreateInstance<ResourceBuildRecipe>();
            recipe.project = project;
            recipe.action = ResourceBuildAction.BuildAndPublish;
            recipe.appContentCompatibilityId = "base-v1";
            AssetDatabase.CreateAsset(recipe, _root + "/Recipe.asset");
            _profile = ScriptableObject.CreateInstance<HybridCLRBuildProfile>();
            _profile.recipe = recipe;
            _profile.generatedAssetDirectory = _root + "/Generated";
            _profile.baselineDirectory = System.IO.Path.GetFullPath(global::YLFramework.Testing.PackageTestPaths.Physical("HybridCLRBaselines/" + id));
            AssetDatabase.CreateAsset(_profile, _root + "/Profile.asset");
            AssetDatabase.SaveAssetIfDirty(project);
            AssetDatabase.SaveAssetIfDirty(recipe);
            AssetDatabase.SaveAssetIfDirty(_profile);
        }

        [TearDown]
        public void TearDown()
        {
            Assert.That(AssetDatabase.AssetPathToGUID(_root), Is.EqualTo(_rootGuid));
            AssetDatabase.DeleteAsset(_root);
            if (Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(_temporary)))
            {
                Directory.Delete(global::YLFramework.Testing.PackageTestPaths.Physical(_temporary), true);
            }

            if (_workspaceRoot != null && Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(_workspaceRoot)))
            {
                Directory.Delete(global::YLFramework.Testing.PackageTestPaths.Physical(_workspaceRoot), true);
            }

            if (_releaseRoot != null && Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(_releaseRoot)))
            {
                Directory.Delete(global::YLFramework.Testing.PackageTestPaths.Physical(_releaseRoot), true);
            }
        }

        [Test]
        public void InvalidProfileReportsThePreflightErrorWithoutChangingPlayerSettings()
        {
            var backend = PlayerSettings.GetScriptingBackend(BuildTargetGroup.Standalone);
            var compatibility = PlayerSettings.GetApiCompatibilityLevel(BuildTargetGroup.Standalone);
            var report = HybridCLRBuildPipeline.Validate(null);
            Assert.That(report.HasSucceeded, Is.False);
            Assert.That(report.Stages.Single().Name, Is.EqualTo("Preflight"));
            Assert.That(report.Stages.Single().Error, Is.TypeOf<ArgumentNullException>());
            Assert.That(PlayerSettings.GetScriptingBackend(BuildTargetGroup.Standalone), Is.EqualTo(backend));
            Assert.That(PlayerSettings.GetApiCompatibilityLevel(BuildTargetGroup.Standalone), Is.EqualTo(compatibility));
        }

        [TestCase("recipe")]
        [TestCase("development")]
        [TestCase("generatedAssetDirectory")]
        [TestCase("baselineDirectory")]
        [TestCase("hotUpdateAssemblies")]
        [TestCase("baseProfile")]
        public void CurrentProfileFieldsDoNotRetainLegacySerializedReaders(string name)
        {
            var field = typeof(HybridCLRBuildProfile).GetField(name);
            Assert.That(field, Is.Not.Null);
            Assert.That(field.GetCustomAttributes<UnityEngine.Serialization.FormerlySerializedAsAttribute>(), Is.Empty,
                "Current formally saved profile inputs must not require the retired field reader: " + name);
        }

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public void CurrentSavedProfilesColdReadBooleansRootsReferencesAndSettingsFingerprint(bool development, bool selectRoots)
        {
            _profile.development = development;
            EditorUtility.SetDirty(_profile);
            AssetDatabase.SaveAssetIfDirty(_profile);
            var child = ScriptableObject.CreateInstance<HybridCLRBuildProfile>();
            child.recipe = _profile.recipe;
            child.development = development;
            child.generatedAssetDirectory = _root + "/ChildGenerated";
            child.baselineDirectory = _profile.baselineDirectory;
            child.hotUpdateAssemblies = selectRoots ? new[] { "Current.Entry", "Current.Support" } : Array.Empty<string>();
            child.baseProfile = _profile;
            string path = _root + "/CurrentProfile.asset";
            AssetDatabase.CreateAsset(child, path);
            AssetDatabase.SaveAssetIfDirty(child);
            var fingerprintMethod = typeof(HybridCLRBuildPipeline).GetMethod("SettingsFingerprint",
                BindingFlags.NonPublic | BindingFlags.Static);
            string Fingerprint(HybridCLRBuildProfile value)
            {
                var settings = global::HybridCLR.Settings.HybridCLRSettings.instance;
                var definitions = settings.hotUpdateAssemblyDefinitions;
                var names = settings.hotUpdateAssemblies;
                var preserved = settings.preserveHotUpdateAssemblies;
                try
                {
                    settings.hotUpdateAssemblyDefinitions = Array.Empty<UnityEditorInternal.AssemblyDefinitionAsset>();
                    settings.hotUpdateAssemblies = new[] { "Current.Entry", "Current.Support" };
                    settings.preserveHotUpdateAssemblies = Array.Empty<string>();
                    return (string)fingerprintMethod.Invoke(null, new object[] { value });
                }
                finally
                {
                    settings.hotUpdateAssemblyDefinitions = definitions;
                    settings.hotUpdateAssemblies = names;
                    settings.preserveHotUpdateAssemblies = preserved;
                }
            }

            string fingerprint = Fingerprint(child);
            var paths = new[]
            {
                path, AssetDatabase.GetAssetPath(_profile), AssetDatabase.GetAssetPath(_profile.recipe),
                AssetDatabase.GetAssetPath(_profile.recipe.project)
            };
            var guids = paths.Select(AssetDatabase.AssetPathToGUID).ToArray();
            var bytes = paths.Select(value => File.ReadAllBytes(value)).ToArray();
            var metas = paths.Select(value => File.ReadAllBytes(value + ".meta")).ToArray();
            Assert.That(AssetDatabase.TryGetGUIDAndLocalFileIdentifier(child, out string childGuid, out long childId), Is.True);
            Assert.That(AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(MonoScript.FromScriptableObject(child))),
                Is.EqualTo("fa0a9e25cfd87fc4987e563e9e6a591d"));
            var recipe = _profile.recipe;
            var project = recipe.project;
            var generated = child.generatedAssetDirectory;
            var baseline = child.baselineDirectory;
            Resources.UnloadAsset(child);
            Resources.UnloadAsset(_profile);
            Resources.UnloadAsset(recipe);
            Resources.UnloadAsset(project);
            Assert.That((bool)child || (bool)_profile || (bool)recipe || (bool)project, Is.False);

            var reloaded = AssetDatabase.LoadAssetAtPath<HybridCLRBuildProfile>(path);
            _profile = reloaded.baseProfile;
            using (var serialized = new SerializedObject(reloaded))
            {
                Assert.That(serialized.FindProperty("recipe").objectReferenceValue, Is.SameAs(_profile.recipe));
                Assert.That(serialized.FindProperty("baseProfile").objectReferenceValue, Is.SameAs(_profile));
                Assert.That(serialized.FindProperty("development").boolValue, Is.EqualTo(development));
            }
            Assert.That(ReferenceEquals(reloaded, child), Is.False);
            Assert.That(AssetDatabase.TryGetGUIDAndLocalFileIdentifier(reloaded, out string restoredGuid, out long restoredId), Is.True);
            Assert.That(restoredGuid, Is.EqualTo(childGuid));
            Assert.That(restoredId, Is.EqualTo(childId));
            Assert.That(reloaded.generatedAssetDirectory, Is.EqualTo(generated));
            Assert.That(reloaded.baselineDirectory, Is.EqualTo(baseline));
            Assert.That(reloaded.hotUpdateAssemblies, Is.EqualTo(selectRoots ? new[] { "Current.Entry", "Current.Support" } : Array.Empty<string>()));
            Assert.That(_profile.development, Is.EqualTo(development));
            Assert.That(reloaded.recipe.project.projectId, Does.StartWith("hot-"));
            Assert.That(reloaded.recipe.appContentCompatibilityId, Is.EqualTo("base-v1"));
            Assert.That(Fingerprint(reloaded), Is.EqualTo(fingerprint));
            var output = Environment.GetEnvironmentVariable("YL_TEST_OUTPUT_ROOT");
            Assert.That(Directory.Exists(output), Is.True, "Run this contract through the declared owned native host.");
            var evidence = Path.Combine(output, "HybridCLRCurrentProfiles-" + development + "-" + selectRoots);
            Assert.That(Directory.Exists(evidence), Is.False);
            Directory.CreateDirectory(evidence);
            for (var i = 0; i < paths.Length; i++)
            {
                Assert.That(AssetDatabase.AssetPathToGUID(paths[i]), Is.EqualTo(guids[i]));
                CollectionAssert.AreEqual(bytes[i], File.ReadAllBytes(paths[i]));
                CollectionAssert.AreEqual(metas[i], File.ReadAllBytes(paths[i] + ".meta"));
                File.WriteAllBytes(Path.Combine(evidence, Path.GetFileName(paths[i])), bytes[i]);
                File.WriteAllBytes(Path.Combine(evidence, Path.GetFileName(paths[i]) + ".meta"), metas[i]);
                TestContext.WriteLine("Current profile input: {0}; GUID={1}", paths[i], guids[i]);
            }
        }

        [Test]
        public void AnInvalidBuildStopsBeforeCompilePayloadPublicationAndBaseline()
        {
            var report = HybridCLRBuildPipeline.BuildBasePlayer(null, new BuildPlayerOptions());
            Assert.That(report.HasSucceeded, Is.False);
            Assert.That(report.Stages.Select(s => s.Name), Is.EqualTo(new[] { "Preflight" }));
            Assert.That(report.Release, Is.Null);
            Assert.That(report.Manifest, Is.Null);
            Assert.That(report.PlayerBuild, Is.Null);
            Assert.That(Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(_profile.generatedAssetDirectory)), Is.False);
            Assert.That(Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(_profile.baselineDirectory)), Is.False);
        }

        [TestCase("library")]
        [TestCase("temporary")]
        [TestCase("assets")]
        [TestCase("overlap")]
        public void BaselinesMustBePersistentAndSeparateFromGeneratedInputs(string invalid)
        {
            switch (invalid)
            {
                case "library":
                    _profile.baselineDirectory = "Library/HybridCLR/OldAot";
                    break;
                case "temporary":
                    _profile.baselineDirectory = "Temp/OldAot";
                    break;
                case "assets":
                    _profile.baselineDirectory = _root + "/NativeBaseline";
                    break;
                default:
                    _profile.baselineDirectory = _profile.generatedAssetDirectory;
                    break;
            }
            Assert.Throws<InvalidDataException>(() => HybridCLRBuildPipeline.ValidateInputPaths(_profile));
        }

        [Test]
        public void UnownedFilesAreRejectedWithoutOverwritingThem()
        {
            Directory.CreateDirectory(global::YLFramework.Testing.PackageTestPaths.Physical(_profile.generatedAssetDirectory));
            var file = _profile.generatedAssetDirectory + "/user-file.bytes";
            File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(file), "user-content");
            Assert.Throws<InvalidDataException>(() => HybridCLRBuildPipeline.ValidateInputPaths(_profile));
            Assert.That(File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(file)), Is.EqualTo("user-content"));
        }

        [Test]
        public void EmptyPredeclaredPayloadFoldersRetainTheirMetaAndCanBeInitialized()
        {
            AssetDatabase.CreateFolder(_root, "Generated");
            AssetDatabase.CreateFolder(_profile.generatedAssetDirectory, "hybridclr");
            AssetDatabase.CreateFolder(_profile.generatedAssetDirectory + "/hybridclr", "assemblies");
            var before = AssetDatabase.AssetPathToGUID(_profile.generatedAssetDirectory + "/hybridclr/assemblies");
            var baseline = CreateFormatFixture();
            var inputs = FixtureInputs();
            Assert.DoesNotThrow(() => HybridCLRBuildPipeline.CommitPayloads(_profile, baseline.InitialManifest, inputs));
            Assert.That(AssetDatabase.AssetPathToGUID(_profile.generatedAssetDirectory + "/hybridclr/assemblies"), Is.EqualTo(before));
        }

        [Test]
        public void UnchangedOwnedBytesDoNotAttemptAnotherAtomicReplacement()
        {
            var bytes = new byte[] { 1, 2, 3 };
            var path = _temporary + "/owned.bytes";
            File.WriteAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(path), bytes);
            Assert.DoesNotThrow(() => HybridCLRBuildPipeline.WriteOwned(_temporary, "owned.bytes", bytes,
                (_, _) => throw new InvalidOperationException("An unchanged payload should not be replaced."), _ => { }));
            CollectionAssert.AreEqual(bytes, File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(path)));
        }

        [Test]
        public void AtomicReplaceRetriesOnlyTheKnownNonDestructiveWindowsError()
        {
            var original = new byte[] { 1, 2, 3 };
            var replacement = new byte[] { 4, 5, 6 };
            File.WriteAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(_temporary + "/owned.bytes"), original);
            var calls = 0;
            Assert.DoesNotThrow(() => HybridCLRBuildPipeline.WriteOwned(_temporary, "owned.bytes", replacement, (from, to) =>
            {
                calls++;
                if (calls <= 2)
                {
                    throw new IOException("Intentional Win32 1175 atomic replacement failure.", unchecked((int)0x80070497));
                }

                File.Replace(from, to, null);
            }, _ => { }));
            Assert.That(calls, Is.EqualTo(3));
            CollectionAssert.AreEqual(replacement, File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(_temporary + "/owned.bytes")));
        }

        [Test]
        public void AnEmptyStartupPackageInfoGitFieldIsBackedByExactManifestAndLockEvidence()
        {
            const string Url = "https://gitee.com/focus-creative-games/hybridclr_unity.git#v9.0.3";
            const string Hash = "6cadd24d101f4cbd00cd80190106f6621132d24d";
            Assert.DoesNotThrow(() => HybridCLRBuildPipeline.ValidateGitProvenance("9.0.3", "Git", Url, "git", Url, Hash, "", ""));
            Assert.Throws<InvalidOperationException>(() => HybridCLRBuildPipeline.ValidateGitProvenance("9.0.3", "Git", Url, "git", Url, new string('a', 40), "", ""));
            Assert.Throws<InvalidOperationException>(() => HybridCLRBuildPipeline.ValidateGitProvenance("9.0.3", "Git", Url, "git", Url, Hash, "v7.3.0", Hash));
            Assert.Throws<InvalidOperationException>(() => HybridCLRBuildPipeline.ValidateGitProvenance("9.0.3", "Embedded", Url, "git", Url, Hash, "", ""));
        }

        [Test]
        public void SuccessfulBuildReportWithoutAnExecutableAndNativeAssemblyIsNotPlayerSuccess()
        {
            var exe = _temporary + "/NotARealPlayer.exe";
            Assert.Throws<InvalidDataException>(() => HybridCLRBuildPipeline.ValidatePlayerOutputs(exe, exe));
            File.WriteAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(exe), new byte[] { 1, 2, 3 });
            Assert.Throws<InvalidDataException>(() => HybridCLRBuildPipeline.ValidatePlayerOutputs(exe, exe));
            File.WriteAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(_temporary + "/GameAssembly.dll"), new byte[] { 4, 5, 6 });
            Assert.Throws<InvalidDataException>(() => HybridCLRBuildPipeline.ValidatePlayerOutputs(exe, _temporary + "/Other.exe"));
        }

        [Test]
        public void NativeTypeRequirementsPreserveTheAssemblyOfNestedSameNamedValueTypeArguments()
        {
            using var root = new ModuleDefUser("GenericRoot.dll") { Kind = ModuleKind.Dll };
            using var first = new ModuleDefUser("PayloadA.dll") { Kind = ModuleKind.Dll };
            using var second = new ModuleDefUser("PayloadB.dll") { Kind = ModuleKind.Dll };
            new AssemblyDefUser("GenericRoot", new Version(1, 0, 0, 0)).Modules.Add(root);
            new AssemblyDefUser("PayloadA", new Version(1, 0, 0, 0)).Modules.Add(first);
            new AssemblyDefUser("PayloadB", new Version(1, 0, 0, 0)).Modules.Add(second);
            var box = new TypeDefUser("Fixture", "Box`1", root.CorLibTypes.Object.TypeDefOrRef);
            root.Types.Add(box);
            var nested = new TypeDefUser("Fixture", "Nested`1", root.CorLibTypes.Object.TypeDefOrRef);
            root.Types.Add(nested);
            var a = new TypeDefUser("Shared", "Payload", first.CorLibTypes.GetTypeRef("System", "ValueType"));
            first.Types.Add(a);
            var b = new TypeDefUser("Shared", "Payload", second.CorLibTypes.GetTypeRef("System", "ValueType"));
            second.Types.Add(b);
            var left = new global::HybridCLR.Meta.GenericClass(box, new System.Collections.Generic.List<TypeSig>
                { new GenericInstSig(new ClassSig(nested), new ValueTypeSig(a)) });
            var right = new global::HybridCLR.Meta.GenericClass(box, new System.Collections.Generic.List<TypeSig>
                { new GenericInstSig(new ClassSig(nested), new ValueTypeSig(b)) });
            Assert.That(left.ToTypeSig().FullName, Is.EqualTo(right.ToTypeSig().FullName), "This must cover a real scope collision, not different type names.");
            Assert.That(HybridCLRBuildPipeline.NativeTypeRequirement(left), Is.Not.EqualTo(HybridCLRBuildPipeline.NativeTypeRequirement(right)));
        }

        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(true, true)]
        public void ScriptOnlyOrVisualStudioExportIsNotACompletedNativeExecutableBuild(bool scripts, bool solution) =>
            Assert.Throws<InvalidDataException>(() => HybridCLRBuildPipeline.ValidateExecutableBuildMode(scripts, solution));

        [Test]
        public void NativeGenericTypeRequirementsDistinguishDifferentValueTypeInstantiations()
        {
            using var module = new ModuleDefUser("Fixture.dll") { Kind = ModuleKind.Dll };
            new AssemblyDefUser("Fixture", new Version(1, 0, 0, 0)).Modules.Add(module);
            var type = new TypeDefUser("Fixture", "Box`1", module.CorLibTypes.Object.TypeDefOrRef);
            module.Types.Add(type);
            var first = new global::HybridCLR.Meta.GenericClass(type, new System.Collections.Generic.List<TypeSig> { module.CorLibTypes.Int32 });
            var second = new global::HybridCLR.Meta.GenericClass(type, new System.Collections.Generic.List<TypeSig> { module.CorLibTypes.Int64 });
            Assert.That(HybridCLRBuildPipeline.NativeTypeRequirement(first), Is.Not.EqualTo(HybridCLRBuildPipeline.NativeTypeRequirement(second)));
        }

        [Test]
        public void MissingNativeToolsFailBeforeAnInstallerOrPlayerBuildIsStarted()
        {
            Assert.Throws<InvalidOperationException>(() => HybridCLRBuildPipeline.ValidateNativeToolchain(_temporary + "/missing.exe", _temporary));
        }

        [Test]
        public void PlayerOptionsCannotBuildAnotherTargetOrOverwriteAnotherPlayer()
        {
            var options = new BuildPlayerOptions { target = BuildTarget.Android, targetGroup = BuildTargetGroup.Android };
            Assert.Throws<InvalidDataException>(() => HybridCLRBuildPipeline.ValidatePlayerOptions(_profile, options));
            Assert.That(Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(_profile.baselineDirectory)), Is.False);
        }

        [Test]
        public void NoProfilePlayerGuardDoesNotReadOrChangeGlobalSettings()
        {
            var backend = PlayerSettings.GetScriptingBackend(BuildTargetGroup.Standalone);
            Assert.DoesNotThrow(() => new HybridCLRPlayerBuildGuard().OnPreprocessBuild(null));
            Assert.That(PlayerSettings.GetScriptingBackend(BuildTargetGroup.Standalone), Is.EqualTo(backend));
        }

        [Test]
        public void UnrelatedEmptyGeneratedDirectoryIsNotClaimed()
        {
            Directory.CreateDirectory(global::YLFramework.Testing.PackageTestPaths.Physical(_profile.generatedAssetDirectory + "/UserData"));
            Assert.Throws<InvalidDataException>(() => HybridCLRBuildPipeline.ValidateInputPaths(_profile));
            Assert.That(Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(_profile.generatedAssetDirectory + "/UserData")), Is.True);
        }

        [TestCase("7.3.0", "9.0.3", "9.0.3")]
        [TestCase("9.0.3", "9.0.4", "9.0.3")]
        [TestCase("9.0.3", "9.0.3", null)]
        [TestCase("9.0.3", "9.0.3", "9.0.3\n")]
        public void PackageAndInstalledVersionsMustMatchExactly(string resolved, string declared, string installed)
        {
            Assert.Throws<InvalidOperationException>(() => HybridCLRBuildPipeline.ValidateSupportedVersions(resolved, declared, installed));
        }

        [Test]
        public void RepeatedPayloadCommitPreservesGuidAndBadInputCannotReplaceTheManifest()
        {
            var baseline = CreateFormatFixture();
            var inputs = FixtureInputs();
            HybridCLRBuildPipeline.CommitPayloads(_profile, baseline.InitialManifest, inputs);
            var address = baseline.InitialManifest.HotUpdateAssemblies[0].Address;
            var path = _profile.generatedAssetDirectory + "/" + address;
            var guid = AssetDatabase.AssetPathToGUID(path);
            var manifestPath = _profile.generatedAssetDirectory + "/" + HybridCLRManifest.ManifestPath;
            var original = File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(manifestPath));
            HybridCLRBuildPipeline.CommitPayloads(_profile, baseline.InitialManifest, inputs);
            Assert.That(AssetDatabase.AssetPathToGUID(path), Is.EqualTo(guid));
            File.WriteAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(inputs.HotUpdate.Single().Path), new byte[] { 1, 2, 3 });
            Assert.Throws<InvalidDataException>(() => HybridCLRBuildPipeline.CommitPayloads(_profile, baseline.InitialManifest, inputs));
            CollectionAssert.AreEqual(original, File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(manifestPath)));
            Assert.That(AssetImporter.GetAtPath(path).assetBundleName, Is.Empty);
        }

        [Test]
        public void RealDllBytesPublishThroughResourceAndCancellationPreservesTheOldRelease()
        {
            var baseline = CreateFormatFixture();
            var inputs = FixtureInputs();
            HybridCLRBuildPipeline.CommitPayloads(_profile, baseline.InitialManifest, inputs);
            ConfigureResourceSelection();
            var report = new HybridCLRBuildReport { Manifest = baseline.InitialManifest };
            HybridCLRBuildPipeline.PublishResources(report, _profile, default, null);
            Assert.That(report.Release, Is.Not.Null, report.ResourceBuild?.Summary);
            var digest = report.Release.Catalog.CatalogDigest;
            var oldCatalog = File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(report.Release.DirectoryPath + "/resource-catalog.json"));
            var canceled = new HybridCLRBuildReport { Manifest = baseline.InitialManifest };
            Assert.Throws<OperationCanceledException>(() => HybridCLRBuildPipeline.PublishResources(canceled, _profile, new CancellationToken(true), null));
            Assert.That(canceled.Release, Is.Null);
            Assert.That(ResourcePublisher.ReadCurrent(_profile.recipe.project, _profile.recipe.target, "base-v1").Catalog.CatalogDigest, Is.EqualTo(digest));
            CollectionAssert.AreEqual(oldCatalog, File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(report.Release.DirectoryPath + "/resource-catalog.json")));
        }

        [Test]
        public void PatchCannotAddAnAotDependencyOrChangeTheRegisteredAssemblySet()
        {
            var baseline = CreateFormatFixture();
            var changed = WriteAssembly("Hot.Entry", "Base");
            var inputs = HybridCLRAssemblyAnalysis.Analyze(new[] { changed }, new[] { _temporary + "/Aot/Base.dll" });
            Assert.Throws<InvalidDataException>(() => HybridCLRBuildPipeline.ValidatePatchSet(baseline, inputs));
            var renamed = HybridCLRAssemblyAnalysis.Analyze(new[] { WriteAssembly("Hot.Other") }, new[] { _temporary + "/Aot/Base.dll" });
            Assert.Throws<InvalidDataException>(() => HybridCLRBuildPipeline.ValidatePatchSet(baseline, renamed));
        }

        [Test]
        public void ProjectManifestIncludesItsDependencyClosureButNotAnotherProject()
        {
            var inputs = ProjectInputs();
            SelectProjectAssemblies(_profile, "Project.A");
            var manifest = ProjectManifest(_profile, inputs);
            Assert.That(manifest.HotUpdateAssemblies.Select(a => a.Name), Is.EqualTo(new[] { "Shared", "Project.A" }));
            Assert.That(manifest.HotUpdateAssemblies.Single(a => a.Name == "Project.A").Dependencies, Is.EqualTo(new[] { "Shared" }));
            Assert.That(inputs.HotUpdate.Select(a => a.Name), Does.Contain("Project.B"), "Selecting a project must not change the native registration input.");
        }

        [Test]
        public void ProjectMetadataDoesNotIncludeAnUnselectedProjectsAotDependency()
        {
            var inputs = HybridCLRAssemblyAnalysis.Analyze(new[] { WriteAssembly("Project.A"), WriteAssembly("Project.B", "Base") },
                new[] { WriteAssembly("Base") });
            SelectProjectAssemblies(_profile, "Project.A");
            Assert.That(ProjectManifest(_profile, inputs).AotMetadata, Is.Empty);
            SelectProjectAssemblies(_profile, "Project.B");
            Assert.That(ProjectManifest(_profile, inputs).AotMetadata.Select(a => a.Name), Is.EqualTo(new[] { "Base" }));
        }

        [TestCase("unknown")]
        [TestCase("duplicate")]
        [TestCase("case-collision")]
        public void InvalidProjectAssemblyRootsAreNotSilentlyPublishedAsTheGlobalSet(string invalid)
        {
            SelectProjectAssemblies(_profile, invalid == "unknown" ? new[] { "Not.Registered" } :
                invalid == "duplicate" ? new[] { "Project.A", "Project.A" } : new[] { "Project.A", "project.a" });
            Assert.Throws<InvalidDataException>(() => ProjectManifest(_profile, ProjectInputs()));
        }

        [Test]
        public void IndependentProjectsCommitOnlyTheirCodeAndPreserveSharedAssemblyBytes()
        {
            var inputs = ProjectInputs();
            SelectProjectAssemblies(_profile, "Project.A");
            var first = ProjectManifest(_profile, inputs);
            HybridCLRBuildPipeline.CommitPayloads(_profile, first, inputs);
            var sharedPath = _profile.generatedAssetDirectory + "/" + HybridCLRManifest.PayloadAddress("Shared", false);
            var sharedBytes = File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(sharedPath));
            var sharedGuid = AssetDatabase.AssetPathToGUID(sharedPath);

            var otherProject = ScriptableObject.CreateInstance<ResourceProject>();
            otherProject.projectId = "second-" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateAsset(otherProject, _root + "/OtherProject.asset");
            var otherRecipe = ScriptableObject.CreateInstance<ResourceBuildRecipe>();
            otherRecipe.project = otherProject;
            otherRecipe.action = ResourceBuildAction.BuildAndPublish;
            otherRecipe.appContentCompatibilityId = "base-v1";
            AssetDatabase.CreateAsset(otherRecipe, _root + "/OtherRecipe.asset");
            var other = ScriptableObject.CreateInstance<HybridCLRBuildProfile>();
            other.recipe = otherRecipe;
            other.generatedAssetDirectory = _root + "/OtherGenerated";
            other.baselineDirectory = _profile.baselineDirectory;
            AssetDatabase.CreateAsset(other, _root + "/OtherProfile.asset");
            SelectProjectAssemblies(other, "Project.B");
            AssetDatabase.SaveAssetIfDirty(otherProject);
            AssetDatabase.SaveAssetIfDirty(otherRecipe);
            AssetDatabase.SaveAssetIfDirty(other);
            HybridCLRBuildPipeline.CommitPayloads(other, ProjectManifest(other, inputs), inputs);

            Assert.That(File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(_profile.generatedAssetDirectory + "/" + HybridCLRManifest.PayloadAddress("Project.B", false))), Is.False);
            Assert.That(File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(other.generatedAssetDirectory + "/" + HybridCLRManifest.PayloadAddress("Project.A", false))), Is.False);
            CollectionAssert.AreEqual(sharedBytes, File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(other.generatedAssetDirectory + "/" + HybridCLRManifest.PayloadAddress("Shared", false))));
            Assert.That(AssetDatabase.AssetPathToGUID(sharedPath), Is.EqualTo(sharedGuid));
            CollectionAssert.AreEqual(sharedBytes, File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(sharedPath)));
        }

        [Test]
        public void SharedBaseBindingRetainsOwnerIdentityWhileChildKeepsItsOwnProject()
        {
            CreateFormatFixture();
            var baseline = HybridCLRBuildBaseline.Read(_temporary);
            _profile.baselineDirectory = _temporary;
            var child = ChildProfile();
            SetBaseProfile(child, _profile);
            var original = File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(_temporary + "/baseline.json"));
            Assert.DoesNotThrow(() => ValidateBinding(child, baseline));
            Assert.That(child.recipe.project.projectId, Is.Not.EqualTo(baseline.InitialManifest.ProjectId));
            Assert.That(baseline.ProfileGuid, Is.EqualTo(AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(_profile))));
            CollectionAssert.AreEqual(original, File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(_temporary + "/baseline.json")));
        }

        [TestCase("unbound")]
        [TestCase("self")]
        [TestCase("chain")]
        [TestCase("directory")]
        [TestCase("compatibility")]
        [TestCase("development")]
        public void ASharedBaseRequiresExplicitUnambiguousMatchingBinding(string invalid)
        {
            CreateFormatFixture();
            var baseline = HybridCLRBuildBaseline.Read(_temporary);
            _profile.baselineDirectory = _temporary;
            var child = ChildProfile();
            SetBaseProfile(child, _profile);
            switch (invalid)
            {
                case "unbound":
                    SetBaseProfile(child, null);
                    break;
                case "self":
                    SetBaseProfile(child, child);
                    break;
                case "chain":
                    SetBaseProfile(_profile, child);
                    break;
                case "directory":
                    child.baselineDirectory += "-different";
                    break;
                case "compatibility":
                    child.recipe.appContentCompatibilityId = "other-base";
                    break;
                default:
                    child.development = !_profile.development;
                    break;
            }
            Assert.Throws<InvalidDataException>(() => ValidateBinding(child, baseline));
        }

        [Test]
        public void FrozenHotFilesWithoutGlobalRegistrationCannotAuthorizeAnotherProject()
        {
            var baseline = CreateFormatFixture();
            var other = HybridCLRAssemblyAnalysis.Read(WriteAssembly("Hot.Unregistered"));
            File.Move(global::YLFramework.Testing.PackageTestPaths.Physical(other.Path), global::YLFramework.Testing.PackageTestPaths.Physical(_temporary + "/HotUpdate/Hot.Unregistered.dll"));
            baseline.Files = baseline.Files.Append(new HybridCLRBaselineFile
            {
                Path = "HotUpdate/Hot.Unregistered.dll",
                Identity = other.Identity,
                Mvid = other.Mvid,
                Length = other.Length,
                Sha256 = other.Sha256
            }).ToArray();
            var bytes = System.Text.Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(baseline));
            File.WriteAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(_temporary + "/baseline.json"), bytes);
            File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(_temporary + "/complete.json"), JsonConvert.SerializeObject(new
            {
                schemaVersion = 1,
                baselineSha256 = HybridCLRManifest.Hash(bytes)
            }));
            Assert.Throws<InvalidDataException>(() => HybridCLRBuildBaseline.Read(_temporary));
        }

        [TestCase(true)]
        [TestCase(false)]
        public void ProjectSubsetNeedsGlobalMetadataRegistrationNotJustAFrozenAotFile(bool registered)
        {
            var baseline = CreateFormatFixture();
            var other = HybridCLRAssemblyAnalysis.Read(WriteAssembly("Hot.Other", "Base"));
            var otherPath = _temporary + "/HotUpdate/Hot.Other.dll";
            File.Move(global::YLFramework.Testing.PackageTestPaths.Physical(other.Path), global::YLFramework.Testing.PackageTestPaths.Physical(otherPath));
            var aot = HybridCLRAssemblyAnalysis.Read(_temporary + "/Aot/Base.dll");
            HybridCLRPayload Payload(HybridCLRAssemblyRecord value, bool metadata) => new HybridCLRPayload
            {
                Name = value.Name,
                Identity = value.Identity,
                Mvid = value.Mvid,
                Length = value.Length,
                Sha256 = value.Sha256,
                Address = HybridCLRManifest.PayloadAddress(value.Name, metadata),
                References = value.References
            };
            baseline.InitialManifest.HotUpdateAssemblies = baseline.InitialManifest.HotUpdateAssemblies.Append(Payload(other, false)).ToArray();
            baseline.InitialManifest.AotMetadata = registered ? new[] { Payload(aot, true) } : Array.Empty<HybridCLRPayload>();
            baseline.InitialManifest.Seal();
            baseline.Files = baseline.Files.Append(new HybridCLRBaselineFile
            {
                Path = "HotUpdate/Hot.Other.dll",
                Identity = other.Identity,
                Mvid = other.Mvid,
                Length = other.Length,
                Sha256 = other.Sha256
            }).ToArray();
            var bytes = System.Text.Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(baseline));
            File.WriteAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(_temporary + "/baseline.json"), bytes);
            File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(_temporary + "/complete.json"), JsonConvert.SerializeObject(new
            {
                schemaVersion = 1,
                baselineSha256 = HybridCLRManifest.Hash(bytes)
            }));
            var frozen = HybridCLRBuildBaseline.Read(_temporary);
            var patch = HybridCLRAssemblyAnalysis.Analyze(new[] { otherPath }, new[] { aot.Path });
            if (registered)
            {
                Assert.DoesNotThrow(() => HybridCLRBuildPipeline.ValidatePatchSet(frozen, patch));
            }
            else
            {
                Assert.Throws<InvalidDataException>(() => HybridCLRBuildPipeline.ValidatePatchSet(frozen, patch));
            }
        }

        private HybridCLRBuildProfile ChildProfile()
        {
            var project = ScriptableObject.CreateInstance<ResourceProject>();
            project.projectId = "child-" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateAsset(project, _root + "/ChildProject.asset");
            var recipe = ScriptableObject.CreateInstance<ResourceBuildRecipe>();
            recipe.project = project;
            recipe.target = _profile.recipe.target;
            recipe.appContentCompatibilityId = _profile.recipe.appContentCompatibilityId;
            AssetDatabase.CreateAsset(recipe, _root + "/ChildRecipe.asset");
            var child = ScriptableObject.CreateInstance<HybridCLRBuildProfile>();
            child.recipe = recipe;
            child.generatedAssetDirectory = _root + "/ChildGenerated";
            child.baselineDirectory = _profile.baselineDirectory;
            child.development = _profile.development;
            AssetDatabase.CreateAsset(child, _root + "/ChildProfile.asset");
            AssetDatabase.SaveAssetIfDirty(project);
            AssetDatabase.SaveAssetIfDirty(recipe);
            AssetDatabase.SaveAssetIfDirty(child);
            return child;
        }

        private static void SetBaseProfile(HybridCLRBuildProfile selected, HybridCLRBuildProfile owner)
        {
            var field = typeof(HybridCLRBuildProfile).GetField("baseProfile");
            Assert.That(field, Is.Not.Null, "A different resource project needs an explicit binding to the native base owner.");
            field.SetValue(selected, owner);
        }

        private static void ValidateBinding(HybridCLRBuildProfile selected, HybridCLRBuildBaseline baseline)
        {
            var method = typeof(HybridCLRBuildPipeline).GetMethod("ValidateBaselineBinding", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(method, Is.Not.Null);
            try
            {
                method.Invoke(null, new object[] { selected, baseline, baseline.SettingsFingerprint });
            }
            catch (TargetInvocationException error) when (error.InnerException != null)
            {
                ExceptionDispatchInfo.Capture(error.InnerException).Throw();
                throw;
            }
        }

        private HybridCLRAssemblySet ProjectInputs() => HybridCLRAssemblyAnalysis.Analyze(new[]
            { WriteAssembly("Shared"), WriteAssembly("Project.A", "Shared"), WriteAssembly("Project.B", "Shared") }, Array.Empty<string>());

        private static void SelectProjectAssemblies(HybridCLRBuildProfile selected, params string[] roots)
        {
            var field = typeof(HybridCLRBuildProfile).GetField("hotUpdateAssemblies");
            Assert.That(field, Is.Not.Null, "A profile must be able to select its own roots from the registered global set.");
            field.SetValue(selected, roots);
        }

        private static HybridCLRManifest ProjectManifest(HybridCLRBuildProfile selected, HybridCLRAssemblySet inputs)
        {
            try
            {
                return (HybridCLRManifest)typeof(HybridCLRBuildPipeline).GetMethod("CreateManifest", BindingFlags.Static | BindingFlags.NonPublic)
                    .Invoke(null, new object[] { selected, inputs });
            }
            catch (TargetInvocationException error) when (error.InnerException != null)
            {
                ExceptionDispatchInfo.Capture(error.InnerException).Throw();
                throw;
            }
        }

        private HybridCLRAssemblySet FixtureInputs() => HybridCLRAssemblyAnalysis.Analyze(new[] { _temporary + "/HotUpdate/Hot.Entry.dll" }, new[] { _temporary + "/Aot/Base.dll" });

        private void ConfigureResourceSelection()
        {
            var project = _profile.recipe.project;
            project.sourceDirectories.Add(new ResourceSourceDirectory { guid = AssetDatabase.AssetPathToGUID(_profile.generatedAssetDirectory) });
            project.groups.Add(new ResourceGroup
            {
                id = "hotupdate",
                selections = new System.Collections.Generic.List<ResourceSelection>
                { new ResourceSelection { guid = AssetDatabase.AssetPathToGUID(_profile.generatedAssetDirectory + "/hybridclr"), extensions = new[] { ".bytes", ".json" } } }
            });
            EditorUtility.SetDirty(project);
            AssetDatabase.SaveAssetIfDirty(project);
            _workspaceRoot = System.IO.Path.GetDirectoryName(ResourceBundleBuilder.GetWorkspacePath(project, _profile.recipe.target));
            _releaseRoot = System.IO.Path.GetFullPath(global::YLFramework.Testing.PackageTestPaths.Physical("AssetBundles/YLFramework/" + project.projectId + "/" + _profile.recipe.target));
            Assert.That(Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(_workspaceRoot)), Is.False);
            Assert.That(Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(_releaseRoot)), Is.False);
        }

        [Test]
        public void AStaleDllNotReportedByTheCompilerCannotBecomeSuccessfulOutput()
        {
            var stale = WriteAssembly("Hot.Entry");
            Assert.That(File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(stale)), Is.True);
            Assert.Throws<InvalidDataException>(() => HybridCLRBuildPipeline.VerifyCompiledOutputs(Array.Empty<string>(), new[] { "Hot.Entry" }));
            Assert.Throws<InvalidDataException>(() => HybridCLRBuildPipeline.VerifyCompiledOutputs(new[] { stale }, new[] { "Another" }));
            Assert.That(HybridCLRBuildPipeline.VerifyCompiledOutputs(new[] { stale }, new[] { "Hot.Entry" }).Single().Name, Is.EqualTo("Hot.Entry"));
        }

        [TestCase("missing-marker")]
        [TestCase("changed-dll")]
        [TestCase("changed-native-file")]
        [TestCase("unknown-file")]
        [TestCase("changed-receipt")]
        public void ACorruptOrIncompleteFrozenBaselineIsNotPatchInput(string invalid)
        {
            var baseline = CreateFormatFixture();
            switch (invalid)
            {
                case "missing-marker":
                    File.Delete(global::YLFramework.Testing.PackageTestPaths.Physical(_temporary + "/complete.json"));
                    break;
                case "changed-dll":
                    File.AppendAllText(global::YLFramework.Testing.PackageTestPaths.Physical(_temporary + "/Aot/Base.dll"), "corrupted");
                    break;
                case "changed-native-file":
                    File.AppendAllText(global::YLFramework.Testing.PackageTestPaths.Physical(_temporary + "/Generated/MethodBridge.inc"), "changed");
                    break;
                case "unknown-file":
                    File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(_temporary + "/unknown.dll"), "unknown");
                    break;
                default:
                    File.AppendAllText(global::YLFramework.Testing.PackageTestPaths.Physical(_temporary + "/baseline.json"), " ");
                    break;
            }
            Assert.Throws<InvalidDataException>(() => HybridCLRBuildBaseline.Read(_temporary));
            Assert.That(File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(_temporary + "/Generated/MethodBridge.inc")), Does.StartWith("format-test-only"));
        }

        [Test]
        public void CompleteFormatFixtureRetainsTheExactDllIdentityAndNativeFileHash()
        {
            var baseline = CreateFormatFixture();
            var loaded = HybridCLRBuildBaseline.Read(_temporary);
            Assert.That(loaded.PlayerBuildHash, Is.EqualTo(baseline.PlayerBuildHash));
            Assert.That(loaded.Files.Single(f => f.Path == "Aot/Base.dll").Identity, Is.EqualTo("Base, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null"));
            Assert.That(loaded.Root, Is.EqualTo(System.IO.Path.GetFullPath(global::YLFramework.Testing.PackageTestPaths.Physical(_temporary))));
        }

        private string WriteAssembly(string name, params string[] dependencies)
        {
            var path = _temporary + "/" + name + ".dll";
            using var module = new ModuleDefUser(name + ".dll") { Kind = ModuleKind.Dll };
            new AssemblyDefUser(name, new Version(1, 0, 0, 0)).Modules.Add(module);
            foreach (var dependency in dependencies)
            {
                module.Types.Add(new TypeDefUser("Fixture", "Uses" + module.Types.Count,
                    new TypeRefUser(module, "Fixture", "Dependency", new AssemblyRefUser(dependency, new Version(1, 0, 0, 0)))));
            }

            module.Write(path);
            return path;
        }

        // 仅测试冻结格式完整性，绝不把此 fixture 作为真实 Player/IL2CPP 成功证据。
        private HybridCLRBuildBaseline CreateFormatFixture()
        {
            Directory.CreateDirectory(global::YLFramework.Testing.PackageTestPaths.Physical(_temporary + "/Aot"));
            Directory.CreateDirectory(global::YLFramework.Testing.PackageTestPaths.Physical(_temporary + "/Generated"));
            var aot = HybridCLRAssemblyAnalysis.Read(WriteAssembly("Base"));
            File.Move(global::YLFramework.Testing.PackageTestPaths.Physical(aot.Path), global::YLFramework.Testing.PackageTestPaths.Physical(_temporary + "/Aot/Base.dll"));
            var hot = HybridCLRAssemblyAnalysis.Read(WriteAssembly("Hot.Entry"));
            var manifest = new HybridCLRManifest
            {
                ProjectId = _profile.recipe.project.projectId,
                AppContentCompatibilityId = "base-v1",
                UnityVersion = Application.unityVersion,
                HotUpdateAssemblies = new[] { new HybridCLRPayload { Name = hot.Name, Identity = hot.Identity, Mvid = hot.Mvid,
                    Address = HybridCLRManifest.PayloadAddress(hot.Name, false), Length = hot.Length, Sha256 = hot.Sha256 } }
            };
            manifest.Seal();
            Directory.CreateDirectory(global::YLFramework.Testing.PackageTestPaths.Physical(_temporary + "/HotUpdate"));
            File.Move(global::YLFramework.Testing.PackageTestPaths.Physical(hot.Path), global::YLFramework.Testing.PackageTestPaths.Physical(_temporary + "/HotUpdate/" + hot.Name + ".dll"));
            var bridge = System.Text.Encoding.UTF8.GetBytes("format-test-only: not a native Player receipt");
            foreach (var name in HybridCLRBuildBaseline.GeneratedNames)
            {
                File.WriteAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(_temporary + "/Generated/" + name), bridge);
            }

            var baseline = new HybridCLRBuildBaseline
            {
                ProfileGuid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(_profile)),
                SettingsFingerprint = new string('a', 64),
                PlayerBuildHash = new string('b', 64),
                NativeRequirements = Array.Empty<string>(),
                InitialManifest = manifest,
                Files = new[] { new HybridCLRBaselineFile { Path = "Aot/Base.dll", Length = aot.Length, Sha256 = aot.Sha256, Identity = aot.Identity, Mvid = aot.Mvid },
                    new HybridCLRBaselineFile { Path = "HotUpdate/" + hot.Name + ".dll", Length = hot.Length, Sha256 = hot.Sha256, Identity = hot.Identity, Mvid = hot.Mvid } }
                    .Concat(HybridCLRBuildBaseline.GeneratedNames.Select(n => new HybridCLRBaselineFile { Path = "Generated/" + n, Length = bridge.Length, Sha256 = HybridCLRManifest.Hash(bridge) })).ToArray()
            };
            var bytes = System.Text.Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(baseline));
            File.WriteAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(_temporary + "/baseline.json"), bytes);
            File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(_temporary + "/complete.json"), JsonConvert.SerializeObject(new
            {
                schemaVersion = 1,
                baselineSha256 = HybridCLRManifest.Hash(bytes)
            }));
            return baseline;
        }
    }
}
