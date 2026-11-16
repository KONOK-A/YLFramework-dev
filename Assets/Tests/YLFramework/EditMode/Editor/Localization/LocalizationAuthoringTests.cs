using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEditor.Localization;
using UnityEngine;
using UnityEngine.Localization;
using YLFramework.Editor.Localization;
using YLFramework.Localization;
using UnityEngine.UI;

namespace YLFramework.Tests.Extensions.Data
{
    public class LocalizationAuthoringTests
    {
        [TestCase("Packages/com.kano.ylframework.localization/Runtime/Systems/Localization/LocalizationSession.cs", "YLFramework.Localization", "ce712fe79d36d83449db36826f4a2f34")]
        [TestCase("Packages/com.kano.ylframework.localization/Runtime/Systems/Localization/LocalizeText.cs", "YLFramework.Localization", "cd445c14d6aa82c49a8d22b7ece51211")]
        public void CurrentScriptsHaveTheirOwnStableIdentity(string path, string assembly, string guid)
        {
            Assert.That(System.IO.Path.GetFileNameWithoutExtension(CompilationPipeline.GetAssemblyNameFromScriptPath(path)), Is.EqualTo(assembly));
            Assert.That(AssetDatabase.AssetPathToGUID(path), Is.EqualTo(guid));
        }

        [Test]
        public void DefaultRuntimeDoesNotDependOnOptionalBusinessAuthoringPackages()
        {
            var references = typeof(LocalizationSession).Assembly.GetReferencedAssemblies().Select(name => name.Name).ToArray();
            Assert.That(references, Does.Contain("Unity.Localization"));
            Assert.That(references, Does.Not.Contain("YLFramework.DataAssets"));
            Assert.That(references, Does.Not.Contain("YLFramework.Excel.Editor"));
        }

        [Test]
        public void CopyStableReferenceUsesOfficialCollectionIdentityWithoutChangingTheTemplate()
        {
            var folder = "Assets/__YLLocalizationReference_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", System.IO.Path.GetFileName(folder));
            try
            {
                var locale = Locale.CreateLocale("en");
                AssetDatabase.CreateAsset(locale, folder + "/en.asset");
                var collection = LocalizationEditorSettings.CreateStringTableCollection("Ref-" + Guid.NewGuid().ToString("N"), folder, new[] { locale });
                collection.SharedData.AddKey("item", 9007199254740993L);
                var reference = new LocalizedString(collection.TableCollectionName, "item");
                var json = JObject.Parse(LocalizationReferenceAuthoring.CopyReference(reference));
                Assert.That((string)json["tableGuid"], Is.EqualTo(collection.SharedData.TableCollectionNameGuid.ToString("N")));
                Assert.That((string)json["entryId"], Is.EqualTo("9007199254740993"));
                Assert.That(reference.TableReference.TableCollectionName, Is.EqualTo(collection.TableCollectionName));
                Assert.That(reference.TableEntryReference.Key, Is.EqualTo("item"));
            }
            finally { AssetDatabase.DeleteAsset(folder); }
        }

        [Test]
        public void CurrentGroupDiagnosticsFindUnsafeGatesMissingFontAndDuplicateTextWriters()
        {
            var root = new GameObject("diagnostic-root", typeof(RectTransform), typeof(CanvasGroup));
            try
            {
                var group = root.AddComponent<YLFramework.Localization.LocalizationGroup>();
                group.Content = root.GetComponent<CanvasGroup>();
                var label = new GameObject("diagnostic-label", typeof(RectTransform), typeof(Text));
                label.transform.SetParent(root.transform, false);
                label.GetComponent<Text>().font = null;
                label.AddComponent<LocalizeText>();
                var official = label.AddComponent<UnityEngine.Localization.Components.LocalizeStringEvent>();
                var setter = (UnityEngine.Events.UnityAction<string>)Delegate.CreateDelegate(typeof(UnityEngine.Events.UnityAction<string>),
                    label.GetComponent<Text>(), typeof(Text).GetProperty("text").SetMethod);
                UnityEditor.Events.UnityEventTools.AddPersistentListener(official.OnUpdateString, setter);
                var method = typeof(LocalizationSetup).GetMethod("ValidateGroup");
                Assert.That(method, Is.Not.Null, "Authoring assistance must diagnose the selected group, not only provide menu links.");
                var errors = (System.Collections.Generic.IReadOnlyList<string>)method.Invoke(null, new object[] { group });
                Assert.That(errors.Any(error => error.Contains("CanvasGroup")), Is.True);
                Assert.That(errors.Any(error => error.Contains("font")), Is.True);
                Assert.That(errors.Any(error => error.Contains("writer")), Is.True);
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }
    }
}
