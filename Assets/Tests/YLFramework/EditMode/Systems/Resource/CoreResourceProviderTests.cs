using System.Collections;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using YLFramework.Core;
using YLFramework.Resource;

namespace YLFramework.Tests.Resource
{
    public class CoreResourceProviderTests
    {
        private const string ExitKey = "YL.Core86.ProviderExit";
        private static CoreResourceRequest<string> _unclaimed;

        [UnityTest]
        public IEnumerator RealExitInvalidatesUnclaimedCoreRequestsAndRemovesBinding()
        {
            new ResourceSessionTests().SetUp();
            SessionState.SetBool(ExitKey, false);
            yield return new EnterPlayMode();
            var startup = new ResourceStartup(new ResourceConfiguration(), bindCoreResourceProvider: false);
            yield return ResourceSessionTests.Start(startup);
            var system = startup.Resources;
            var provider = system.BindCoreResourceProvider();
            _unclaimed = provider.ReadTextAsync("ownership", "message");
            yield return ResourceSessionTests.Wait(() => _unclaimed.Status != CoreResourceStatus.Pending, "unclaimed Core text result");
            Assert.That(_unclaimed.Status, Is.EqualTo(CoreResourceStatus.Succeeded), _unclaimed.Error?.ToString());
            Assert.That(_unclaimed.IsDisposed, Is.False);
            Assert.That(system.GetDiagnostics().LeaseCount, Is.Zero, "Copied text must not retain the temporary asset.");
            EditorApplication.playModeStateChanged += ObserveExit;
            yield return new ExitPlayMode();
            Assert.That(SessionState.GetBool(ExitKey, false), Is.True);
            SessionState.EraseBool(ExitKey);
        }

        private static void ObserveExit(PlayModeStateChange state)
        {
            if (state != PlayModeStateChange.ExitingPlayMode)
            {
                return;
            }

            try
            {
                bool rejected = false;
                try
                {
                    _unclaimed.TakeResult();
                }
                catch (System.ObjectDisposedException) { rejected = true; }
                SessionState.SetBool(ExitKey, CoreResourceAccess.Provider == null && _unclaimed.IsDisposed &&
                    _unclaimed.Status == CoreResourceStatus.Succeeded && rejected);
            }
            finally
            {
                EditorApplication.playModeStateChanged -= ObserveExit;
                _unclaimed.Dispose();
                _unclaimed = null;
            }
        }

        [UnityTest]
        public IEnumerator ExplicitBindingCopiesTextAndShutdownUnbindsOnlyItsProvider()
        {
            new ResourceSessionTests().SetUp();
            yield return new EnterPlayMode();
            yield return BindingBody();
        }

        private static IEnumerator BindingBody()
        {
            using var startup = new ResourceStartup(new ResourceConfiguration(), bindCoreResourceProvider: false);
            yield return ResourceSessionTests.Start(startup);
            var system = startup.Resources;
            Assert.That(CoreResourceAccess.Provider, Is.Null, "Initialization alone must not configure Core.");
            var provider = system.BindCoreResourceProvider();
            Assert.That(provider, Is.Not.Null);
            Assert.That(CoreResourceAccess.Provider, Is.SameAs(provider));
            using var loading = system.AcquireAsync<TextAsset>(ResourceSessionTests.MessageKey);
            yield return ResourceSessionTests.Wait(() => loading.IsDone, "independent text owner");
            Assert.That(loading.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), loading.Error?.ToString());
            using var independent = loading.TakeResult();
            using var read = provider.ReadTextAsync("ownership", "message");
            yield return ResourceSessionTests.Wait(() => read.Status != CoreResourceStatus.Pending, "Core copied text");

            Assert.That(read.Status, Is.EqualTo(CoreResourceStatus.Succeeded), read.Error?.ToString());
            Assert.That(system.GetDiagnostics().LeaseCount, Is.EqualTo(1), "Text is released before Core receives success.");
            Assert.That(read.TakeResult(), Is.EqualTo(independent.Asset.text));
            Assert.Throws<System.InvalidOperationException>(() => read.TakeResult());
            Assert.That(CoreResourceAccess.Unbind(provider), Is.True);
            Assert.That(CoreResourceAccess.Provider, Is.Null);
            Assert.That(system.BindCoreResourceProvider(), Is.SameAs(provider));
            independent.Dispose();
            yield return ResourceSessionTests.Stop(startup);
            Assert.That(CoreResourceAccess.Provider, Is.Null);
            using var secondStartup = new ResourceStartup(new ResourceConfiguration());
            yield return ResourceSessionTests.Start(secondStartup);
            var second = secondStartup.Resources;
            var other = new OtherBinding();
            CoreResourceAccess.Bind(other);
            try
            {
                using var rejected = provider.ReadTextAsync("ownership", "message");
                Assert.That(rejected.Status, Is.EqualTo(CoreResourceStatus.Failed));
                Assert.That(rejected.Error.Kind, Is.EqualTo(CoreResourceError.Unavailable));
                yield return ResourceSessionTests.Stop(startup);
                Assert.That(second.IsAvailable, Is.True, "A stale Startup cannot stop the replacement service.");
                Assert.That(ResourceSystem.Current, Is.SameAs(second));
                Assert.That(CoreResourceAccess.Unbind(provider), Is.False);
                yield return ResourceSessionTests.Stop(secondStartup);
                Assert.That(CoreResourceAccess.Provider, Is.SameAs(other), "A former owner cannot remove a replacement binding.");
            }
            finally
            {
                CoreResourceAccess.Unbind(other);
            }
        }

        private sealed class OtherBinding : ICoreResourceProvider
        {
            public CoreResourceRequest<string> ReadTextAsync(string projectId, string address) => throw new System.NotSupportedException();
            public CorePrefabLease AcquirePrefab(string projectId, string address) => throw new System.NotSupportedException();
            public CoreResourceRequest<CorePrefabLease> AcquirePrefabAsync(string projectId, string address) => throw new System.NotSupportedException();
            public CoreResourceRequest<CoreSceneResult> LoadSceneAsync(string projectId, string address, LoadSceneMode mode) => throw new System.NotSupportedException();
        }

        [UnityTearDown]
        public IEnumerator ExitSession()
        {
            EditorApplication.playModeStateChanged -= ObserveExit;
            _unclaimed?.Dispose();
            _unclaimed = null;
            var cleanup = new ResourceSessionTests().TearDown();
            while (cleanup.MoveNext())
            {
                yield return cleanup.Current;
            }
        }
    }
}
