using System;
using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;
using YLFramework.Editor.Resource;
using YLFramework.Integrations.HybridCLR.Editor;

namespace YLFramework.Tests.Integrations.HybridCLR
{
    public class HybridCLRBuildInputTests
    {
        private string _root;
        private HybridCLRBuildProfile _profile;
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
            var project = ScriptableObject.CreateInstance<ResourceProject>();
            project.projectId = name;
            AssetDatabase.CreateAsset(project, _root + "/Project.asset");
            var recipe = ScriptableObject.CreateInstance<ResourceBuildRecipe>();
            recipe.project = project;
            recipe.action = ResourceBuildAction.BuildAndPublish;
            recipe.appContentCompatibilityId = "inputs-v1";
            AssetDatabase.CreateAsset(recipe, _root + "/Recipe.asset");
            _profile = ScriptableObject.CreateInstance<HybridCLRBuildProfile>();
            _profile.recipe = recipe;
            _profile.generatedAssetDirectory = _root + "/Generated";
            _profile.baselineDirectory = "HybridCLRBaselines/" + name;
            AssetDatabase.CreateAsset(_profile, _root + "/Profile.asset");
            AssetDatabase.SaveAssetIfDirty(project);
            AssetDatabase.SaveAssetIfDirty(recipe);
            AssetDatabase.SaveAssetIfDirty(_profile);
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
            var recipe = original.recipe;
            var project = recipe.project;
            var recipeGuid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(recipe));
            var projectGuid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(project));
            Resources.UnloadAsset(original);
            Resources.UnloadAsset(recipe);
            Resources.UnloadAsset(project);
            Assert.That(ReferenceEquals(original, null), Is.False);
            Assert.That((bool)original, Is.False);
            Assert.That((bool)recipe, Is.False);
            Assert.That((bool)project, Is.False);

            var restored = Reload(snapshot);
            Assert.That(restored, Is.Not.Null);
            Assert.That(ReferenceEquals(restored, original), Is.False);
            Assert.That(AssetDatabase.TryGetGUIDAndLocalFileIdentifier(restored, out string afterGuid, out long afterId), Is.True);
            Assert.That(afterGuid, Is.EqualTo(guid));
            Assert.That(afterId, Is.EqualTo(localId));
            Assert.That(AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(restored.recipe)), Is.EqualTo(recipeGuid));
            Assert.That(AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(restored.recipe.project)), Is.EqualTo(projectGuid));
            Assert.That(restored.recipe.appContentCompatibilityId, Is.EqualTo("inputs-v1"));
        }

        [TestCase("profile")]
        [TestCase("recipe")]
        [TestCase("project")]
        public void DirtyInputsCannotBecomeAnInitialSnapshot(string input)
        {
            UnityEngine.Object asset = input == "profile" ? _profile : input == "recipe" ? _profile.recipe : _profile.recipe.project;
            EditorUtility.SetDirty(asset);
            Assert.Throws<InvalidDataException>(() => Capture(_profile));
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
        [TestCase("recipe-binding")]
        [TestCase("project-binding")]
        [TestCase("compatibility")]
        [TestCase("project-id")]
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
                case "recipe-binding":
                    var recipe = UnityEngine.Object.Instantiate(_profile.recipe);
                    AssetDatabase.CreateAsset(recipe, _root + "/OtherRecipe.asset");
                    _profile.recipe = recipe;
                    break;
                case "project-binding":
                    var project = ScriptableObject.CreateInstance<ResourceProject>();
                    project.projectId = "other";
                    AssetDatabase.CreateAsset(project, _root + "/OtherProject.asset");
                    _profile.recipe.project = project;
                    asset = _profile.recipe;
                    break;
                case "compatibility":
                    _profile.recipe.appContentCompatibilityId = "inputs-v2";
                    asset = _profile.recipe;
                    break;
                default:
                    _profile.recipe.project.projectId += "-changed";
                    asset = _profile.recipe.project;
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
