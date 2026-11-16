using System;
using System.Collections;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Localization;
using UnityEngine.Localization.Settings;
using UnityEngine.Localization.SmartFormat.PersistentVariables;
using UnityEngine.TestTools;
using UnityEngine.UI;
using YLFramework.Localization;
#if UNITY_6000_0_OR_NEWER
using TMPro;
#endif

namespace YLFramework.Tests.Localization
{
    public class LocalizationBindingTests
    {
        private static IDisposable Session()
        {
            return new LocalizationSession();
        }

        private static IDisposable Bind(IDisposable session, LocalizedString reference, Action<string> apply)
        {
            return ((LocalizationSession)session).BindString(reference, apply);
        }

        [UnityTest]
        public IEnumerator SharedTemplateHasIndependentConsumersAndClosingOneDoesNotStopAnother()
        {
            using var fixture = new LocalizationFixture();
            var template = new LocalizedString(fixture.Strings, "name");
            var first = new List<string>();
            var second = new List<string>();
            using var firstSession = Session();
            using var secondSession = Session();
            Bind(firstSession, template, first.Add);
            Bind(secondSession, template, second.Add);
            for (var i = 0; i < 5; i++)
                yield return null;
            Assert.That(first, Is.EqualTo(new[] { "Sword" }));
            Assert.That(second, Is.EqualTo(new[] { "Sword" }));
            firstSession.Dispose();
            firstSession.Dispose();
            LocalizationSettings.SelectedLocale = fixture.French;
            for (var i = 0; i < 5; i++)
                yield return null;
            Assert.That(first, Is.EqualTo(new[] { "Sword" }));
            Assert.That(second[second.Count - 1], Is.EqualTo("Epee"));
            Assert.That(template.HasChangeHandler, Is.False);
            Assert.That(template.LocaleOverride, Is.Null);
        }

        [UnityTest]
        public IEnumerator ClosingBeforeTableCompletionPreventsLateDelivery()
        {
            using var fixture = new LocalizationFixture { DelayTables = true };
            var deliveries = new List<string>();
            var session = Session();
            Bind(session, new LocalizedString(fixture.Strings, "name"), deliveries.Add);
            session.Dispose();
            fixture.CompletePending();
            for (var i = 0; i < 5; i++)
                yield return null;
            Assert.That(deliveries, Is.Empty);
        }

        [UnityTest]
        public IEnumerator BlankOutputConfiguredByOfficialPolicyDoesNotBlockReadiness()
        {
            using var fixture = new LocalizationFixture();
            fixture.Settings.GetStringDatabase().MissingTranslationState = MissingTranslationBehavior.PrintWarning;
            LogAssert.Expect(LogType.Warning, new Regex("No translation found for 'empty'"));
            var values = new List<string>();
            using var session = Session();
            var reference = new LocalizedString(fixture.Strings, "empty");
            reference.Add("empty", new StringVariable { Value = "" });
            Bind(session, reference, values.Add);
            for (var i = 0; i < 5; i++)
                yield return null;
            Assert.That(values, Is.EqualTo(new[] { "" }));
            Assert.That(((YLFramework.Localization.LocalizationSession)session).IsReady, Is.True);
        }

        [UnityTest]
        public IEnumerator UntranslatedCellKeepsOfficialMissingTranslationBehavior()
        {
            using var fixture = new LocalizationFixture();
            var values = new List<string>();
            using var session = Session();
            Bind(session, new LocalizedString(fixture.Strings, "untranslated"), values.Add);
            for (var i = 0; i < 5; i++)
                yield return null;
            Assert.That(values.Count, Is.EqualTo(1));
            Assert.That(values[0], Does.StartWith("No translation found"));
        }

        [UnityTest]
        public IEnumerator DelayedAssetSwitchDeliversOnlyTheCurrentLocale()
        {
            using var fixture = new LocalizationFixture { DelayTables = true };
            var english = fixture.Own(new Texture2D(2, 2));
            var french = fixture.Own(new Texture2D(3, 3));
            fixture.SetAsset(fixture.English, "icon", english);
            fixture.SetAsset(fixture.French, "icon", french);
            var values = new List<Texture2D>();
            using var session = new LocalizationSession();
            var reference = new LocalizedAsset<Texture2D>
            {
                TableReference = fixture.Assets,
                TableEntryReference = "icon"
            };
            session.BindAsset(reference, values.Add);
            for (var i = 0; i < 8 && fixture.PendingTableLoads == 0; i++)
                yield return null;
            Assert.That(fixture.PendingTableLoads, Is.EqualTo(1));
            LocalizationSettings.SelectedLocale = fixture.French;
            for (var i = 0; i < 8 && fixture.PendingTableLoads < 2; i++)
                yield return null;
            Assert.That(fixture.PendingTableLoads, Is.EqualTo(2));
            fixture.CompletePending();
            for (var i = 0; i < 5; i++)
                yield return null;
            Assert.That(values, Is.EqualTo(new[] { french }));
            Assert.That(session.Error, Is.Null);
        }

