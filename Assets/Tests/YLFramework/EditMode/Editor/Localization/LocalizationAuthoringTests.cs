using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using YLFramework.Editor.Localization;
using YLFramework.Localization;

namespace YLFramework.Tests.Extensions.Data
{
    public class LocalizationAuthoringTests
    {
        private string _path;
        private string _languagesPath;
        private MultiLanguageConfigs _root;
        private LanguageConfig _english;
        private LanguageConfig _chinese;
        private string[] _absentParents = Array.Empty<string>();

        [Test]
        public void LocalizationOwnsItsRuntimeAndEditorWithoutReverseMainOrDataAssetsDependencies()
        {
            var runtime = typeof(LocalizationManager).Assembly;
            var editor = typeof(LocalizationAuthoring).Assembly;
            Assert.That(runtime.GetName().Name, Is.EqualTo("YLFramework.Localization"));
            Assert.That(editor.GetName().Name, Is.EqualTo("YLFramework.Localization.Editor"));
            Assert.That(typeof(LocalizationAuthoring).IsVisible, Is.False);
            Assert.That(typeof(LocalizationAuthoring).IsPublic, Is.False);
            Assert.That(AppDomain.CurrentDomain.GetAssemblies().Count(owner => owner.GetType(typeof(LocalizationAuthoring).FullName) != null), Is.EqualTo(1));
            Assert.That(typeof(YLFramework.Resource.ResourceSystem).Assembly.GetType(typeof(LocalizeImage).FullName), Is.Null);
            Assert.That(typeof(YLFramework.Editor.Core.CreateModuleCommand).Assembly.GetType(typeof(LocalizationAuthoring).FullName), Is.Null);
            Assert.That(runtime.GetReferencedAssemblies().Select(reference => reference.Name), Does.Not.Contain("YLFramework.DataAssets").And.Not.Contain("YLFramework.Excel.Editor"));
            Assert.That(editor.GetReferencedAssemblies().Select(reference => reference.Name), Does.Not.Contain("YLFramework.DataAssets").And.Not.Contain("YLFramework.DataAssets.Editor"));
            var required = typeof(LocalizeImage).GetCustomAttributes(typeof(RequireComponent), true).Cast<RequireComponent>().Single();
            Assert.That(required.m_Type0, Is.EqualTo(typeof(YLFramework.Resource.ResourceImage)));
        }

        [TestCase("48588b1d9a7c5a14b817b732e9568227", "Runtime/Systems/Localization/LocalizationManager.cs")]
        [TestCase("02373141161f98548927076bb0014367", "Runtime/Systems/Localization/MultiLanguageConfigs.cs")]
        [TestCase("d5ae76428dc6dc047a8b1d62832313c2", "Runtime/Systems/Localization/LocalizeString.cs")]
        [TestCase("2345dd753f4b47642becd100e4fb0255", "Runtime/Systems/Localization/LocalizeImage.cs")]
        public void MovedLocalizationKeepsOriginalScriptIdentity(string guid, string relative)
        {
            Assert.That(AssetDatabase.GUIDToAssetPath(guid), Is.EqualTo("Packages/com.kano.ylframework.localization/" + relative));
        }

