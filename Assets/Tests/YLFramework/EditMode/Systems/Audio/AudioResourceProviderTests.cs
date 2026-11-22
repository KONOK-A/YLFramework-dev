using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.AddressableAssets.ResourceLocators;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.ResourceManagement.ResourceLocations;
using UnityEngine.ResourceManagement.ResourceProviders;
using UnityEngine.TestTools;
using YLFramework.Audio;
using YLFramework.Resource;
using YLFramework.Tests.Resource;

namespace YLFramework.Tests.Audio
{
    public class AudioResourceProviderTests
    {
        private static OfficialFixture _fixture;

        [Test]
        public void RetiredLibraryLocationsAreRejectedWithoutCreatingAResourceAuthority()
        {
            var library = ScriptableObject.CreateInstance<AudioClipLibrary>();
            var clip = AudioClip.Create("borrowed86b", 4410, 1, 44100, false);
            try
            {
                library.groups.Add(new AudioClipGroup
                {
                    name = "legacy",
                    entries = new() { new AudioClipEntry { source = (AudioClipSource)1, key = "resources://old-clip" } }
                });
                library.groups.Add(new AudioClipGroup
                {
                    name = "direct",
                    entries = new() { new AudioClipEntry { clip = clip } }
                });
                Assert.That(library.TrySelect("legacy", out _), Is.False, "Old locators do not initialize a service or guess a project.");
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
            yield return CleanupRuntime();
            yield return new ExitPlayMode();
        }

        private static IEnumerator ProviderBody()
        {
            _fixture = new OfficialFixture("audio86b");
            yield return _fixture.Start();
            var system = _fixture.System;
            var provider = system.GetAudioClipProvider(_fixture.Project);
            Assert.That(system.GetAudioClipProvider(_fixture.Project), Is.SameAs(provider));
            var library = ScriptableObject.CreateInstance<AudioClipLibrary>();
            try
            {
                library.groups.Add(new AudioClipGroup
                {
                    name = "formal",
                    entries = new() { new AudioClipEntry { source = AudioClipSource.Resource, projectId = _fixture.Project, key = "wrong-type" } }
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
            Assert.That(_fixture.Locator.Locate(_fixture.Project + "/wrong-type", typeof(TextAsset), out var text), Is.True);
            Assert.That(text.Single().ResourceType, Is.EqualTo(typeof(TextAsset)));
            Assert.That(Assert.Throws<ResourceException>(() => provider.Load("wrong-type")).Kind, Is.EqualTo(ResourceError.InProgress),
                "Cold synchronous access may not query or start an official producer, even for a mistyped key.");
            Assert.That(_fixture.Provider.Provides, Is.Zero);
            var wrongCalls = 0;
            Exception wrong = null;
            provider.LoadAsync("wrong-type", (lease, error) =>
            {
                Assert.That(lease, Is.Null, "A TextAsset must never be delivered as an AudioClip.");
                wrong = error;
                wrongCalls++;
            }, null);
            yield return ResourceSessionTests.Wait(() => wrongCalls != 0, "official AudioClip type filtering");
            Assert.That(wrongCalls, Is.EqualTo(1));
            Assert.That(wrong, Is.TypeOf<ResourceException>());
            Assert.That(((ResourceException)wrong).Kind, Is.EqualTo(ResourceError.AddressNotFound),
                "The current typed AA lookup reports no matching AudioClip location, not the retired catalog error.");
            Assert.That(_fixture.Provider.Provides, Is.Zero, "Type rejection occurs before invoking the real provider.");
            var missingCalls = 0;
            provider.LoadAsync("absent", (lease, error) =>
            {
                Assert.That(lease, Is.Null);
                Assert.That(error, Is.TypeOf<ResourceException>());
                Assert.That(((ResourceException)error).Kind, Is.EqualTo(ResourceError.AddressNotFound));
                missingCalls++;
            }, null);
            yield return ResourceSessionTests.Wait(() => missingCalls != 0, "absent official location");
            Assert.That(missingCalls, Is.EqualTo(1));
            yield return ResourceSessionTests.Stop(_fixture.Startup);
            Assert.That(Assert.Throws<ResourceException>(() => provider.Load("wrong-type")).Kind, Is.EqualTo(ResourceError.SessionUnavailable));
            Assert.That(ResourceSystem.Current, Is.Null);
            AssertEmpty(system);
        }

        [UnityTest]
        [Category("YL.Special.AudioSession")]
        public IEnumerator ProgressObservesPendingResourceWithoutOwningCompletionAndObserverFailuresRelease()
        {
            new ResourceSessionTests().SetUp();
            yield return new EnterPlayMode();
            yield return ProgressBody();
            yield return CleanupRuntime();
            yield return new ExitPlayMode();
        }

        private static IEnumerator ProgressBody()
        {
            _fixture = new OfficialFixture("audio-progress");
            yield return _fixture.Start();
            var system = _fixture.System;
            var provider = system.GetAudioClipProvider(_fixture.Project);
            AudioClipLease delivered = null;
            var accepting = true;
            try
            {
                var values = new List<float>();
                var completions = 0;
                provider.LoadAsync("signal", (lease, error) =>
                {
                    if (!accepting)
                    {
                        lease?.Dispose();
                        if (error != null)
                        {
                            ExceptionDispatchInfo.Capture(error).Throw();
                        }
                        return;
                    }
                    Assert.That(error, Is.Null);
                    delivered = lease;
                    completions++;
                }, values.Add);
                _fixture.External = Addressables.ResourceManager.ProvideResource<AudioClip>(_fixture.Signal);
                yield return ResourceSessionTests.Wait(() => _fixture.Provider.Pending != null, "actual official pending producer");
                // ProviderOperation includes its already-settled empty dependency stage: (1 + .3) / 2 = .65.
                _fixture.Provider.Pending.Value = .3f;
                Assert.That(_fixture.External.IsDone, Is.False);
                Assert.That(_fixture.External.PercentComplete, Is.EqualTo(.65f).Within(.0001f),
                    "The official operation itself, not a manually edited YL request, reports the controlled progress.");
                for (var index = 0; index < 3; index++)
                {
                    yield return null;
                }
                Assert.That(values.Any(value => value > 0 && value < 1), Is.True, "Audio progress may not stay zero until physical completion.");
                Assert.That(values.Last(), Is.EqualTo(.65f).Within(.0001f));
                Assert.That(delivered, Is.Null);
                Assert.That(completions, Is.Zero, "Progress is not a completion or lease-ownership signal.");
                _fixture.Provider.Pending.Finish();
                yield return ResourceSessionTests.Wait(() => delivered != null, "actual Audio ownership handoff");
                Assert.That(completions, Is.EqualTo(1));
                Assert.That(delivered.Clip, Is.SameAs(_fixture.Clip));
                Assert.That(values.Last(), Is.EqualTo(1));
                delivered.Dispose();
                delivered = null;
                yield return ResourceSessionTests.Wait(() => Empty(system), "YL return while independent official consumer stays alive");
                Assert.That(_fixture.External.IsValid() && _fixture.External.Result, Is.True);
                Assert.That(_fixture.Provider.SignalReleases, Is.Zero, "Returning YL ownership must not release the independent official owner.");
                _fixture.ReleaseExternal();
                yield return ResourceSessionTests.Wait(() => _fixture.Provider.SignalReleases == 1, "last independent official consumer");
                Assert.That(_fixture.Clip, Is.Not.Null, "The provider borrows this test-owned AudioClip; Release is not Destroy.");

                var callbacks = 0;
                var pendingObserverThrew = false;
                LogAssert.Expect(LogType.Exception, "InvalidOperationException: audio86b-progress-observer");
                provider.LoadAsync("signal", (lease, error) =>
                {
                    Assert.That(error, Is.Null);
                    Assert.That(lease.Clip, Is.SameAs(_fixture.Clip));
                    callbacks++;
                    throw new InvalidOperationException("audio86b-completion-observer");
                }, value =>
                {
                    if (value >= 1 || value > 0 && !pendingObserverThrew)
                    {
                        pendingObserverThrew = value < 1 || pendingObserverThrew;
                        throw new InvalidOperationException("audio86b-progress-observer");
                    }
                });
                yield return ResourceSessionTests.Wait(() => _fixture.Provider.Provides == 2 && _fixture.Provider.Pending != null,
                    "second real official producer");
                _fixture.Provider.Pending.Value = .3f;
                yield return ResourceSessionTests.Wait(() => pendingObserverThrew, "real in-flight observer exception");
                Assert.That(callbacks, Is.Zero);
                LogAssert.Expect(LogType.Exception, "InvalidOperationException: audio86b-progress-observer");
                LogAssert.Expect(LogType.Exception, "InvalidOperationException: audio86b-completion-observer");
                _fixture.Provider.Pending.Finish();
                yield return ResourceSessionTests.Wait(() => callbacks != 0 && Empty(system), "throwing observers return unaccepted success ownership");
                Assert.That(callbacks, Is.EqualTo(1));
                Assert.That(_fixture.Provider.SignalReleases, Is.EqualTo(2));
                Assert.That(_fixture.Clip, Is.Not.Null);
                yield return ResourceSessionTests.Stop(_fixture.Startup);
                AssertEmpty(system);
            }
            finally
            {
                accepting = false;
                delivered?.Dispose();
            }
        }

        [UnityTest]
        [Category("YL.Special.AudioSession")]
        public IEnumerator LaterOfficialFailuresDoNotStealPreviouslyDeliveredAudioOrResourceLeases()
        {
            new ResourceSessionTests().SetUp();
            yield return new EnterPlayMode();
            yield return FailureIsolationBody();
            yield return CleanupRuntime();
            yield return new ExitPlayMode();
        }

        private static IEnumerator FailureIsolationBody()
        {
            _fixture = new OfficialFixture("audio-admission");
            yield return _fixture.Start();
            var system = _fixture.System;
            var provider = system.GetAudioClipProvider(_fixture.Project);
            using var loaded = system.AcquireAsync<AudioClip>(_fixture.Project + "/signal");
            yield return ResourceSessionTests.Wait(() => _fixture.Provider.Pending != null, "original real official asset");
            _fixture.Provider.Pending.Finish();
            yield return ResourceSessionTests.Wait(() => loaded.IsDone, "original independent Resource lease");
            using var independent = loaded.TakeResult();
            using var cache = new AudioClipCache();
            using var held = cache.Load(new AudioClipAddress(provider, "signal"));
            Assert.That(held.Clip, Is.SameAs(_fixture.Clip));
            Assert.That(_fixture.Provider.SignalProvides, Is.EqualTo(1));
            var errors = 0;
            Exception failure = null;
            LogAssert.Expect(LogType.Error, "System.InvalidOperationException: official-audio-independent-failure");
            using var waiter = cache.LoadAsync(new AudioClipAddress(provider, "failure"), (lease, error) =>
            {
                Assert.That(lease, Is.Null);
                failure = error;
                errors++;
            }, null);
            yield return ResourceSessionTests.Wait(() => errors != 0, "independent official backend failure");
            Assert.That(errors, Is.EqualTo(1));
            Assert.That(failure, Is.TypeOf<ResourceException>());
            Assert.That(((ResourceException)failure).Kind, Is.EqualTo(ResourceError.LoadFailed));
            using (var typed = system.AcquireAsync<TextAsset>(_fixture.Project + "/signal"))
            {
                yield return ResourceSessionTests.Wait(() => typed.IsDone, "independent typed lookup failure");
                Assert.That(typed.Status, Is.EqualTo(ResourceRequestStatus.Failed));
                Assert.That(typed.Error.Kind, Is.EqualTo(ResourceError.AddressNotFound));
            }
            Assert.That(independent.IsValid, Is.True);
            Assert.That(independent.Asset, Is.SameAs(_fixture.Clip));
            Assert.That(held.Clip, Is.SameAs(_fixture.Clip));
            Assert.That(_fixture.Provider.SignalProvides, Is.EqualTo(1), "Unrelated failures must not reopen the successful cached signal backend.");
            Assert.That(_fixture.Provider.SignalReleases, Is.Zero);
            using (var next = cache.Load(new AudioClipAddress(provider, "signal")))
            {
                Assert.That(next.Clip, Is.SameAs(_fixture.Clip), "An independent failed location does not fault admission to a healthy cached signal.");
            }
            held.Dispose();
            cache.Dispose();
            Assert.That(independent.IsValid, Is.True, "Returning Audio/cache ownership must not return the already delivered independent lease.");
            Assert.That(_fixture.Provider.SignalReleases, Is.Zero);
            independent.Dispose();
            yield return ResourceSessionTests.Wait(() => _fixture.Provider.SignalReleases == 1 && Empty(system), "the actual final successful signal owner");
            Assert.That(_fixture.Clip, Is.Not.Null);
            yield return ResourceSessionTests.Stop(_fixture.Startup);
            AssertEmpty(system);
        }

        [UnityTearDown]
        public IEnumerator RestoreSession()
        {
            var errors = new List<Exception>();
            foreach (var cleanup in new[] { CleanupRuntime(), new ResourceSessionTests().TearDown() })
            {
                var stack = new Stack<IEnumerator>();
                stack.Push(cleanup);
                try
                {
                    while (stack.Count != 0)
                    {
                        var current = stack.Peek();
                        bool next;
                        try
                        {
                            next = current.MoveNext();
                        }
                        catch (Exception error)
                        {
                            errors.Add(error);
                            break;
                        }
                        if (!next)
                        {
                            stack.Pop();
                            try
                            {
                                (current as IDisposable)?.Dispose();
                            }
                            catch (Exception error)
                            {
                                errors.Add(error);
                            }
                            continue;
                        }
                        if (current.Current is IEnumerator nested)
                        {
                            stack.Push(nested);
                        }
                        else
                        {
                            yield return current.Current;
                        }
                    }
                }
                finally
                {
                    while (stack.Count != 0)
                    {
                        try
                        {
                            (stack.Pop() as IDisposable)?.Dispose();
                        }
                        catch (Exception error)
                        {
                            errors.Add(error);
                        }
                    }
                }
            }
            if (errors.Count == 1)
            {
                ExceptionDispatchInfo.Capture(errors[0]).Throw();
            }
            if (errors.Count > 1)
            {
                throw new AggregateException(errors);
            }
        }

        private static IEnumerator CleanupRuntime()
        {
            if (_fixture == null)
            {
                yield break;
            }
            var fixture = _fixture;
            try
            {
                if (Application.isPlaying)
                {
                    fixture.Provider.Pending?.Finish();
                    fixture.ReleaseExternal();
                    if (fixture.System != null)
                    {
                        yield return ResourceSessionTests.Wait(() => Empty(fixture.System), "finish owned pending callbacks before stopping the service");
                        AssertEmpty(fixture.System);
                    }
                    if (fixture.Startup.Resources != null)
                    {
                        yield return ResourceSessionTests.Stop(fixture.Startup);
                    }
                    if (fixture.System != null)
                    {
                        AssertEmpty(fixture.System);
                    }
                }
            }
            finally
            {
                fixture.Remove();
                _fixture = null;
            }
        }

        private static bool Empty(ResourceSystem system)
        {
            var report = system.GetDiagnostics();
            return report.LeaseCount == 0 && report.ScopeCount == 0 && report.RequestCount == 0 && report.PendingWorkCount == 0 && report.DrainingHandleCount == 0;
        }
        private static void AssertEmpty(ResourceSystem system)
        {
            Assert.That(Empty(system), Is.True);
            Assert.That(system.GetDiagnostics().Errors, Is.Empty);
        }

        private sealed class OfficialFixture
        {
            internal readonly string Project;
            internal readonly ResourceStartup Startup;
            internal ResourceSystem System;
            internal readonly AudioClip Clip;
            internal readonly TextAsset Text;
            internal readonly ControlledProvider Provider;
            internal readonly ResourceLocationMap Locator;
            internal readonly IResourceLocation Signal;
            internal AsyncOperationHandle<AudioClip> External;
            internal OfficialFixture(string project)
            {
                Assert.That(Application.isPlaying, Is.True, "Construct live ownership only after the top-level EnterPlayMode instruction.");
                Assert.That(ResourceSystem.Current, Is.Null);
                Project = project;
                Startup = new ResourceStartup(new ResourceConfiguration(), bindCoreResourceProvider: false, audioProjects: new[] { project });
                Clip = AudioClip.Create("AudioProvider-" + project, 44100, 1, 44100, false);
                Text = new TextAsset("A real typed non-Audio asset");
                Provider = new ControlledProvider("Audio.Provider." + Guid.NewGuid().ToString("N"), Clip, Text);
                Locator = new ResourceLocationMap(Provider.ProviderId);
                Signal = new ResourceLocationBase(project + "/signal", "signal", Provider.ProviderId, typeof(AudioClip));
                Locator.Add(project + "/signal", Signal);
                Locator.Add(project + "/wrong-type", new ResourceLocationBase(project + "/wrong-type", "wrong-type", Provider.ProviderId, typeof(TextAsset)));
                Locator.Add(project + "/failure", new ResourceLocationBase(project + "/failure", "failure", Provider.ProviderId, typeof(AudioClip)));
            }
            internal IEnumerator Start()
            {
                yield return ResourceSessionTests.Start(Startup);
                System = Startup.Resources;
                Addressables.ResourceManager.ResourceProviders.Add(Provider);
                Addressables.AddResourceLocator(Locator);
            }
            internal void ReleaseExternal()
            {
                if (!External.IsValid())
                {
                    return;
                }
                Addressables.Release(External);
                External = default;
            }
            internal void Remove()
            {
                try
                {
                    ReleaseExternal();
                    Addressables.RemoveResourceLocator(Locator);
                    Addressables.ResourceManager.ResourceProviders.Remove(Provider);
                    Assert.That(Provider.SignalProvides, Is.EqualTo(Provider.SignalReleases), "All real successful official signal operations must be returned exactly once.");
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(Clip);
                    UnityEngine.Object.DestroyImmediate(Text);
                }
            }
        }

        private sealed class PendingProvide
        {
            private readonly ProvideHandle _handle;
            private readonly AudioClip _clip;
            private bool _finished;
            internal float Value = .2f;
            internal PendingProvide(ProvideHandle handle, AudioClip clip)
            {
                _handle = handle;
                _clip = clip;
                handle.SetProgressCallback(() => Value);
            }
            internal void Finish()
            {
                if (_finished)
                {
                    return;
                }
                _finished = true;
                _handle.Complete(_clip, true, null);
            }
        }
        private sealed class ControlledProvider : ResourceProviderBase
        {
            private readonly AudioClip _clip;
            private readonly TextAsset _text;
            internal int Provides;
            internal int SignalProvides;
            internal int SignalReleases;
            internal PendingProvide Pending;
            internal ControlledProvider(string id, AudioClip clip, TextAsset text)
            {
                m_ProviderId = id;
                _clip = clip;
                _text = text;
            }
            public override Type GetDefaultType(IResourceLocation location) => location.ResourceType;
            public override void Provide(ProvideHandle handle)
            {
                Provides++;
                switch (handle.Location.InternalId)
                {
                    case "signal":
                        SignalProvides++;
                        Pending = new PendingProvide(handle, _clip);
                        break;
                    case "wrong-type":
                        handle.Complete(_text, true, null);
                        break;
                    case "failure":
                        handle.Complete<AudioClip>(null, false, new InvalidOperationException("official-audio-independent-failure"));
                        break;
                    default:
                        throw new InvalidOperationException("Unknown owned official location.");
                }
            }
            public override void Release(IResourceLocation location, object asset)
            {
                if (location.InternalId == "signal")
                {
                    SignalReleases++;
                }
            }
        }
    }
}