        [UnityTest]
        public IEnumerator ConsumerMayCloseInsideItsStringDelivery()
        {
            using var fixture = new LocalizationFixture();
            var count = 0;
            var session = new LocalizationSession();
            session.BindString(new LocalizedString(fixture.Strings, "name"), _ =>
            {
                count++;
                session.Dispose();
            });
            for (var i = 0; i < 3; i++)
                yield return null;
            LocalizationSettings.SelectedLocale = fixture.French;
            for (var i = 0; i < 3; i++)
                yield return null;
            Assert.That(count, Is.EqualTo(1));
            Assert.That(session.IsDisposed, Is.True);
        }

        [UnityTest]
        public IEnumerator UguiTextBindsWithoutReflectionAndRestoresItsOwnValueOnDisable()
        {
            using var fixture = new LocalizationFixture();
            var go = fixture.Own(new GameObject("localized-text", typeof(RectTransform), typeof(Text)));
            go.SetActive(false);
            var text = go.GetComponent<Text>();
            text.text = "original";
            var component = go.AddComponent<LocalizeText>();
            component.StringReference = new LocalizedString(fixture.Strings, "name");
            go.SetActive(true);
            for (var i = 0; i < 5; i++)
                yield return null;
            Assert.That(text.text, Is.EqualTo("Sword"));
            Assert.That(go.activeSelf, Is.True);
            go.SetActive(false);
            Assert.That(text.text, Is.EqualTo("original"));
            LocalizationSettings.SelectedLocale = fixture.French;
            for (var i = 0; i < 3; i++)
                yield return null;
            Assert.That(text.text, Is.EqualTo("original"));
            go.SetActive(true);
            for (var i = 0; i < 4; i++)
                yield return null;
            Assert.That(text.text, Is.EqualTo("Epee"));
        }

        [UnityTest]
        public IEnumerator UguiSpriteUsesTheOfficialAssetTableAndRestoresOnDisable()
        {
            using var fixture = new LocalizationFixture();
            var texture = fixture.Own(new Texture2D(2, 2));
            var sprite = fixture.Own(Sprite.Create(texture, new Rect(0, 0, 2, 2), Vector2.zero));
            fixture.SetAsset(fixture.English, "icon", sprite);
            var go = fixture.Own(new GameObject("localized-image", typeof(RectTransform), typeof(Image)));
            go.SetActive(false);
            var component = go.AddComponent<LocalizeSprite>();
            component.SpriteReference = new LocalizedSprite { TableReference = fixture.Assets, TableEntryReference = "icon" };
            go.SetActive(true);
            for (var i = 0; i < 5; i++)
                yield return null;
            Assert.That(go.GetComponent<Image>().sprite, Is.SameAs(sprite));
            go.SetActive(false);
            Assert.That(go.GetComponent<Image>().sprite, Is.Null);
        }

        [UnityTest]
        public IEnumerator TextWaitsForItsRequiredFontBeforeReplacingOriginalContent()
        {
            using var fixture = new LocalizationFixture { DelayAssetTables = true };
            var font = fixture.Own(Font.CreateDynamicFontFromOSFont("Arial", 12));
            fixture.SetAsset(fixture.English, "font", font);
            var go = fixture.Own(new GameObject("font-text", typeof(RectTransform), typeof(Text)));
            go.SetActive(false);
            var text = go.GetComponent<Text>();
            text.text = "original";
            var originalFont = text.font;
            var component = go.AddComponent<LocalizeText>();
            component.StringReference = new LocalizedString(fixture.Strings, "name");
            component.FontReference = new LocalizedFont { TableReference = fixture.Assets, TableEntryReference = "font" };
            go.SetActive(true);
            for (var i = 0; i < 3; i++)
                yield return null;
            Assert.That(text.text, Is.EqualTo("original"));
            Assert.That(component.IsReady, Is.False);
            fixture.CompletePending();
            for (var i = 0; i < 4; i++)
                yield return null;
            Assert.That(text.text, Is.EqualTo("Sword"));
            Assert.That(text.font, Is.SameAs(font));
            go.SetActive(false);
            Assert.That(text.font, Is.SameAs(originalFont));
        }

