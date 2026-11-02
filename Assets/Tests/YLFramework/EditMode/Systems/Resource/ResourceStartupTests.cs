using System;
using System.Collections;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using YLFramework.Core;
using YLFramework.Resource;
using ResourceLocation = YLFramework.Resource.ResourceLocation;

namespace YLFramework.Tests.Resource
{
    public class ResourceStartupTests
    {
        private readonly ResourceSessionTests _scene = new();
        [SetUp] public void SetUp() => _scene.SetUp();
        [UnityTearDown] public IEnumerator TearDown() => _scene.TearDown();

        private sealed class Source : IResourceSource
        {
            internal TextAsset Text;
            internal AudioClip Clip;
            internal Action OnValidate;
            public ResourceSourceId Id => new("startup-c05");
            public bool SupportsSynchronousLoad => true;
            public void Validate(ResourceCatalogEntry entry) => OnValidate?.Invoke();
            public ResourceSourceResult Load(ResourceCatalogEntry entry, Type type) => new(entry.Location.Locator == "text" ? Text : Clip);
            public ResourceSourceOperation LoadAsync(ResourceCatalogEntry entry, Type type) => new Operation(Load(entry, type).Asset);
            public void Release(UnityEngine.Object value)
            {
            }
            private sealed class Operation : ResourceSourceOperation
            {
                internal Operation(UnityEngine.Object value) => Complete(value, null);
            }
        }

        private sealed class ForeignCore : ICoreResourceProvider
        {
            public CoreResourceRequest<string> ReadTextAsync(string projectId, string address) => throw new NotSupportedException();
            public CorePrefabLease AcquirePrefab(string projectId, string address) => throw new NotSupportedException();
            public CoreResourceRequest<CorePrefabLease> AcquirePrefabAsync(string projectId, string address) => throw new NotSupportedException();
            public CoreResourceRequest<CoreSceneResult> LoadSceneAsync(string projectId, string address, LoadSceneMode mode) => throw new NotSupportedException();
        }

        private static T Take<T>(ResourceRequest<T> request)
        {
            using (request)
            {
                Assert.That(request.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), request.Error?.ToString());
                return request.TakeResult();
            }
        }

        private static ResourceConfiguration Configuration(Source source) => new(new[]
        {
            new ResourceCatalog("startup-c05", new[]
            {
                new ResourceCatalogEntry("text", new ResourceAddress("startup-c05", "message"), new ResourceLocation(source.Id, "text", typeof(TextAsset))),
                new ResourceCatalogEntry("clip", new ResourceAddress("startup-c05", "tone"), new ResourceLocation(source.Id, "clip", typeof(AudioClip)))
            })
        }, new IResourceSource[] { source });

        [UnityTest]
        public IEnumerator StartupJoinAssemblyFailureCancellationAndBusyOnlySettleTheirOwnResponsibilities()
        {
            yield return new EnterPlayMode();
            VerifyStartupResponsibilities();
            yield return new ExitPlayMode();
        }

