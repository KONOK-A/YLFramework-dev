using System;
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Localization;
using UnityEngine.Localization.Settings;
using UnityEngine.TestTools;
using UnityEngine.UI;
using YLFramework.Binding;
using YLFramework.Core;
using YLFramework.Localization;
using YLFramework.UI;

namespace YLFramework.Tests.Localization
{
    public class LocalizationPresentationTests
    {
        [UnityTest]
        public IEnumerator GroupWaitsForTheRequiredFontWithoutRestartingBusinessObjects()
        {
            using var fixture = new LocalizationFixture { DelayAssetTables = true };
            var font = fixture.Own(Font.CreateDynamicFontFromOSFont("Arial", 12));
            fixture.SetAsset(fixture.English, "font", font);
            var surface = new Surface(fixture);
            surface.Text.FontReference = new LocalizedFont { TableReference = fixture.Assets, TableEntryReference = "font" };
            surface.Open();
            for (var i = 0; i < 3; i++)
                yield return null;
            Assert.That(surface.State, Is.EqualTo("Loading"));
            Assert.That(surface.Content.alpha, Is.Zero);
            Assert.That(surface.Label.text, Is.EqualTo("original"));
            Assert.That(surface.Root.activeSelf && surface.Label.gameObject.activeSelf, Is.True);
            Assert.That(surface.Loading.activeSelf, Is.True);
            fixture.CompletePending();
            for (var i = 0; i < 5; i++)
                yield return null;
            Assert.That(surface.State, Is.EqualTo("Ready"));
            Assert.That(surface.Content.alpha, Is.EqualTo(1));
            Assert.That(surface.Label.text, Is.EqualTo("Sword"));
            Assert.That(surface.Label.font, Is.SameAs(font));
            Assert.That(surface.Loading.activeSelf, Is.False);
        }

        [UnityTest]
        public IEnumerator ReopeningTheSameViewAndItsRemovalInvalidateEarlierRounds()
        {
            using var fixture = new LocalizationFixture { DelayTables = true };
            var surface = new Surface(fixture);
            var owner = surface.Root.AddComponent<PresentationOwner>();
            var first = surface.Open(owner);
            var second = surface.Open(owner);
            Assert.That(first.IsDisposed, Is.True);
            Assert.That(second.IsDisposed, Is.False);
            owner.ViewRemoved?.Invoke();
            fixture.CompletePending();
            for (var i = 0; i < 4; i++)
                yield return null;
            Assert.That(second.IsDisposed, Is.True);
            Assert.That(surface.State, Is.EqualTo("Inactive"));
            Assert.That(surface.Label.text, Is.EqualTo("original"));
            Assert.That(surface.Root.activeSelf, Is.True);
        }

        [UnityTest]
        public IEnumerator ReadyBusinessUpdatesDoNotReenterTheGroupLoadingState()
        {
            using var fixture = new LocalizationFixture();
            var surface = new Surface(fixture);
            var count = new BindableProperty<int>(2);
            surface.Text.StringReference = new LocalizedString(fixture.Strings, "count");
            surface.Open(null, _ => surface.Text.StringBinding.BindVariable("count", count));
            for (var i = 0; i < 4; i++)
                yield return null;
            Assert.That(surface.State, Is.EqualTo("Ready"));
            Assert.That(surface.Label.text, Is.EqualTo("2"));
            count.Value = 9;
            for (var i = 0; i < 3; i++)
            {
                yield return null;
                Assert.That(surface.State, Is.EqualTo("Ready"));
                Assert.That(surface.Content.alpha, Is.EqualTo(1));
                Assert.That(surface.Loading.activeSelf, Is.False);
            }
            Assert.That(surface.Label.text, Is.EqualTo("9"));
        }

        [UnityTest]
        public IEnumerator FailedInitializationShowsALocalErrorAndCanRetryAfterRepair()
        {
            using var fixture = new LocalizationFixture(true);
            var surface = new Surface(fixture);
            surface.Open();
            fixture.Settings.CompleteInitialization(false);
            for (var i = 0; i < 4; i++)
                yield return null;
            Assert.That(surface.State, Is.EqualTo("Failed"));
            Assert.That(surface.Content.alpha, Is.Zero);
            Assert.That(surface.Loading.activeSelf, Is.False);
            Assert.That(surface.Failure.activeSelf, Is.True);
            Assert.That(surface.Type.GetProperty("Error").GetValue(surface.Group), Is.Not.Null);
            fixture.Settings.ReleaseInitialization();
            fixture.Settings.DelayInitialization = false;
            surface.Type.GetMethod("Retry").Invoke(surface.Group, null);
            for (var i = 0; i < 5; i++)
                yield return null;
            Assert.That(surface.State, Is.EqualTo("Ready"));
            Assert.That(surface.Failure.activeSelf, Is.False);
        }

