using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.TestTools;
using FirstSettings = YLFramework.Core.Editor.Tests.Settings.First.CoreEditorSettingsFixture;
using SecondSettings = YLFramework.Core.Editor.Tests.Settings.Second.CoreEditorSettingsFixture;
using YLFramework.Editor.Common;
using YLFramework.Editor.Core;

namespace YLFramework.Core.Editor.Tests
{
    public class CoreEditorConfigurationTests
    {
        [UnityTest]
        public IEnumerator BothSettingsEntrypointsSelectPublicProjectPagesWithoutCreatingConfiguration()
        {
            var previousFocus = EditorWindow.focusedWindow;
            var existing = Resources.FindObjectsOfTypeAll<EditorWindow>()
                .SingleOrDefault(window => window.GetType().FullName == "UnityEditor.ProjectSettingsWindow");
            var getProvider = typeof(UnityEditor.Editor).Assembly.GetType("UnityEditor.SettingsWindow")
                .GetMethod("GetCurrentProvider", BindingFlags.NonPublic | BindingFlags.Instance);
            string previousPath = existing ? ((SettingsProvider)getProvider.Invoke(existing, null))?.settingsPath : null;
            const string SelectionKey = "SettingsWindow_Project Settings_current_provider";
            const string SplitterKey = "SettingsWindow_m_Splitter";
            bool hadSelection = EditorPrefs.HasKey(SelectionKey);
            bool hadSplitter = EditorPrefs.HasKey(SplitterKey);
            string selection = EditorPrefs.GetString(SelectionKey);
            float splitter = EditorPrefs.GetFloat(SplitterKey);
            var settings = ScriptableObject.CreateInstance<FirstSettings>();
            EditorWindow opened = null;
            try
            {
                OpenAndDrainOwnedSettingsFocus(() => ProjectSettingTools.OpenProjectSettings("Project/Tags and Layers"));
                opened = Resources.FindObjectsOfTypeAll<EditorWindow>()
                    .Single(window => window.GetType().FullName == "UnityEditor.ProjectSettingsWindow");
                Assert.That(((SettingsProvider)getProvider.Invoke(opened, null)).settingsPath, Is.EqualTo("Project/Tags and Layers"));
                OpenAndDrainOwnedSettingsFocus(() => settings.OpenProjectSettings("Project/Physics"));
                Assert.That(((SettingsProvider)getProvider.Invoke(opened, null)).settingsPath, Is.EqualTo("Project/Physics"));
                Assert.That(Resources.FindObjectsOfTypeAll<EditorWindow>()
                    .Count(window => window.GetType().FullName == "UnityEditor.ProjectSettingsWindow"), Is.EqualTo(1));
                Assert.That(File.Exists(SettingsPath<FirstSettings>()), Is.False);
                yield return null;
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(settings);
                if (!existing && opened)
                {
                    opened.Close();
                }
                else if (existing && previousPath != null)
                {
                    OpenAndDrainOwnedSettingsFocus(() => SettingsService.OpenProjectSettings(previousPath));
                }

                if (hadSelection)
                {
                    EditorPrefs.SetString(SelectionKey, selection);
                }
                else
                {
                    EditorPrefs.DeleteKey(SelectionKey);
                }

                if (hadSplitter)
                {
                    EditorPrefs.SetFloat(SplitterKey, splitter);
                }
                else
                {
                    EditorPrefs.DeleteKey(SplitterKey);
                }

                if (previousFocus)
                {
                    previousFocus.Focus();
                }
            }
            yield return null;
            if (previousFocus)
            {
                Assert.That(EditorWindow.focusedWindow, Is.SameAs(previousFocus));
            }
        }

        private static void OpenAndDrainOwnedSettingsFocus(Action open)
        {
            var before = EditorApplication.delayCall?.GetInvocationList() ?? Array.Empty<Delegate>();
            try
            {
                open();
            }
            finally
            {
                // 测试运行中 delayCall 不保证逐帧分发；只执行本次调用产生的原生 Show focus 回调。
                var added = (EditorApplication.delayCall?.GetInvocationList() ?? Array.Empty<Delegate>())
                    .Where(callback => !before.Contains(callback) &&
                        callback.Method.DeclaringType?.DeclaringType?.FullName == "UnityEditor.SettingsWindow" &&
                        callback.Method.Name.StartsWith("<Show>", StringComparison.Ordinal))
                    .Cast<EditorApplication.CallbackFunction>().ToArray();
                foreach (var callback in added)
                {
                    EditorApplication.delayCall -= callback;
                    callback();
                }
            }
        }