        private static void VerifyStartupResponsibilities()
        {
            Assert.That(ResourceSystem.Current, Is.Null);
            var textAsset = new TextAsset("c05-shared-business");
            var audioClip = AudioClip.Create("c05-tone", 256, 1, 8000, false);
            var source = new Source { Text = textAsset, Clip = audioClip };
            try
            {
                var config = Configuration(source);
                var owner = new ResourceStartup(config, true, new[] { "startup-c05" });
                Assert.That(owner.Resources, Is.Null);
                Assert.That(CoreResourceAccess.Provider, Is.Null);
                Assert.That(Take(owner.StartAsync()), Is.SameAs(owner));
                var resources = owner.Resources;
                Assert.That(owner.OwnsResourceSystem, Is.True);
                Assert.That(Take(owner.StartAsync()).Resources, Is.SameAs(resources));
                using (var text = owner.CoreProvider.ReadTextAsync("startup-c05", "message"))
                {
                    Assert.That(text.TakeResult(), Is.EqualTo("c05-shared-business"));
                }

                using (var clip = owner.AudioProviders["startup-c05"].Load("tone"))
                {
                    Assert.That(clip.Clip, Is.SameAs(source.Clip));
                    Assert.That(resources.GetDiagnosticsSnapshot().LeaseCount, Is.GreaterThan(0));
                }
                var joined = new ResourceStartup(config, true, new[] { "startup-c05" });
                Take(joined.StartAsync());
                Assert.That(joined.OwnsResourceSystem, Is.False);
                Assert.That(joined.Resources, Is.SameAs(resources));
                using (var busy = owner.StopAsync())
                {
                    Assert.That(busy.Error?.Kind, Is.EqualTo(ResourceError.Busy), "Another startup is a real application owner even without an asset lease.");
                }

                Assert.That(Take(joined.StopAsync()), Is.True);
                Assert.That(CoreResourceAccess.Provider, Is.SameAs(owner.CoreProvider));
                using (var external = resources.Acquire<TextAsset>(new ResourceAddress("startup-c05", "message")))
                {
                    using var busy = owner.StopAsync();
                    Assert.That(busy.Error?.Kind, Is.EqualTo(ResourceError.Busy));
                    Assert.That(resources.IsAvailable, Is.True);
                    Assert.That(CoreResourceAccess.Provider, Is.SameAs(owner.CoreProvider));
                }
                Assert.That(Take(owner.StopAsync()), Is.True);
                Assert.That(ResourceSystem.Current, Is.Null);
                Assert.That(CoreResourceAccess.Provider, Is.Null);

                resources = Take(ResourceSystem.InitializeAsync(config));
                var borrowed = new ResourceStartup(config, true, new[] { "startup-c05" });
                Take(borrowed.StartAsync());
                Assert.That(borrowed.OwnsResourceSystem, Is.False);
                Assert.That(Take(borrowed.StopAsync()), Is.True);
                Assert.That(resources.IsAvailable, Is.True, "A borrowed startup must not close an external initializer's session.");
                Assert.That(CoreResourceAccess.Provider, Is.Null);
                var foreign = new ForeignCore();
                CoreResourceAccess.Bind(foreign);
                var failed = new ResourceStartup(config, true);
                using (var start = failed.StartAsync())
                {
                    Assert.That(start.Error?.Kind, Is.EqualTo(ResourceError.ConfigurationConflict));
                }

                Assert.That(ResourceSystem.Current, Is.SameAs(resources));
                Assert.That(CoreResourceAccess.Provider, Is.SameAs(foreign));
                var conflict = new ResourceStartup(Configuration(source));
                using (var start = conflict.StartAsync())
                {
                    Assert.That(start.Error?.Kind, Is.EqualTo(ResourceError.ConfigurationConflict));
                }

                Assert.That(ResourceSystem.Current, Is.SameAs(resources));
                CoreResourceAccess.Unbind(foreign);
                var missingAudio = new ResourceStartup(config, true, new[] { "missing-c05" });
                using (var start = missingAudio.StartAsync())
                {
                    Assert.That(start.Error?.Kind, Is.EqualTo(ResourceError.AddressNotFound));
                }

                Assert.That(resources.IsAvailable, Is.True);
                Assert.That(CoreResourceAccess.Provider, Is.Null, "Validate all optional consumers before touching the binding slot.");
                Take(resources.ShutdownAsync());

                var ownFailure = new ResourceStartup(config, true);
                CoreResourceAccess.Bind(foreign);
                using (var start = ownFailure.StartAsync())
                {
                    Assert.That(start.Error?.Kind, Is.EqualTo(ResourceError.ConfigurationConflict));
                }

                Assert.That(ResourceSystem.Current, Is.Null, "Assembly failure must close only the resource session created by this startup.");
                Assert.That(CoreResourceAccess.Provider, Is.SameAs(foreign));
                CoreResourceAccess.Unbind(foreign);
                Take(ownFailure.StartAsync());
                CoreResourceAccess.Bind(foreign);
                Take(ownFailure.StopAsync());
                Assert.That(CoreResourceAccess.Provider, Is.SameAs(foreign), "An old startup cannot erase a later explicit replacement.");
                CoreResourceAccess.Unbind(foreign);

                var retry = new ResourceStartup(config, true);
                source.OnValidate = () => throw new InvalidOperationException("c05-initialization-failure");
                using (var start = retry.StartAsync())
                {
                    Assert.That(start.Status, Is.EqualTo(ResourceRequestStatus.Failed));
                }

                Assert.That(ResourceSystem.Current, Is.Null);
                source.OnValidate = null;
                Take(retry.StartAsync());
                Take(retry.StopAsync());

                var reentrant = new ResourceStartup(config, true);
                source.OnValidate = () =>
                {
                    source.OnValidate = null;
                    using var canceled = reentrant.StartAsync();
                    Assert.That(canceled.Status, Is.EqualTo(ResourceRequestStatus.Pending));
                    canceled.Cancel();
                    Assert.That(canceled.Status, Is.EqualTo(ResourceRequestStatus.Canceled));
                };
                Take(reentrant.StartAsync());
                Assert.That(reentrant.Resources.IsAvailable, Is.True);
                Take(reentrant.StopAsync());
                Assert.That(ResourceSystem.Current, Is.Null);

                var retainedFailure = new ResourceStartup(config, audioProjects: new[] { "missing-c05" });
                var otherStartup = new ResourceStartup(config);
                ResourceRequest<ResourceStartup> otherJoin = null;
                source.OnValidate = () =>
                {
                    source.OnValidate = null;
                    otherJoin = otherStartup.StartAsync();
                };
                using (var start = retainedFailure.StartAsync())
                {
                    Assert.That(start.Error?.Kind, Is.EqualTo(ResourceError.AddressNotFound));
                }

                Take(otherJoin);
                var previous = retainedFailure.Resources;
                Assert.That(previous, Is.SameAs(otherStartup.Resources));
                Assert.That(retainedFailure.OwnsResourceSystem, Is.True);
                using (var start = retainedFailure.StartAsync())
                {
                    Assert.That(start.Error?.Kind, Is.EqualTo(ResourceError.AddressNotFound));
                }

                Assert.That(retainedFailure.OwnsResourceSystem, Is.True, "Retry on the same created instance must preserve its cleanup responsibility.");
                Take(otherStartup.StopAsync());
                Take(previous.ShutdownAsync());
                var replacement = Take(ResourceSystem.InitializeAsync(config));
                Assert.That(replacement, Is.Not.SameAs(previous));
                using (var start = retainedFailure.StartAsync())
                {
                    Assert.That(start.Error?.Kind, Is.EqualTo(ResourceError.AddressNotFound));
                }

                Assert.That(replacement.IsAvailable, Is.True, "A failed old startup must not inherit ownership of an external replacement session.");
                Assert.That(ResourceSystem.Current, Is.SameAs(replacement));
                Assert.That(retainedFailure.OwnsResourceSystem, Is.False);
                Take(retainedFailure.StopAsync());
                Take(replacement.ShutdownAsync());

                var ended = new ResourceStartup(config, true);
                source.OnValidate = () =>
                {
                    source.OnValidate = null;
                    typeof(ResourceSystem).GetMethod("EndSession", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null);
                };
                using (var start = ended.StartAsync())
                {
                    Assert.That(start.Status, Is.EqualTo(ResourceRequestStatus.Invalidated));
                    Assert.That(start.Error?.Kind, Is.EqualTo(ResourceError.SessionUnavailable));
                }
                Assert.That(ended.Resources, Is.Null);
            }
            finally
            {
                if (ResourceSystem.Current != null)
                {
                    using (var stop = ResourceSystem.Current.ShutdownAsync())
                    {
                    }
                }

                UnityEngine.Object.DestroyImmediate(source.Text);
                UnityEngine.Object.DestroyImmediate(source.Clip);
            }
        }
    }
}