        [UnityTest]
        public IEnumerator DisabledGroupReleasesTheOldRoundAndRebuildsFromCurrentData()
        {
            using var fixture = new LocalizationFixture();
            var surface = new Surface(fixture);
            var count = new BindableProperty<int>(1);
            surface.Text.StringReference = new LocalizedString(fixture.Strings, "count");
            var scope = surface.Open(null, _ => surface.Text.StringBinding.BindVariable("count", count));
            for (var i = 0; i < 4; i++)
                yield return null;
            surface.Root.SetActive(false);
            Assert.That(scope.IsDisposed, Is.True);
            count.Value = 7;
            surface.Root.SetActive(true);
            for (var i = 0; i < 5; i++)
                yield return null;
            Assert.That(surface.State, Is.EqualTo("Ready"));
            Assert.That(surface.Label.text, Is.EqualTo("7"));
        }

        [UnityTest]
        public IEnumerator WithdrawingAPendingMemberCannotLeaveTheGroupWaitingForever()
        {
            using var fixture = new LocalizationFixture { DelayAssetTables = true };
            var font = fixture.Own(Font.CreateDynamicFontFromOSFont("Arial", 12));
            fixture.SetAsset(fixture.English, "font", font);
            var surface = new Surface(fixture);
            surface.Text.FontReference = new LocalizedFont { TableReference = fixture.Assets, TableEntryReference = "font" };
            surface.Open();
            yield return null;
            surface.Text.enabled = false;
            for (var i = 0; i < 3; i++)
                yield return null;
            Assert.That(surface.State, Is.EqualTo("Ready"));
            Assert.That(surface.Label.gameObject.activeSelf, Is.True);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void InvalidRootAssignmentsCannotHideOrDeactivateTheBusinessRoot(bool contentUsesRoot)
        {
            using var fixture = new LocalizationFixture();
            var surface = new Surface(fixture);
            var rootGate = surface.Root.AddComponent<CanvasGroup>();
            if (contentUsesRoot)
                surface.Type.GetProperty("Content").SetValue(surface.Group, rootGate);
            else
                surface.Type.GetProperty("LoadingRoot").SetValue(surface.Group, surface.Root);
            var error = Assert.Throws<System.Reflection.TargetInvocationException>(() => surface.Open());
            Assert.That(error.InnerException, Is.TypeOf<InvalidOperationException>());
            Assert.That(surface.Root.activeSelf, Is.True);
            Assert.That(rootGate.alpha, Is.EqualTo(1));
            Assert.That(rootGate.interactable, Is.True);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void StatusRootsCannotBeAncestorsOfBusinessContent(bool loading)
        {
            using var fixture = new LocalizationFixture();
            var surface = new Surface(fixture);
            var holder = new GameObject("business-holder", typeof(RectTransform));
            holder.transform.SetParent(surface.Root.transform, false);
            surface.Content.transform.SetParent(holder.transform, false);
            surface.Type.GetProperty(loading ? "LoadingRoot" : "FailureRoot").SetValue(surface.Group, holder);
            var error = Assert.Throws<System.Reflection.TargetInvocationException>(() => surface.Open());
            Assert.That(error.InnerException, Is.TypeOf<InvalidOperationException>());
            Assert.That(holder.activeSelf && surface.Content.gameObject.activeSelf && surface.Label.gameObject.activeSelf, Is.True);
        }

        [UnityTest]
        public IEnumerator RapidLanguageSwitchCommitsOnlyTheLatestTextAndFontTogether()
        {
            using var fixture = new LocalizationFixture { DelayAssetTables = true };
            var english = fixture.Own(Font.CreateDynamicFontFromOSFont("Arial", 12));
            var french = fixture.Own(Font.CreateDynamicFontFromOSFont("Arial", 14));
            fixture.SetAsset(fixture.English, "font", english);
            fixture.SetAsset(fixture.French, "font", french);
            var surface = new Surface(fixture);
            surface.Text.FontReference = new LocalizedFont { TableReference = fixture.Assets, TableEntryReference = "font" };
            surface.Open();
            LocalizationSettings.SelectedLocale = fixture.French;
            LocalizationSettings.SelectedLocale = fixture.English;
            LocalizationSettings.SelectedLocale = fixture.French;
            for (var i = 0; i < 2; i++)
                yield return null;
            Assert.That(surface.Content.alpha, Is.Zero);
            Assert.That(surface.Label.text, Is.EqualTo("original"));
            fixture.CompletePending();
            for (var i = 0; i < 5; i++)
                yield return null;
            Assert.That(surface.State, Is.EqualTo("Ready"));
            Assert.That(surface.Label.text, Is.EqualTo("Epee"));
            Assert.That(surface.Label.font, Is.SameAs(french));
        }

        [UnityTest]
        public IEnumerator NestedGroupOwnsItsMembersAndDoesNotBlockTheOuterGroup()
        {
            using var fixture = new LocalizationFixture { DelayAssetTables = true };
            var font = fixture.Own(Font.CreateDynamicFontFromOSFont("Arial", 12));
            fixture.SetAsset(fixture.English, "font", font);
            var outer = new Surface(fixture);
            var inner = new Surface(fixture);
            inner.Root.transform.SetParent(outer.Content.transform, false);
            inner.Text.FontReference = new LocalizedFont { TableReference = fixture.Assets, TableEntryReference = "font" };
            var innerScope = inner.Open();
            outer.Open();
            for (var i = 0; i < 4; i++)
                yield return null;
            Assert.That(outer.State, Is.EqualTo("Ready"));
            Assert.That(inner.State, Is.EqualTo("Loading"));
            Assert.That(inner.Content.alpha, Is.Zero);
            outer.Type.GetMethod("EndSession").Invoke(outer.Group, null);
            Assert.That(innerScope.IsDisposed, Is.False);
            fixture.CompletePending();
            for (var i = 0; i < 4; i++)
                yield return null;
            Assert.That(inner.State, Is.EqualTo("Ready"));
        }

        [UnityTest]
        public IEnumerator ReparentedMemberLeavesTheOldBarrierAndCanBindIndependently()
        {
            using var fixture = new LocalizationFixture { DelayAssetTables = true };
            var font = fixture.Own(Font.CreateDynamicFontFromOSFont("Arial", 12));
            fixture.SetAsset(fixture.English, "font", font);
            var surface = new Surface(fixture);
            surface.Text.FontReference = new LocalizedFont { TableReference = fixture.Assets, TableEntryReference = "font" };
            var scope = surface.Open();
            yield return null;
            fixture.Own(surface.Text.gameObject);
            surface.Text.transform.SetParent(null, false);
            for (var i = 0; i < 3; i++)
                yield return null;
            Assert.That(surface.State, Is.EqualTo("Ready"));
            surface.Type.GetMethod("EndSession").Invoke(surface.Group, null);
            Assert.That(scope.IsDisposed, Is.True);
            fixture.CompletePending();
            for (var i = 0; i < 4; i++)
                yield return null;
            Assert.That(surface.Label.text, Is.EqualTo("Sword"));
            Assert.That(surface.Label.font, Is.SameAs(font));
        }

        [UnityTest]
        public IEnumerator ReentrantStatusObserversCannotReceiveAnOldReadyAfterTheNewRoundStarts()
        {
            using var fixture = new LocalizationFixture();
            var surface = new Surface(fixture);
            var group = (LocalizationGroup)surface.Group;
            var restarted = false;
            var stale = 0;
            group.StateChanged += state =>
            {
                if (state != LocalizationGroupState.Ready || restarted)
                    return;
                restarted = true;
                group.BeginSession();
            };
            group.StateChanged += state =>
            {
                if (state == LocalizationGroupState.Ready && group.State != LocalizationGroupState.Ready)
                    stale++;
            };
            surface.Open();
            for (var frame = 0; frame < 7; frame++)
                yield return null;
            Assert.That(restarted, Is.True);
            Assert.That(stale, Is.Zero);
            Assert.That(group.State, Is.EqualTo(LocalizationGroupState.Ready));
        }

        [UnityTest]
        public IEnumerator LocalizedFailureMessagesCannotTriggerAnAutomaticRetryLoop()
        {
            using var fixture = new LocalizationFixture(true);
            var surface = new Surface(fixture);
            var status = new GameObject("localized-failure", typeof(RectTransform), typeof(Text));
            status.transform.SetParent(surface.Root.transform, false);
            status.SetActive(false);
            ((LocalizationGroup)surface.Group).FailureRoot = status;
            status.AddComponent<LocalizeText>().StringReference = new LocalizedString(fixture.Strings, "name");
            var loadingRounds = 0;
            ((LocalizationGroup)surface.Group).StateChanged += state =>
            {
                if (state == LocalizationGroupState.Loading)
                    loadingRounds++;
            };
            surface.Open();
            fixture.Settings.CompleteInitialization(false);
            for (var frame = 0; frame < 8; frame++)
                yield return null;
            Assert.That(surface.State, Is.EqualTo("Failed"));
            Assert.That(loadingRounds, Is.EqualTo(1), "Failure display is not authorization to retry the business round.");
            Assert.That(status.activeSelf, Is.True);
        }

        [UnityTest]
        public IEnumerator RealCoreSinglePanelHideShowAndCachedReopenRespectLocalizationRounds()
        {
            using var fixture = new LocalizationFixture();
            var surface = new Surface(fixture);
            surface.Root.SetActive(false);
            surface.Root.AddComponent<PresentationPanel>();
            var canvas = fixture.Own(new GameObject("panel-root", typeof(RectTransform), typeof(Canvas), typeof(GraphicRaycaster)));
            canvas.SetActive(false);
            var root = YLFramework.Core.Tests.CorePanelTestRoots.Create(canvas, 1);
            var module = new PresentationModule();
            try
            {
                var startup = ModuleManager.StartUpModule(module);
                yield return startup;
                Assert.That(startup.Exception, Is.Null);
                var settings = new PanelOverrides { Layer = 0, InstanceMode = PanelInstanceMode.Single, CacheOnClose = true };
                var panel = module.LoadPanel<PresentationPanel>(surface.Root, root: root, overrides: settings);
                for (var frame = 0; frame < 4; frame++)
                    yield return null;
                Assert.That(panel.Scope.IsReady, Is.True);
                var firstScope = panel.Scope;
                panel.Hide();
                Assert.That(panel.IsVisible, Is.False);
                Assert.That(firstScope.IsDisposed, Is.False);
                LocalizationSettings.SelectedLocale = fixture.French;
                for (var frame = 0; frame < 4; frame++)
                    yield return null;
                Assert.That(panel.GetComponentInChildren<Text>(true).text, Is.EqualTo("Epee"));
                panel.Show();
                Assert.That(panel.Scope, Is.SameAs(firstScope));
                Assert.That(panel.IsVisible, Is.True);
                var single = module.LoadPanel<PresentationPanel>(surface.Root, root: root, overrides: settings);
                Assert.That(single, Is.SameAs(panel));
                Assert.That(firstScope.IsDisposed, Is.True);
                var secondScope = single.Scope;
                single.Close();
                Assert.That(secondScope.IsDisposed, Is.True);
                var reopened = module.LoadPanel<PresentationPanel>(surface.Root, root: root, overrides: settings);
                Assert.That(reopened.gameObject, Is.SameAs(panel.gameObject));
                Assert.That(reopened.Scope, Is.Not.SameAs(secondScope));
                for (var frame = 0; frame < 4; frame++)
                    yield return null;
                Assert.That(reopened.Scope.IsReady, Is.True);
            }
            finally
            {
                if (module.State == ModuleState.Running)
                    module.Close();
            }
        }

        private sealed class Surface
        {
            public readonly Type Type;
            public readonly GameObject Root;
            public readonly CanvasGroup Content;
            public readonly Text Label;
            public readonly LocalizeText Text;
            public readonly Component Group;
            public readonly GameObject Loading;
            public readonly GameObject Failure;
            public string State => Type.GetProperty("State").GetValue(Group).ToString();

            public Surface(LocalizationFixture fixture)
            {
                Type = System.Type.GetType("YLFramework.Localization.LocalizationGroup, YLFramework.Localization");
                Assert.That(Type, Is.Not.Null, "A localized panel needs a local content readiness coordinator.");
                Root = fixture.Own(new GameObject("localization-group", typeof(RectTransform)));
                Root.SetActive(false);
                Group = Root.AddComponent(Type);
                var content = new GameObject("content", typeof(RectTransform), typeof(CanvasGroup));
                content.transform.SetParent(Root.transform, false);
                Content = content.GetComponent<CanvasGroup>();
                Type.GetProperty("Content").SetValue(Group, Content);
                Type.GetProperty("AutoStart").SetValue(Group, false);
                Loading = new GameObject("loading");
                Failure = new GameObject("failure");
                Loading.transform.SetParent(Root.transform, false);
                Failure.transform.SetParent(Root.transform, false);
                Type.GetProperty("LoadingRoot").SetValue(Group, Loading);
                Type.GetProperty("FailureRoot").SetValue(Group, Failure);
                var label = new GameObject("label", typeof(RectTransform), typeof(Text));
                label.transform.SetParent(content.transform, false);
                Label = label.GetComponent<Text>();
                Label.text = "original";
                Text = label.AddComponent<LocalizeText>();
                Text.StringReference = new LocalizedString(fixture.Strings, "name");
                Root.SetActive(true);
            }

            public LocalizationSession Open(View owner = null, Action<LocalizationSession> configure = null)
            {
                return (LocalizationSession)Type.GetMethod("BeginSession").Invoke(Group, new object[] { owner, configure });
            }
        }

        public sealed class PresentationOwner : View
        {
        }
        public sealed class PresentationModule : YLFramework.Core.Module
        {
            public override void OnStart()
            {
            }
        }
        public sealed class PresentationPanel : Panel
        {
            public LocalizationSession Scope
            {
                get; private set;
            }
            protected override void OnOpen(LoadViewArgs args) => Scope = GetComponent<LocalizationGroup>().BeginSession(this);
        }
    }
}