        [Test]
        public void ScriptableObjectsAreCreatedOnlyUnderOwnedRootAndPersistentObjectsCannotMove()
        {
            string directory = ScriptableObjectExtensions.GeneratedAssetRoot + "/YLCoreEditorTest_" + Guid.NewGuid().ToString("N");
            bool rootExisted = Directory.Exists(ScriptableObjectExtensions.GeneratedAssetRoot) || File.Exists(ScriptableObjectExtensions.GeneratedAssetRoot + ".meta");
            var selection = Selection.objects;
            var asset = ScriptableObject.CreateInstance<CoreEditorGeneratedAssetFixture>();
            var second = ScriptableObject.CreateInstance<CoreEditorGeneratedAssetFixture>();
            try
            {
                Assert.That(ScriptableObjectExtensions.GeneratedAssetRoot,
                    Is.EqualTo("Assets/YLFramework/Editor/Core/GeneratedAssets"));
                asset.marker = "YL test owns this asset.";
                asset.CreateAsset(directory);
                string firstPath = AssetDatabase.GetAssetPath(asset);
                Assert.That(firstPath, Is.EqualTo(directory + "/" + typeof(CoreEditorGeneratedAssetFixture).Name + ".asset"));
                Assert.That(AssetDatabase.LoadAssetAtPath<CoreEditorGeneratedAssetFixture>(firstPath), Is.SameAs(asset));
                byte[] firstBytes = File.ReadAllBytes(firstPath);
                string firstGuid = AssetDatabase.AssetPathToGUID(firstPath);
                Assert.Throws<InvalidOperationException>(() => asset.CreateAsset(directory + "/MoveAttempt"));
                Assert.That(AssetDatabase.GetAssetPath(asset), Is.EqualTo(firstPath));
                Assert.That(Directory.Exists(directory + "/MoveAttempt"), Is.False);
                second.CreateAsset(directory);
                Assert.That(AssetDatabase.GetAssetPath(second), Is.Not.EqualTo(firstPath).And.StartsWith(directory + "/"));
                Assert.That(File.ReadAllBytes(firstPath), Is.EqualTo(firstBytes));
                Assert.That(AssetDatabase.AssetPathToGUID(firstPath), Is.EqualTo(firstGuid));
            }
            finally
            {
                Selection.objects = selection;
                if (AssetDatabase.IsValidFolder(directory))
                {
                    AssetDatabase.DeleteAsset(directory);
                }

                if (asset)
                {
                    UnityEngine.Object.DestroyImmediate(asset);
                }

                if (second)
                {
                    UnityEngine.Object.DestroyImmediate(second);
                }

                if (!rootExisted && AssetDatabase.IsValidFolder(ScriptableObjectExtensions.GeneratedAssetRoot) &&
                    Directory.GetFileSystemEntries(ScriptableObjectExtensions.GeneratedAssetRoot).Length == 0)
                {
                    AssetDatabase.DeleteAsset(ScriptableObjectExtensions.GeneratedAssetRoot);
                }
            }
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("Assets/Games")]
        [TestCase("Assets/Resources/ExternalUI")]
        [TestCase("Assets/YLFramework/Editor/Help")]
        [TestCase("Assets/YLFramework/Editor/Core/GeneratedAssets/../NotOwned")]
        [TestCase("Assets/YLFramework/Editor/Core/GeneratedAssetsExtra")]
        [TestCase("Assets/YLFramework/Editor/Unrelated/GeneratedAssets")]
        [TestCase("ProjectSettings")]
        public void ScriptableObjectGeneratorRejectsPathsOutsideItsOwnedRoot(string path)
        {
            var asset = ScriptableObject.CreateInstance<CoreEditorGeneratedAssetFixture>();
            var selection = Selection.objects;
            try
            {
                Assert.Throws<ArgumentException>(() => asset.CreateAsset(path));
                Assert.That(AssetDatabase.Contains(asset), Is.False);
                Assert.That(Selection.objects, Is.EqualTo(selection));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(asset);
            }
        }

        [Test]
        public void ScriptableObjectGeneratorRefusesAnExistingUnimportedDirectory()
        {
            string directory = ScriptableObjectExtensions.GeneratedAssetRoot + "/YLCoreEditorTest_" + Guid.NewGuid().ToString("N");
            bool rootExisted = Directory.Exists(ScriptableObjectExtensions.GeneratedAssetRoot) || File.Exists(ScriptableObjectExtensions.GeneratedAssetRoot + ".meta");
            var asset = ScriptableObject.CreateInstance<CoreEditorGeneratedAssetFixture>();
            try
            {
                if (!rootExisted)
                {
                    Assert.That(AssetDatabase.CreateFolder("Assets/YLFramework/Editor/Core", "GeneratedAssets"), Is.Not.Empty);
                }

                Assert.That(AssetDatabase.IsValidFolder(ScriptableObjectExtensions.GeneratedAssetRoot), Is.True);
                Directory.CreateDirectory(directory);
                string sentinel = directory + "/owned-test-sentinel.txt";
                File.WriteAllText(sentinel, "YL test owns this file.");
                Assert.Throws<IOException>(() => asset.CreateAsset(directory));
                Assert.That(File.ReadAllText(sentinel), Is.EqualTo("YL test owns this file."));
                Assert.That(AssetDatabase.Contains(asset), Is.False);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(asset);
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, true);
                }

                if (File.Exists(directory + ".meta"))
                {
                    File.Delete(directory + ".meta");
                }

