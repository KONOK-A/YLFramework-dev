using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;
using UnityEngine;
using YLFramework.Integrations.HybridCLR.Editor;

namespace YLFramework.Tests.Integrations.HybridCLR
{
    public class HybridCLRBuildInputTests
    {
        private string _root;
        private HybridCLRBuildProfile _profile;
        private AddressableAssetSettings _official;
        private UnityEditorInternal.AssemblyDefinitionAsset[] _definitions;
        private string[] _names;
        private string[] _preserved;

        [SetUp]
        public void SetUp()
        {
            var settings = global::HybridCLR.Settings.HybridCLRSettings.instance;
            _definitions = settings.hotUpdateAssemblyDefinitions;
            _names = settings.hotUpdateAssemblies;
            _preserved = settings.preserveHotUpdateAssemblies;
            settings.hotUpdateAssemblyDefinitions = Array.Empty<UnityEditorInternal.AssemblyDefinitionAsset>();
            settings.hotUpdateAssemblies = new[] { "Fixture.Hot" };
            settings.preserveHotUpdateAssemblies = Array.Empty<string>();

            var name = "HybridCLRInputs-" + Guid.NewGuid().ToString("N");
            _root = "Assets/Tests/YLFramework/Fixtures/" + name;
            AssetDatabase.CreateFolder("Assets/Tests/YLFramework/Fixtures", name);
            var previousDefault = AddressableAssetSettingsDefaultObject.GetSettings(false);
            _official = AddressableAssetSettings.Create(_root, "Settings", true, true);
            _official.DisableCatalogUpdateOnStartup = true;
            _official.BuildRemoteCatalog = true;
            _official.profileSettings.SetValue(_official.activeProfileId, AddressableAssetSettings.kRemoteBuildPath, "Build/HybridCLRInputs/" + name);
            _official.profileSettings.SetValue(_official.activeProfileId, AddressableAssetSettings.kRemoteLoadPath, "https://hybridclr-inputs.invalid/" + name);
            Assert.That(_official.RemoteCatalogBuildPath.SetVariableByName(_official, AddressableAssetSettings.kRemoteBuildPath), Is.True);
            Assert.That(_official.RemoteCatalogLoadPath.SetVariableByName(_official, AddressableAssetSettings.kRemoteLoadPath), Is.True);
            var code = _official.DefaultGroup;
            var bundle = code.GetSchema<BundledAssetGroupSchema>();
            bundle.BuildPath.SetVariableByName(_official, AddressableAssetSettings.kRemoteBuildPath);
            bundle.LoadPath.SetVariableByName(_official, AddressableAssetSettings.kRemoteLoadPath);
            code.GetSchema<ContentUpdateGroupSchema>().StaticContent = false;
            _profile = ScriptableObject.CreateInstance<HybridCLRBuildProfile>();
            _profile.schemaVersion = 2;
            _profile.addressableSettings = _official;
            _profile.codeGroup = code;
            _profile.projectId = name;
            _profile.appContentCompatibilityId = "inputs-v2";
            _profile.target = BuildTarget.StandaloneWindows64;
            _profile.generatedAssetDirectory = _root + "/Generated";
            _profile.baselineDirectory = "HybridCLRBaselines/" + name;
            AssetDatabase.CreateAsset(_profile, _root + "/Profile.asset");
            foreach (var asset in Authoring())
            {
                EditorUtility.SetDirty(asset);
                AssetDatabase.SaveAssetIfDirty(asset);
            }
            AssetDatabase.SaveAssetIfDirty(_profile);
            HybridCLRBuildPipeline.ValidateResourceInputs(_profile);
            Assert.That(AddressableAssetSettingsDefaultObject.GetSettings(false), Is.SameAs(previousDefault), "The fixture must not replace application/default Settings.");
        }

        [TearDown]
        public void TearDown()
        {
            var settings = global::HybridCLR.Settings.HybridCLRSettings.instance;
            settings.hotUpdateAssemblyDefinitions = _definitions;
            settings.hotUpdateAssemblies = _names;
            settings.preserveHotUpdateAssemblies = _preserved;
            AssetDatabase.DeleteAsset(_root);
        }

