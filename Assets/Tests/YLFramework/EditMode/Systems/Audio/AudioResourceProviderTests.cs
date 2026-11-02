using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using YLFramework.Audio;
using YLFramework.Resource;
using YLFramework.Tests.Resource;

namespace YLFramework.Tests.Audio
{
    public class AudioResourceProviderTests
    {
        [Test]
        public void LegacyLibraryLocationsRequireExplicitProjectAddressMigration()
        {
            var library = ScriptableObject.CreateInstance<AudioClipLibrary>();
            var clip = AudioClip.Create("borrowed86b", 4410, 1, 44100, false);
            try
            {
                library.groups.Add(new AudioClipGroup
                {
                    name = "legacy",
                    entries = new()
                {
                    new AudioClipEntry { source = AudioClipSource.Resources, key = "resources://old-clip" }
                }
                });
                library.groups.Add(new AudioClipGroup
                {
                    name = "direct",
                    entries = new()
                {
                    new AudioClipEntry { clip = clip }
                }
                });
                Assert.That(library.TrySelect("legacy", out _), Is.False, "Old keys must not start the legacy resource authority or guess a project.");
                Assert.That(library.TrySelect("direct", out var selected), Is.True);
                Assert.That(selected.Clip, Is.SameAs(clip));
                Assert.That(ResourceSystem.Current, Is.Null);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(library);
                UnityEngine.Object.DestroyImmediate(clip);
            }
        }

        [UnityTest]
        [Category("YL.Special.AudioSession")]
        public IEnumerator ExplicitProviderCapturesAuthorityAndRejectsEndedSession()
        {
            new ResourceSessionTests().SetUp();
            yield return new EnterPlayMode();
            yield return ProviderBody();
        }

        private static IEnumerator ProviderBody()
        {
            var config = new ResourceConfiguration(new[]
            {
                new ResourceCatalog("audio86b", new[]
                {
                    new ResourceCatalogEntry("text", new ResourceAddress("audio86b", "wrong-type"),
                        new YLFramework.Resource.ResourceLocation(ResourceSourceId.Resources, "YLResource81/Message", typeof(TextAsset)))
                })
            });
            using var initialization = ResourceSystem.InitializeAsync(config);
            var system = initialization.TakeResult();
            var provider = system.GetAudioClipProvider("audio86b");
            Assert.That(system.GetAudioClipProvider("audio86b"), Is.SameAs(provider));
            var library = ScriptableObject.CreateInstance<AudioClipLibrary>();
            try
            {
                library.groups.Add(new AudioClipGroup
                {
                    name = "formal",
                    entries = new()
                {
                    new AudioClipEntry { source = AudioClipSource.Resource, projectId = "audio86b", key = "wrong-type" }
                }
                });
                Assert.That(library.TrySelect("formal", out var first), Is.True);
                Assert.That(library.TrySelect("formal", out var second), Is.True);
                Assert.That(first.Address.Provider, Is.SameAs(provider));
                Assert.That(second.Address.Provider, Is.SameAs(provider));
                Assert.That(first.Address.Key, Is.EqualTo("wrong-type"));
            }
            finally
            {
                UnityEngine.Object.Destroy(library);
            }
            Assert.That(Assert.Throws<ResourceException>(() => provider.Load("wrong-type")).Kind, Is.EqualTo(ResourceError.TypeMismatch));
            var calls = 0;
            provider.LoadAsync("absent", (lease, error) =>
            {
                Assert.That(lease, Is.Null);
                Assert.That(error, Is.TypeOf<ResourceException>());
                calls++;
            }, null);
            Assert.That(calls, Is.EqualTo(1));
            using var shutdown = system.ShutdownAsync();
            yield return shutdown;
            Assert.That(shutdown.Status, Is.EqualTo(ResourceRequestStatus.Succeeded));
            Assert.That(Assert.Throws<ResourceException>(() => provider.Load("wrong-type")).Kind, Is.EqualTo(ResourceError.SessionUnavailable));
            Assert.That(ResourceSystem.Current, Is.Null);
            Assert.That(system.GetDiagnosticsSnapshot().RecordCount, Is.Zero);
        }