        [SetUp]
        public void SetUp()
        {
            const string Parent = "Assets/Tests/YLFramework/Fixtures/Extensions/Data/Localization";
            _absentParents = new[]
            {
                Parent, "Assets/Tests/YLFramework/Fixtures/Extensions/Data", "Assets/Tests/YLFramework/Fixtures/Extensions",
                "Assets/Tests/YLFramework/Fixtures", "Assets/YLFrameworkData/Localization", "Assets/YLFrameworkData"
            }.Where(path => !Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(path)) && !File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(path)) && !File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(path + ".meta"))).ToArray();
            EnsureFolder(Parent);
            _path = Parent + "/Run-" + Guid.NewGuid().ToString("N") + ".asset";
            _root = ScriptableObject.CreateInstance<MultiLanguageConfigs>();
            AssetDatabase.CreateAsset(_root, _path);
            _languagesPath = LocalizationAuthoring.GetLanguageFolder(_root);
        }

        [TearDown]
        public void TearDown()
        {
            if (_root)
            {
                Undo.ClearUndo(_root);
            }

            if (_english)
            {
                Undo.ClearUndo(_english);
            }

            if (_chinese)
            {
                Undo.ClearUndo(_chinese);
            }

            if (TestContext.CurrentContext.Result.Outcome.Status == NUnit.Framework.Interfaces.TestStatus.Failed)
            {
                Console.WriteLine("Preserved failed localization fixture: " + _path + " | " + _languagesPath);
                return;
            }

            if (!string.IsNullOrEmpty(_languagesPath))
            {
                AssetDatabase.DeleteAsset(_languagesPath);
            }

            if (!string.IsNullOrEmpty(_path))
            {
                AssetDatabase.DeleteAsset(_path);
            }

            foreach (var path in _absentParents)
            {
                if (Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(path)) && Directory.GetFileSystemEntries(global::YLFramework.Testing.PackageTestPaths.Physical(path)).Length == 0)
                {
                    Assert.That(AssetDatabase.DeleteAsset(path), Is.True, path);
                }
            }
        }

        [Test, Order(-1), Category("YL.Test.ParentCleanup")]
        public void TearDownReturnsNewEmptyAuthoringParents()
        {
            _english = LocalizationAuthoring.CreateLanguage(_root, SystemLanguage.English);
            TearDown();
            foreach (var path in _absentParents)
            {
                Assert.That(Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(path)), Is.False, path);
                Assert.That(File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(path + ".meta")), Is.False, path + ".meta");
            }
        }

        [Test]
        public void AuthoringCreateDuplicateUndoSaveReopenAndRemoveReferenceKeepsAsset()
        {
            _english = LocalizationAuthoring.CreateLanguage(_root, SystemLanguage.English);
            LocalizationAuthoring.AddEntry(_root, 1, MultiLanguageType.String);
            _english.languagesInfo[0].str = "source";
            _chinese = LocalizationAuthoring.CreateLanguage(_root, SystemLanguage.ChineseSimplified);
            Assert.That(_chinese.languagesInfo[0].id, Is.EqualTo(1));
            Assert.That(_chinese.languagesInfo[0].str, Is.Null, "A new language must not impersonate the source translation.");
            LocalizationAuthoring.DuplicateEntry(_root, 1, 2);
            Assert.That(_english.GetLanguageInfo(2).str, Is.EqualTo("source"));
            var languagePath = AssetDatabase.GetAssetPath(_chinese);
            Undo.IncrementCurrentGroup();
            LocalizationAuthoring.RemoveLanguageReference(_root, _chinese);
            Undo.FlushUndoRecordObjects();
            Assert.That(AssetDatabase.LoadAssetAtPath<LanguageConfig>(languagePath), Is.SameAs(_chinese));
            Undo.PerformUndo();
            Assert.That(_root.Languages.Contains(_chinese), Is.True);
            EditorUtility.SetDirty(_english);
            AssetDatabase.SaveAssetIfDirty(_english);
            AssetDatabase.SaveAssetIfDirty(_root);
            AssetDatabase.ImportAsset(_path, ImportAssetOptions.ForceUpdate);
            var reopened = AssetDatabase.LoadAssetAtPath<MultiLanguageConfigs>(_path);
            Assert.That(reopened.Languages.Count, Is.EqualTo(2));
            Assert.That(reopened.GetLanguageConfigData(SystemLanguage.English).GetLanguageInfo(2).str, Is.EqualTo("source"));
            Assert.Throws<InvalidOperationException>(() => LocalizationAuthoring.AddEntry(reopened, 2, MultiLanguageType.String));
        }

        private static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path))
            {
                return;
            }

            var slash = path.LastIndexOf('/');
            var parent = path.Substring(0, slash);
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, path.Substring(slash + 1));
        }
    }
}