        [UnityTest]
        public IEnumerator InitializationGatesBindingsAndDoesNotBlockTheMainThread()
        {
            using var fixture = new LocalizationFixture(true);
            using var session = new LocalizationSession();
            var values = new List<string>();
            session.BindString(new LocalizedString(fixture.Strings, "name"), values.Add);
            yield return null;
            Assert.That(values, Is.Empty);
            Assert.That(session.IsReady, Is.False);
            fixture.Settings.CompleteInitialization();
            for (var i = 0; i < 5; i++)
                yield return null;
            Assert.That(values, Is.EqualTo(new[] { "Sword" }));
            Assert.That(session.IsReady, Is.True);
        }

        [UnityTest]
        public IEnumerator InitializationFailureHasAnObservableTerminalError()
        {
            using var fixture = new LocalizationFixture(true);
            using var session = new LocalizationSession();
            var values = new List<string>();
            session.BindString(new LocalizedString(fixture.Strings, "name"), values.Add);
            fixture.Settings.CompleteInitialization(false);
            for (var i = 0; i < 4; i++)
                yield return null;
            Assert.That(session.Error, Is.Not.Null);
            Assert.That(session.Error.ToString(), Does.Contain("Fixture initialization failed"));
            Assert.That(session.IsReady, Is.False);
            Assert.That(values, Is.Empty);
        }

        [UnityTest]
        public IEnumerator TemplateArgumentsAreCopiedWithoutMutatingSharedReference()
        {
            using var fixture = new LocalizationFixture();
            fixture.StringTable(fixture.English).AddEntry("argument", "{0}");
            var arguments = new List<object> { "first" };
            var template = new LocalizedString(fixture.Strings, "argument") { Arguments = arguments };
            var values = new List<string>();
            using var session = new LocalizationSession();
            session.BindString(template, values.Add);
            arguments[0] = "changed";
            for (var i = 0; i < 4; i++)
                yield return null;
            Assert.That(values[0], Is.EqualTo("first"));
            Assert.That(template.Arguments, Is.SameAs(arguments));
            Assert.That(template.HasChangeHandler, Is.False);
        }

        [UnityTest]
        public IEnumerator OneConsumerDeliveryFailureIsObservableAndDoesNotInterruptAnother()
        {
            using var fixture = new LocalizationFixture();
            using var first = new LocalizationSession();
            using var second = new LocalizationSession();
            var values = new List<string>();
            first.BindString(new LocalizedString(fixture.Strings, "name"), _ =>
                throw new InvalidOperationException("Consumer delivery failed"));
            second.BindString(new LocalizedString(fixture.Strings, "name"), values.Add);
            for (var i = 0; i < 4; i++)
                yield return null;
            Assert.That(first.Error?.Message, Is.EqualTo("Consumer delivery failed"));
            Assert.That(first.IsReady, Is.False);
            Assert.That(values, Is.EqualTo(new[] { "Sword" }));
        }