        [UnityTest]
        [Category("YL.Special.AudioSession")]
        public IEnumerator ProgressObservesPendingResourceWithoutOwningCompletionAndObserverFailuresRelease()
        {
            new ResourceSessionTests().SetUp();
            yield return new EnterPlayMode();
            yield return ProgressBody();
        }

        private static IEnumerator ProgressBody()
        {
            var source = new ProgressSource();
            var clip = AudioClip.Create("Progress86b", 44100, 1, 44100, false);
            using var initialization = ResourceSystem.InitializeAsync(new ResourceConfiguration(new[]
            {
                new ResourceCatalog("audio-progress", new[] { new ResourceCatalogEntry("signal", new ResourceAddress("audio-progress", "signal"),
                    new YLFramework.Resource.ResourceLocation(source.Id, "signal", typeof(AudioClip))) })
            }, new IResourceSource[] { source }));
            var system = initialization.TakeResult();
            var provider = system.GetAudioClipProvider("audio-progress");
            AudioClipLease delivered = null;
            try
            {
                var values = new List<float>();
                provider.LoadAsync("signal", (lease, error) =>
                {
                    Assert.That(error, Is.Null);
                    delivered = lease;
                }, values.Add);
                source.Pending.Value = .65f;
                for (var i = 0; i < 3; i++)
                {
                    yield return null;
                }

                Assert.That(values.Any(x => x > 0 && x < 1), Is.True, "Audio request progress must not remain at zero until physical completion.");
                Assert.That(values.Last(), Is.EqualTo(.65f), "Progress must continue observing the producer after initial submission.");
                Assert.That(delivered, Is.Null);
                source.Pending.Finish(clip);
                Assert.IsTrue(delivered.Clip);
                Assert.That(values.Last(), Is.EqualTo(1));
                delivered.Dispose();
                delivered = null;
                Assert.That(system.GetDiagnosticsSnapshot().RecordCount, Is.Zero);

                var callbacks = 0;
                LogAssert.Expect(LogType.Exception, "InvalidOperationException: audio86b-progress-observer");
                provider.LoadAsync("signal", (lease, error) =>
                {
                    Assert.That(error, Is.Null);
                    Assert.IsTrue(lease.Clip);
                    callbacks++;
                    throw new InvalidOperationException("audio86b-completion-observer");
                }, _ => throw new InvalidOperationException("audio86b-progress-observer"));
                yield return null;
                LogAssert.Expect(LogType.Exception, "InvalidOperationException: audio86b-progress-observer");
                LogAssert.Expect(LogType.Exception, "InvalidOperationException: audio86b-completion-observer");
                source.Pending.Finish(clip);
                Assert.That(callbacks, Is.EqualTo(1));
                Assert.That(system.GetDiagnosticsSnapshot().RecordCount, Is.Zero);
                using var shutdown = system.ShutdownAsync();
                Assert.That(shutdown.TakeResult(), Is.True);
            }
            finally
            {
                if (source.Pending != null && !source.Pending.IsDone)
                {
                    source.Pending.Finish(clip);
                }

                delivered?.Dispose();
                UnityEngine.Object.Destroy(clip);
            }
        }

        private sealed class ProgressOperation : ResourceSourceOperation
        {
            internal float Value = .2f;
            public override float Progress => IsDone ? 1 : Value;
            internal void Finish(AudioClip clip) => Complete(clip, null);
        }
        private sealed class ProgressSource : IResourceSource
        {
            public ResourceSourceId Id => ResourceSourceId.Resources;
            public bool SupportsSynchronousLoad => true;
            internal ProgressOperation Pending;
            public void Validate(ResourceCatalogEntry entry)
            {
            }
            public ResourceSourceResult Load(ResourceCatalogEntry entry, Type type) => throw new InvalidOperationException("Async-only fixture.");
            public ResourceSourceOperation LoadAsync(ResourceCatalogEntry entry, Type type) => Pending = new ProgressOperation();
            public void Release(UnityEngine.Object asset)
            {
            }
        }

        [UnityTearDown]
        public IEnumerator RestoreSession()
        {
            var cleanup = new ResourceSessionTests().TearDown();
            while (cleanup.MoveNext())
            {
                yield return cleanup.Current;
            }
        }
    }
}