        [Test]
        public void ReloadsTheSamePersistedInputsAfterAllOriginalWrappersAreUnloaded()
        {
            var snapshot = Capture(_profile);
            var original = _profile;
            Assert.That(AssetDatabase.TryGetGUIDAndLocalFileIdentifier(original, out string guid, out long localId), Is.True);
            var authoring = Authoring();
            var identities = authoring.Select(asset =>
            {
                Assert.That(AssetDatabase.TryGetGUIDAndLocalFileIdentifier(asset, out string assetGuid, out long id), Is.True);
                var path = AssetDatabase.GetAssetPath(asset);
                return (Guid: assetGuid, Id: id, Path: path, Type: asset.GetType(), Bytes: File.ReadAllBytes(path), Meta: File.ReadAllBytes(path + ".meta"));
            }).ToArray();
            Resources.UnloadAsset(original);
            foreach (var asset in authoring.Reverse())
                Resources.UnloadAsset(asset);
            Assert.That(ReferenceEquals(original, null), Is.False);
            Assert.That((bool)original, Is.False);
            Assert.That(authoring.All(asset => !asset), Is.True);

            var restored = Reload(snapshot);
            Assert.That(restored, Is.Not.Null);
            Assert.That(ReferenceEquals(restored, original), Is.False);
            Assert.That(AssetDatabase.TryGetGUIDAndLocalFileIdentifier(restored, out string afterGuid, out long afterId), Is.True);
            Assert.That(afterGuid, Is.EqualTo(guid));
            Assert.That(afterId, Is.EqualTo(localId));
            Assert.That(restored.recipe, Is.Null);
            Assert.That(restored.schemaVersion, Is.EqualTo(2));
            Assert.That(restored.appContentCompatibilityId, Is.EqualTo("inputs-v2"));
            foreach (var identity in identities)
            {
                var asset = AssetDatabase.LoadAllAssetsAtPath(identity.Path).Single(value => value &&
                    AssetDatabase.TryGetGUIDAndLocalFileIdentifier(value, out string savedGuid, out long savedId) && savedGuid == identity.Guid && savedId == identity.Id);
                Assert.That(asset.GetType(), Is.EqualTo(identity.Type));
                CollectionAssert.AreEqual(identity.Bytes, File.ReadAllBytes(identity.Path));
                CollectionAssert.AreEqual(identity.Meta, File.ReadAllBytes(identity.Path + ".meta"));
            }
            _profile = restored;
            _official = restored.addressableSettings;
        }

        [TestCase("profile")]
        [TestCase("settings")]
        [TestCase("group")]
        [TestCase("bundle-schema")]
        [TestCase("update-schema")]
        public void DirtyInputsCannotBecomeAnInitialSnapshot(string input)
        {
            var asset = CapturedAsset(input);
            var path = AssetDatabase.GetAssetPath(asset);
            var bytes = File.ReadAllBytes(path);
            EditorUtility.SetDirty(asset);
            Assert.Throws<InvalidDataException>(() => Capture(_profile));
            CollectionAssert.AreEqual(bytes, File.ReadAllBytes(path));
        }

