using System.Collections;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using YLFramework.Core;
using YLFramework.Resource;
using ResourceLocation = YLFramework.Resource.ResourceLocation;

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
            yield return new EnterPlayMode();
            using (var initialize = ResourceSystem.InitializeAsync(Configuration()))
            {
                var system = initialize.TakeResult();
                var provider = system.BindCoreResourceProvider();
                _unclaimed = provider.ReadTextAsync("core86", "text");
                while (_unclaimed.Status == CoreResourceStatus.Pending)
                {
                    yield return null;
                }

                Assert.That(_unclaimed.Status, Is.EqualTo(CoreResourceStatus.Succeeded));
                EditorApplication.playModeStateChanged += ObserveExit;
            }
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

            bool rejected = false;
            try
            {
                _unclaimed.TakeResult();
            }
            catch (System.ObjectDisposedException)
            {
                rejected = true;
            }
            SessionState.SetBool(ExitKey, CoreResourceAccess.Provider == null && _unclaimed.IsDisposed &&
                _unclaimed.Status == CoreResourceStatus.Succeeded && rejected);
            EditorApplication.playModeStateChanged -= ObserveExit;
            _unclaimed.Dispose();
            _unclaimed = null;
        }

        private static ResourceConfiguration Configuration() => new(new[]
        {
            new ResourceCatalog("core86", new[]
            {
                new ResourceCatalogEntry("text", new ResourceAddress("core86", "text"),
                    new ResourceLocation(ResourceSourceId.Resources, "YLResource81/Message", typeof(TextAsset)))
            })
        });

        [UnityTest]
        public IEnumerator ExplicitBindingCopiesTextAndShutdownUnbindsOnlyItsProvider()
        {
            new ResourceSessionTests().SetUp();
            yield return new EnterPlayMode();
            yield return BindingBody();
        }

        private static IEnumerator BindingBody()
        {
            using var initialize = ResourceSystem.InitializeAsync(Configuration());
            var system = initialize.TakeResult();
            Assert.That(CoreResourceAccess.Provider, Is.Null, "Initialization alone must not configure Core.");
            var bind = typeof(ResourceSystem).GetMethod("BindCoreResourceProvider", BindingFlags.Instance | BindingFlags.Public);
            Assert.That(bind, Is.Not.Null, "Resource must provide explicit Core composition.");
            var provider = (ICoreResourceProvider)bind.Invoke(system, null);
            Assert.That(provider, Is.Not.Null);
            Assert.That(CoreResourceAccess.Provider, Is.SameAs(provider));
            using var independent = system.Acquire<TextAsset>(new ResourceAddress("core86", "text"));
            using var read = provider.ReadTextAsync("core86", "text");
            while (read.Status == CoreResourceStatus.Pending)
            {
                yield return null;
            }

            Assert.That(read.Status, Is.EqualTo(CoreResourceStatus.Succeeded), read.Error?.ToString());
            Assert.That(system.GetDiagnosticsSnapshot().LeaseCount, Is.EqualTo(1), "Text is released before Core receives success.");
            Assert.That(read.TakeResult(), Is.EqualTo(independent.Asset.text));
            Assert.Throws<System.InvalidOperationException>(() => read.TakeResult());
            Assert.That(CoreResourceAccess.Unbind(provider), Is.True);
            Assert.That(CoreResourceAccess.Provider, Is.Null);
            bind.Invoke(system, null);
            independent.Dispose();
            using var shutdown = system.ShutdownAsync();
            yield return shutdown;
            Assert.That(shutdown.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), shutdown.Error?.ToString());
            Assert.That(CoreResourceAccess.Provider, Is.Null);
            using var secondInitialize = ResourceSystem.InitializeAsync(Configuration());
            var second = secondInitialize.TakeResult();
            second.BindCoreResourceProvider();
            var other = new OtherBinding();
            CoreResourceAccess.Bind(other);
            try
            {
                using var secondShutdown = second.ShutdownAsync();
                yield return secondShutdown;
                Assert.That(secondShutdown.Status, Is.EqualTo(ResourceRequestStatus.Succeeded));
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
            var cleanup = new ResourceSessionTests().TearDown();
            while (cleanup.MoveNext())
            {
                yield return cleanup.Current;
            }
        }
    }
}