                if (!rootExisted && AssetDatabase.IsValidFolder(ScriptableObjectExtensions.GeneratedAssetRoot) &&
                    Directory.GetFileSystemEntries(ScriptableObjectExtensions.GeneratedAssetRoot).Length == 0)
                {
                    AssetDatabase.DeleteAsset(ScriptableObjectExtensions.GeneratedAssetRoot);
                }
            }
        }

        [Test]
        public void SameShortNameSettingsUseSeparateFullNameFilesAndRoundTripTheirOwnData()
        {
            WithOwnedSettings(() =>
            {
                Assert.That(typeof(FirstSettings).Name, Is.EqualTo(typeof(SecondSettings).Name));
                Assert.That(SettingsPath<FirstSettings>(), Is.Not.EqualTo(SettingsPath<SecondSettings>()));
                FirstSettings.Instance.marker = "first-" + Guid.NewGuid().ToString("N");
                SecondSettings.Instance.marker = "second-" + Guid.NewGuid().ToString("N");
                string first = FirstSettings.Instance.marker;
                string second = SecondSettings.Instance.marker;
                FirstSettings.Save();
                SecondSettings.Save();
                byte[] secondBytes = File.ReadAllBytes(SettingsPath<SecondSettings>());
                ResetSettings<FirstSettings>();
                FirstSettings.LoadOrCreate();
                Assert.That(FirstSettings.Instance.marker, Is.EqualTo(first));
                Assert.That(SecondSettings.Instance.marker, Is.EqualTo(second));
                Assert.That(File.ReadAllBytes(SettingsPath<SecondSettings>()), Is.EqualTo(secondBytes));
                ResetSettings<SecondSettings>();
                SecondSettings.LoadOrCreate();
                Assert.That(SecondSettings.Instance.marker, Is.EqualTo(second));
            });
        }

        [Test]
        public void UninitializedSettingsCannotSaveOrCreateAFile()
        {
            WithOwnedSettings(() =>
            {
                Assert.Throws<InvalidOperationException>(() => FirstSettings.Save());
                Assert.That(File.Exists(SettingsPath<FirstSettings>()), Is.False);
                Assert.That(File.Exists(SettingsPath<SecondSettings>()), Is.False);
            });
        }

        [Test]
        public void IncompatibleSettingsAreRejectedByLoadAndSaveWithoutOverwriting()
        {
            WithOwnedSettings(() =>
            {
                var initialized = FirstSettings.Instance;
                var wrongType = ScriptableObject.CreateInstance<SecondSettings>();
                try
                {
                    wrongType.marker = "YL test owns this incompatible input.";
                    InternalEditorUtility.SaveToSerializedFileAndForget(new UnityEngine.Object[] { wrongType }, SettingsPath<FirstSettings>(), true);
                    byte[] before = File.ReadAllBytes(SettingsPath<FirstSettings>());
                    DateTime timestamp = File.GetLastWriteTimeUtc(SettingsPath<FirstSettings>());
                    Assert.Throws<IOException>(() => FirstSettings.Save());
                    Assert.That(FirstSettings.Instance, Is.SameAs(initialized));
                    ResetSettings<FirstSettings>();
                    Assert.Throws<IOException>(() => FirstSettings.LoadOrCreate());
                    Assert.That(File.ReadAllBytes(SettingsPath<FirstSettings>()), Is.EqualTo(before));
                    Assert.That(File.GetLastWriteTimeUtc(SettingsPath<FirstSettings>()), Is.EqualTo(timestamp));
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(wrongType);
                }
            });
        }

        private static void WithOwnedSettings(Action test)
        {
            string first = SettingsPath<FirstSettings>();
            string second = SettingsPath<SecondSettings>();
            // Never back up, delete or adopt a pre-existing configuration, even one using a test type name.
            Assert.That(File.Exists(first), Is.False, "Pre-existing test configuration is not owned by this invocation: " + first);
            Assert.That(File.Exists(second), Is.False, "Pre-existing test configuration is not owned by this invocation: " + second);
            ResetSettings<FirstSettings>();
            ResetSettings<SecondSettings>();
            try
            {
                test();
            }
            finally
            {
                ResetSettings<FirstSettings>();
                ResetSettings<SecondSettings>();
                if (File.Exists(first))
                {
                    File.Delete(first);
                }

                if (File.Exists(second))
                {
                    File.Delete(second);
                }
            }
        }

        private static string SettingsPath<T>() where T : ScriptableObject
        {
            var method = typeof(ProjectSettingsScriptableObject<T>).GetMethod("GetFilePath", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(method, Is.Not.Null);
            string path = (string)method.Invoke(null, null);
            Assert.That(path, Is.EqualTo("ProjectSettings/YLFramework/Core/" + typeof(T).FullName + ".asset"));
            return path;
        }

        private static void ResetSettings<T>() where T : ScriptableObject
        {
            var field = typeof(ProjectSettingsScriptableObject<T>).GetField("_instance", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(field, Is.Not.Null);
            var instance = (T)field.GetValue(null);
            if (instance)
            {
                UnityEngine.Object.DestroyImmediate(instance);
            }

            field.SetValue(null, null);
        }
    }
}