        [TestCase("profile")]
        [TestCase("settings")]
        [TestCase("group")]
        [TestCase("bundle-schema")]
        [TestCase("update-schema")]
        public void ReloadRejectsDirtyCapturedAuthoringWithoutChangingPersistedBytes(string input)
        {
            var snapshot = Capture(_profile);
            var asset = CapturedAsset(input);
            var path = AssetDatabase.GetAssetPath(asset);
            var bytes = File.ReadAllBytes(path);
            var metadata = File.ReadAllBytes(path + ".meta");
            EditorUtility.SetDirty(asset);
            Assert.Throws<InvalidDataException>(() => Reload(snapshot));
            CollectionAssert.AreEqual(bytes, File.ReadAllBytes(path));
            CollectionAssert.AreEqual(metadata, File.ReadAllBytes(path + ".meta"));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void UnsavedInstancesAndClonesCannotImpersonateSavedProfiles(bool clone)
        {
            var transient = clone ? UnityEngine.Object.Instantiate(_profile) : ScriptableObject.CreateInstance<HybridCLRBuildProfile>();
            try
            {
                Assert.Throws<InvalidDataException>(() => Capture(transient));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(transient);
            }
        }

        [TestCase("entry-subset")]
        [TestCase("generated-root")]
        [TestCase("baseline-root")]
        [TestCase("base-owner")]
        [TestCase("development")]
        [TestCase("settings-binding")]
        [TestCase("group-binding")]
        [TestCase("compatibility")]
        [TestCase("project-id")]
        [TestCase("target")]
        [TestCase("settings-policy")]
        [TestCase("group-schema")]
        public void ReloadRejectsChangedSavedExecutionInputs(string changed)
        {
            var snapshot = Capture(_profile);
            UnityEngine.Object asset = _profile;
            switch (changed)
            {
                case "entry-subset":
                    _profile.hotUpdateAssemblies = new[] { "Other.Entry" };
                    break;
                case "generated-root":
                    _profile.generatedAssetDirectory += "Other";
                    break;
                case "baseline-root":
                    _profile.baselineDirectory += "Other";
                    break;
                case "base-owner":
                    _profile.baseProfile = _profile;
                    break;
                case "development":
                    _profile.development = !_profile.development;
                    break;
                case "settings-binding":
                    AssetDatabase.CreateFolder(_root, "Other");
                    _profile.addressableSettings = AddressableAssetSettings.Create(_root + "/Other", "Settings", true, true);
                    break;
                case "group-binding":
                    _profile.codeGroup = _official.CreateGroup("Other Code", false, false, true, null, typeof(BundledAssetGroupSchema), typeof(ContentUpdateGroupSchema));
                    break;
                case "compatibility":
                    _profile.appContentCompatibilityId = "changed-compatibility";
                    break;
                case "target":
                    _profile.target = BuildTarget.Android;
                    break;
                case "settings-policy":
                    _official.DisableCatalogUpdateOnStartup = false;
                    asset = _official;
                    break;
                case "group-schema":
                    var schema = _profile.codeGroup.GetSchema<BundledAssetGroupSchema>();
                    schema.Compression = schema.Compression == BundledAssetGroupSchema.BundleCompressionMode.Uncompressed ? BundledAssetGroupSchema.BundleCompressionMode.LZ4 : BundledAssetGroupSchema.BundleCompressionMode.Uncompressed;
                    asset = schema;
                    break;
                default:
                    _profile.projectId += "-changed";
                    break;
            }
            EditorUtility.SetDirty(asset);
            AssetDatabase.SaveAssetIfDirty(asset);
            Assert.Throws<InvalidDataException>(() => Reload(snapshot));
        }

        [Test]
        public void ReloadRejectsUnsavedMemoryChangesEvenWithoutAnExplicitDirtyFlag()
        {
            var snapshot = Capture(_profile);
            _profile.hotUpdateAssemblies = new[] { "Unsaved.Entry" };
            Assert.Throws<InvalidDataException>(() => Reload(snapshot));
        }

        [Test]
        public void ANewProfileAtTheSamePathCannotReplaceTheFrozenGuid()
        {
            var snapshot = Capture(_profile);
            var path = AssetDatabase.GetAssetPath(_profile);
            Assert.That(AssetDatabase.TryGetGUIDAndLocalFileIdentifier(_profile, out string guid, out long _), Is.True);
            var replacement = ScriptableObject.CreateInstance<HybridCLRBuildProfile>();
            var replacementPath = _root + "/Replacement.asset";
            AssetDatabase.CreateAsset(replacement, replacementPath);
            Assert.That(AssetDatabase.TryGetGUIDAndLocalFileIdentifier(replacement, out string replacementGuid, out long replacementId), Is.True);
            Assert.That(replacementGuid, Is.Not.EqualTo(guid));
            Assert.That(AssetDatabase.DeleteAsset(path), Is.True);
            Assert.That(AssetDatabase.MoveAsset(replacementPath, path), Is.Empty);
            Assert.That(AssetDatabase.TryGetGUIDAndLocalFileIdentifier(replacement, out string movedGuid, out long movedId), Is.True);
            Assert.That(movedGuid, Is.EqualTo(replacementGuid));
            Assert.That(movedId, Is.EqualTo(replacementId));
            Assert.Throws<InvalidDataException>(() => Reload(snapshot));
        }

        [Test]
        public void ReloadRejectsChangedNativeSettingsInsteadOfRecordingANewEndFingerprint()
        {
            var settings = global::HybridCLR.Settings.HybridCLRSettings.instance;
            var original = settings.enable;
            var snapshot = Capture(_profile);
            try
            {
                settings.enable = !original;
                Assert.Throws<InvalidDataException>(() => Reload(snapshot));
            }
            finally
            {
                settings.enable = original;
            }
        }

        [Test]
        public void OrdinaryBuildsWithoutAnExplicitScopeRemainUntouched() =>
            Assert.DoesNotThrow(() => new HybridCLRPlayerBuildGuard().OnPreprocessBuild(null));

        [Test]
        public void AnActiveScopeCannotSilentlyDisappearWhenItsProfileIsUnloaded()
        {
            using var scope = HybridCLRPlayerBuildGuard.Enter(_profile, false);
            Resources.UnloadAsset(_profile);
            Assert.That((bool)_profile, Is.False);
            Assert.Throws<BuildFailedException>(() => new HybridCLRPlayerBuildGuard().OnPreprocessBuild(null));
        }

        [Test]
        public void AnUnloadedProfileDoesNotAuthorizeANestedBuildScope()
        {
            using var scope = HybridCLRPlayerBuildGuard.Enter(_profile, false);
            var other = UnityEngine.Object.Instantiate(_profile);
            Resources.UnloadAsset(_profile);
            IDisposable nested = null;
            try
            {
                Assert.Throws<InvalidOperationException>(() => nested = HybridCLRPlayerBuildGuard.Enter(other, true));
            }
            finally
            {
                nested?.Dispose();
                UnityEngine.Object.DestroyImmediate(other);
            }
        }

        [Test]
        public void DisposingAnOldScopeTwiceCannotClearANewActiveScope()
        {
            var previous = HybridCLRPlayerBuildGuard.Enter(_profile, false);
            previous.Dispose();
            using var current = HybridCLRPlayerBuildGuard.Enter(_profile, true);
            previous.Dispose();
            Assert.Throws<InvalidOperationException>(() => HybridCLRPlayerBuildGuard.Enter(_profile, false));
        }

        private static object Capture(HybridCLRBuildProfile profile)
        {
            var type = typeof(HybridCLRBuildPipeline).Assembly.GetType("YLFramework.Integrations.HybridCLR.Editor.HybridCLRBuildInputs");
            Assert.That(type, Is.Not.Null, "Native build inputs need a persistent identity snapshot, not an old Unity wrapper.");
            return Invoke(type.GetMethod("Capture", BindingFlags.NonPublic | BindingFlags.Static), null, new object[] { profile });
        }

        private UnityEngine.Object[] Authoring() => _official.groups.Where(group => group).Select(group => (UnityEngine.Object)group)
            .Concat(_official.groups.Where(group => group).SelectMany(group => group.Schemas).Where(schema => schema)).Prepend(_official).Distinct().ToArray();

        private UnityEngine.Object CapturedAsset(string input) => input switch
        {
            "profile" => _profile,
            "settings" => _official,
            "group" => _profile.codeGroup,
            "bundle-schema" => _profile.codeGroup.GetSchema<BundledAssetGroupSchema>(),
            "update-schema" => _profile.codeGroup.GetSchema<ContentUpdateGroupSchema>(),
            _ => throw new ArgumentOutOfRangeException(nameof(input))
        };

        private static HybridCLRBuildProfile Reload(object snapshot) => (HybridCLRBuildProfile)Invoke(
            snapshot.GetType().GetMethod("Reload", BindingFlags.NonPublic | BindingFlags.Instance), snapshot, Array.Empty<object>());

        private static object Invoke(MethodInfo method, object target, object[] arguments)
        {
            Assert.That(method, Is.Not.Null);
            try
            {
                return method.Invoke(target, arguments);
            }
            catch (TargetInvocationException error) when (error.InnerException != null)
            {
                ExceptionDispatchInfo.Capture(error.InnerException).Throw();
                throw;
            }
        }
    }
}