        [UnityTest]
        public IEnumerator RetiredOfficialReferencesCannotReacquireAfterTheLocaleEventDispatch()
        {
            using var fixture = new LocalizationFixture();
            using var session = new LocalizationSession();
            var binding = session.BindString(new LocalizedString(fixture.Strings, "name"), _ => { });
            var field = typeof(LocalizedStringBinding).GetField("_reference", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var retired = new List<LocalizedString>();
            try
            {
                for (var round = 0; round < 6; round++)
                {
                    for (var frame = 0; frame < 3; frame++)
                        yield return null;
                    retired.Add((LocalizedString)field.GetValue(binding));
                    LocalizationSettings.SelectedLocale = round % 2 == 0 ? fixture.French : fixture.English;
                }
                for (var frame = 0; frame < 4; frame++)
                    yield return null;
                foreach (var reference in retired)
                    Assert.That(reference.CurrentLoadingOperationHandle.IsValid(), Is.False,
                        "A retired private reference must not reacquire a table-entry handle in the still-running official locale callback array.");
                var current = (LocalizedString)field.GetValue(binding);
                session.Dispose();
                Assert.That(current.CurrentLoadingOperationHandle.IsValid(), Is.False);
            }
            finally
            {
                foreach (var reference in retired)
                    ((IDisposable)reference).Dispose();
            }
        }

        [UnityTest]
        public IEnumerator SessionResetDrainsRetiredReferencesBeforeTheOldUnityContextDisappears()
        {
            using var fixture = new LocalizationFixture();
            var session = new LocalizationSession();
            session.BindString(new LocalizedString(fixture.Strings, "name"), _ => { });
            for (var frame = 0; frame < 3; frame++)
                yield return null;
            session.Dispose();
            var flags = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic;
            typeof(LocalizationSession).GetMethod("CloseSessions", flags).Invoke(null, null);
            var owners = (HashSet<LocalizationSession>)typeof(LocalizationSession).GetField("_sessions", flags).GetValue(null);
            Assert.That(owners.Contains(session), Is.False,
                "Play reset must finish its own retired references even when the old SynchronizationContext will no longer execute posts.");
        }

#if UNITY_6000_0_OR_NEWER
#if UNITY_EDITOR
        [PrebuildSetup(typeof(TmpResourcesSetup))]
        [PostBuildCleanup(typeof(TmpResourcesSetup))]
#endif
        [UnityTest]
        public IEnumerator TmpTextAndFontUseTheTypedOptionalAdapter()
        {
            using var fixture = new LocalizationFixture();
            var type = Type.GetType("YLFramework.Localization.LocalizeTMPText, YLFramework.Localization.TMP");
            Assert.That(type, Is.Not.Null, "TMP must have its own typed integration, not the old reflected Other target.");
            Assert.That(Resources.Load<TMP_Settings>("TMP Settings"), Is.Not.Null,
                "The official TMP resources must be imported by the prebuild step before entering Play Mode.");
            var first = fixture.Own(TMP_FontAsset.CreateFontAsset("Arial", "Regular"));
            var second = fixture.Own(TMP_FontAsset.CreateFontAsset("Arial", "Regular"));
            Assert.That(first, Is.Not.Null);
            Assert.That(second, Is.Not.Null);
            fixture.SetAsset(fixture.English, "tmp-font", second);
            var go = fixture.Own(new GameObject("tmp-localization", typeof(RectTransform)));
            go.SetActive(false);
            var text = go.AddComponent<TextMeshProUGUI>();
            text.font = first;
            text.text = "original";
            var component = go.AddComponent(type);
            type.GetProperty("StringReference").SetValue(component, new LocalizedString(fixture.Strings, "name"));
            type.GetProperty("FontReference").SetValue(component,
                new LocalizedTmpFont { TableReference = fixture.Assets, TableEntryReference = "tmp-font" });
            go.SetActive(true);
            for (var i = 0; i < 6; i++) yield return null;
            Assert.That(text.text, Is.EqualTo("Sword"));
            Assert.That(text.font, Is.SameAs(second));
            go.SetActive(false);
            Assert.That(text.text, Is.EqualTo("original"));
            Assert.That(text.font, Is.SameAs(first));
        }

#if UNITY_EDITOR
        public sealed class TmpResourcesSetup : IPrebuildSetup, IPostBuildCleanup
        {
            private const string _ownershipKey = "YL.Tests.Localization.OwnsTMPResources";

            public void Setup()
            {
                if (Resources.Load<TMP_Settings>("TMP Settings")) return;
                var root = System.IO.Directory.GetParent(Application.dataPath).FullName;
                var markerPath = System.IO.Path.Combine(root, ".yl-owner.json");
                if (!Application.isBatchMode || !System.IO.File.Exists(markerPath))
                    throw new InvalidOperationException("TMP resource setup requires a tool-owned batchmode host.");
                var marker = JsonUtility.FromJson<OwnerMarker>(System.IO.File.ReadAllText(markerPath));
                if (marker.purpose != "YLTesting.Directory" || marker.kind != "Host" ||
                    !string.Equals(System.IO.Path.GetFullPath(marker.path), root, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("TMP test host ownership does not match.");
                if (UnityEditor.AssetDatabase.IsValidFolder("Assets/TextMesh Pro"))
                    throw new InvalidOperationException("Do not overwrite an existing TMP resource folder.");
                var package = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(TMP_Text).Assembly);
                var source = System.IO.Path.Combine(package.resolvedPath, "Package Resources", "TMP Essential Resources.unitypackage");
                if (!System.IO.File.Exists(source)) throw new InvalidOperationException("Official TMP Essential Resources are missing.");
                UnityEditor.SessionState.SetBool(_ownershipKey, true);
                try
                {
                    UnityEditor.AssetDatabase.ImportPackage(source, false);
                }
                catch
                {
                    Cleanup();
                    throw;
                }
            }

            public void Cleanup()
            {
                if (UnityEditor.SessionState.GetBool(_ownershipKey, false) &&
                    UnityEditor.AssetDatabase.IsValidFolder("Assets/TextMesh Pro") &&
                    !UnityEditor.AssetDatabase.DeleteAsset("Assets/TextMesh Pro"))
                    throw new InvalidOperationException("Failed to remove the TMP resources owned by this test.");
                UnityEditor.SessionState.EraseBool(_ownershipKey);
            }

            [Serializable]
            private sealed class OwnerMarker
            {
                public string purpose;
                public string kind;
                public string path;
            }
        }
#endif
#endif
    }
}
