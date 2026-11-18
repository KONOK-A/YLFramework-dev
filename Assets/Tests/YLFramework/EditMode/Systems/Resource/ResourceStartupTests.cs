using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.AddressableAssets.ResourceLocators;
using UnityEngine.ResourceManagement.ResourceLocations;
using UnityEngine.ResourceManagement.ResourceProviders;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using YLFramework.Core;
using YLFramework.Resource;

namespace YLFramework.Tests.Resource
{
    public class ResourceStartupTests
    {
        private readonly ResourceSessionTests _scene = new();
        // Runtime ownership is established by a fresh case-body iterator after EnterPlayMode has resumed.
        private ResourceStartup _owner;
        private AssetLease<TextAsset> _held;
        private ResourceLocationMap _locator;
        private ToneProvider _provider;
        private AudioClip _clip;
        private ForeignCore _foreign;
        [SetUp] public void SetUp() => _scene.SetUp();

        private sealed class ForeignCore : ICoreResourceProvider
        {
            public CoreResourceRequest<string> ReadTextAsync(string projectId, string address) => throw new NotSupportedException();
            public CorePrefabLease AcquirePrefab(string projectId, string address) => throw new NotSupportedException();
            public CoreResourceRequest<CorePrefabLease> AcquirePrefabAsync(string projectId, string address) => throw new NotSupportedException();
            public CoreResourceRequest<CoreSceneResult> LoadSceneAsync(string projectId, string address, LoadSceneMode mode) => throw new NotSupportedException();
        }

        [UnityTest]
        public IEnumerator StartupWaitersCancelIndependentlyAndAnotherStartupCannotJoinTheOwner()
        {
            yield return new EnterPlayMode();
            yield return StartupWaitersCase();
            yield return new ExitPlayMode();
        }

        // UTF restores the outer iterator PC, not its display-class locals. Do not capture across EnterPlayMode.
        private IEnumerator StartupWaitersCase()
        {
            Assert.That(Application.isPlaying, Is.True);
            Assert.That(ResourceSystem.Current, Is.Null);
            _owner = new ResourceStartup(new ResourceConfiguration());
            using var first = _owner.StartAsync();
            using var canceled = _owner.StartAsync();
            using var follower = _owner.StartAsync();
            Assert.That(first.Status, Is.EqualTo(ResourceRequestStatus.Pending));
            int cancellations = 0;
            canceled.Completed += _ => cancellations++;
            canceled.Cancel();
            Assert.That(canceled.Status, Is.EqualTo(ResourceRequestStatus.Canceled));
            Assert.That(canceled.Error.Kind, Is.EqualTo(ResourceError.Canceled));
            yield return ResourceSessionTests.Wait(() => first.IsDone && follower.IsDone, "independent startup observers");
            Assert.That(first.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), first.Error?.ToString());
            Assert.That(follower.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), follower.Error?.ToString());
            Assert.That(first.TakeResult(), Is.SameAs(_owner));
            Assert.That(follower.TakeResult(), Is.SameAs(_owner));
            Assert.That(cancellations, Is.EqualTo(1));
            Assert.That(Assert.Throws<ResourceException>(() => canceled.TakeResult()).Kind, Is.EqualTo(ResourceError.Canceled));
            var resources = _owner.Resources;
            using var repeated = _owner.StartAsync();
            Assert.That(repeated.TakeResult().Resources, Is.SameAs(resources));
            using var other = new ResourceStartup(new ResourceConfiguration());
            using var conflict = other.StartAsync();
            Assert.That(conflict.Status, Is.EqualTo(ResourceRequestStatus.Failed));
            Assert.That(conflict.Error.Kind, Is.EqualTo(ResourceError.ConfigurationConflict));
            Assert.That(other.Resources, Is.Null);
            Assert.That(other.OwnsResourceSystem, Is.False);
            yield return ResourceSessionTests.Stop(other);
            Assert.That(resources.IsAvailable, Is.True);
            Assert.That(CoreResourceAccess.Provider, Is.SameAs(_owner.CoreProvider));
            yield return ResourceSessionTests.Stop(_owner);
        }

        [UnityTest]
        public IEnumerator BusyStopFreezesAdmissionButKeepsIndependentOwnershipUntilReleaseAndRetry()
        {
            yield return new EnterPlayMode();
            yield return BusyStopCase();
            yield return new ExitPlayMode();
        }

        private IEnumerator BusyStopCase()
        {
            Assert.That(Application.isPlaying, Is.True);
            _owner = new ResourceStartup(new ResourceConfiguration());
            yield return ResourceSessionTests.Start(_owner);
            var resources = _owner.Resources;
            using var acquired = resources.AcquireAsync<TextAsset>(ResourceSessionTests.MessageKey);
            yield return ResourceSessionTests.Wait(() => acquired.IsDone, "independent asset before Busy");
            Assert.That(acquired.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), acquired.Error?.ToString());
            _held = acquired.TakeResult();
            const string retainedOwner = "Busy snapshot retained consumer";
            int originalId = resources.GetDiagnostics().Leases.Single(lease => lease.Key == ResourceSessionTests.MessageKey).Id;
            using var retained = _held.Retain(retainedOwner);
            int retainedId = resources.GetDiagnostics().Leases.Single(lease => lease.Owner == retainedOwner).Id;
            using var pending = resources.AcquireAsync<TextAsset>(ResourceSessionTests.MessageKey);
            Assert.That(pending.Status, Is.EqualTo(ResourceRequestStatus.Pending));
            ResourceException originalBusy;
            using (var busy = _owner.StopAsync())
            {
                Assert.That(busy.Status, Is.EqualTo(ResourceRequestStatus.Failed));
                originalBusy = busy.Error;
                Assert.That(originalBusy.Kind, Is.EqualTo(ResourceError.Busy));
                Assert.That(originalBusy.Message, Does.Contain($"#{originalId} ").And.Contain($"#{retainedId} ")
                    .And.Contain(ResourceSessionTests.MessageKey).And.Contain(retainedOwner));
            }
            Assert.That(resources.IsAvailable, Is.False, "Busy does not reopen admission.");
            Assert.That(resources.IsSessionActive, Is.True);
            Assert.That(resources.State, Is.EqualTo(ResourceSystemState.ShuttingDown));
            Assert.That(ResourceSystem.Current, Is.SameAs(resources));
            Assert.That(CoreResourceAccess.Provider, Is.SameAs(_owner.CoreProvider));
            Assert.That(_held.IsValid, Is.True);
            Assert.That(_held.Asset.text, Is.EqualTo(ResourceSessionTests.MessageText));
            Assert.That(pending.Status, Is.EqualTo(ResourceRequestStatus.Invalidated));
            Assert.That(pending.IsDisposed, Is.True);
            Assert.Throws<ObjectDisposedException>(() => pending.TakeResult());
            retained.Dispose();
            using (var retry = _owner.StopAsync())
            {
                Assert.That(retry.Status, Is.EqualTo(ResourceRequestStatus.Failed));
                Assert.That(retry.Error.Kind, Is.EqualTo(ResourceError.Busy));
                Assert.That(retry.Error.Message, Does.Contain($"#{originalId} ").And.Not.Contain($"#{retainedId} ").And.Not.Contain(retainedOwner));
                Assert.That(originalBusy.Message, Does.Contain($"#{retainedId} ").And.Contain(retainedOwner));
            }
            using var rejected = resources.AcquireAsync<TextAsset>(ResourceSessionTests.MessageKey);
            Assert.That(rejected.Status, Is.EqualTo(ResourceRequestStatus.Failed));
            Assert.That(rejected.Error.Kind, Is.EqualTo(ResourceError.SessionUnavailable));
            Assert.That(Assert.Throws<ResourceException>(() => resources.TryAcquireLoaded<TextAsset>(ResourceSessionTests.MessageKey, out _)).Kind,
                Is.EqualTo(ResourceError.SessionUnavailable));
            _held.Dispose();
            _held = null;
            yield return ResourceSessionTests.Stop(_owner);
            Assert.That(CoreResourceAccess.Provider, Is.Null);
        }

        [UnityTest]
        public IEnumerator FailedOptionalAssemblyCannotPretendSuccessOnRetryOrLoseItsCleanupResponsibility()
        {
            yield return new EnterPlayMode();
            yield return FailedOptionalAssemblyCase();
            yield return new ExitPlayMode();
        }

        private IEnumerator FailedOptionalAssemblyCase()
        {
            Assert.That(Application.isPlaying, Is.True);
            _owner = new ResourceStartup(new ResourceConfiguration(), bindCoreResourceProvider: false);
            yield return ResourceSessionTests.Start(_owner);
            yield return ResourceSessionTests.Stop(_owner); // Prove official runtime data exists before testing invalid input.
            _foreign = new ForeignCore();
            CoreResourceAccess.Bind(_foreign);
            _owner = new ResourceStartup(new ResourceConfiguration(), audioProjects: new[] { "invalid/project" });
            using var failed = _owner.StartAsync();
            yield return ResourceSessionTests.Wait(() => failed.IsDone, "invalid optional namespace");
            Assert.That(failed.Status, Is.EqualTo(ResourceRequestStatus.Failed));
            Assert.That(failed.Error, Is.Not.Null);
            Assert.That(failed.Error.Kind == ResourceError.InvalidAddress || failed.Error.Kind == ResourceError.InvalidConfiguration ||
                failed.Error.InnerException is ArgumentException, Is.True, failed.Error.ToString());
            Assert.That(_owner.AudioProviders, Is.Empty, "The invalid optional consumer has not been assembled.");
            Assert.That(CoreResourceAccess.Provider, Is.SameAs(_foreign), "Failed optional assembly cannot publish a replacement binding.");
            var created = _owner.Resources;
            bool owns = _owner.OwnsResourceSystem;
            Assert.That(owns, Is.EqualTo(created != null));
            Assert.That(ResourceSystem.Current, Is.SameAs(created));
            using var retried = _owner.StartAsync();
            yield return ResourceSessionTests.Wait(() => retried.IsDone, "invalid optional namespace retry");
            Assert.That(retried.Status, Is.EqualTo(ResourceRequestStatus.Failed), "A ready service is not proof that consumer assembly succeeded.");
            Assert.That(retried.Error.Kind, Is.EqualTo(failed.Error.Kind));
            Assert.That(_owner.AudioProviders, Is.Empty);
            Assert.That(CoreResourceAccess.Provider, Is.SameAs(_foreign));
            Assert.That(_owner.Resources, Is.SameAs(created));
            Assert.That(_owner.OwnsResourceSystem, Is.EqualTo(owns));
            yield return ResourceSessionTests.Stop(_owner);
            Assert.That(CoreResourceAccess.Provider, Is.SameAs(_foreign));
            Assert.That(ResourceSystem.Current, Is.Null);
            CoreResourceAccess.Unbind(_foreign);
            _foreign = null;
        }

        [UnityTest]
        public IEnumerator StaleStartupAndProviderCannotUnbindOrTakeOwnershipOfAnExplicitReplacement()
        {
            yield return new EnterPlayMode();
            yield return StaleStartupCase();
            yield return new ExitPlayMode();
        }

        private IEnumerator StaleStartupCase()
        {
            Assert.That(Application.isPlaying, Is.True);
            _owner = new ResourceStartup(new ResourceConfiguration());
            yield return ResourceSessionTests.Start(_owner);
            var oldStartup = _owner;
            var oldProvider = _owner.CoreProvider;
            _foreign = new ForeignCore();
            CoreResourceAccess.Bind(_foreign);
            yield return ResourceSessionTests.Stop(oldStartup);
            Assert.That(CoreResourceAccess.Provider, Is.SameAs(_foreign));
            _owner = new ResourceStartup(new ResourceConfiguration(), bindCoreResourceProvider: false);
            yield return ResourceSessionTests.Start(_owner);
            var replacement = _owner.Resources;
            using var rejected = oldStartup.StartAsync();
            Assert.That(rejected.Status, Is.EqualTo(ResourceRequestStatus.Failed));
            Assert.That(rejected.Error.Kind, Is.EqualTo(ResourceError.ConfigurationConflict));
            Assert.That(oldStartup.Resources, Is.Null);
            Assert.That(oldStartup.OwnsResourceSystem, Is.False);
            using var staleRead = oldProvider.ReadTextAsync("ownership", "message");
            Assert.That(staleRead.Status, Is.EqualTo(CoreResourceStatus.Failed));
            Assert.That(staleRead.Error.Kind, Is.EqualTo(CoreResourceError.Unavailable));
            yield return ResourceSessionTests.Stop(oldStartup);
            oldStartup.Dispose();
            Assert.That(replacement.IsAvailable, Is.True);
            Assert.That(ResourceSystem.Current, Is.SameAs(replacement));
            Assert.That(CoreResourceAccess.Provider, Is.SameAs(_foreign));
            Assert.That(CoreResourceAccess.Unbind(oldProvider), Is.False);
            yield return ResourceSessionTests.Stop(_owner);
            Assert.That(CoreResourceAccess.Provider, Is.SameAs(_foreign));
            CoreResourceAccess.Unbind(_foreign);
            _foreign = null;
        }

        [UnityTest]
        public IEnumerator StopBeforeTheFirstStartupTickRejectsLateCompositionAndTheSameOwnerCanRetry()
        {
            yield return new EnterPlayMode();
            yield return StopBeforeFirstTickCase();
            yield return new ExitPlayMode();
        }

        private IEnumerator StopBeforeFirstTickCase()
        {
            Assert.That(Application.isPlaying, Is.True);
            _owner = new ResourceStartup(new ResourceConfiguration(), audioProjects: new[] { "startup-c05" });
            using var starting = _owner.StartAsync();
            Assert.That(starting.Status, Is.EqualTo(ResourceRequestStatus.Pending));
            using var stopping = _owner.StopAsync();
            yield return ResourceSessionTests.Wait(() => starting.IsDone && stopping.IsDone, "stop during initialization");
            Assert.That(starting.Status, Is.EqualTo(ResourceRequestStatus.Failed));
            Assert.That(starting.Error.Kind, Is.EqualTo(ResourceError.Canceled));
            Assert.That(stopping.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), stopping.Error?.ToString());
            Assert.That(stopping.TakeResult(), Is.True);
            Assert.That(_owner.Resources, Is.Null);
            Assert.That(_owner.CoreProvider, Is.Null);
            Assert.That(_owner.AudioProviders, Is.Empty);
            Assert.That(ResourceSystem.Current, Is.Null);
            Assert.That(CoreResourceAccess.Provider, Is.Null);
            yield return ResourceSessionTests.Start(_owner);
            Assert.That(CoreResourceAccess.Provider, Is.SameAs(_owner.CoreProvider));
            yield return ResourceSessionTests.Stop(_owner);
        }

        [UnityTest]
        public IEnumerator AudioNamespacesCopyTheirOptionsWithoutCatalogsAndOwnIndependentClipLeases()
        {
            yield return new EnterPlayMode();
            yield return AudioNamespacesCase();
            yield return new ExitPlayMode();
        }

        private IEnumerator AudioNamespacesCase()
        {
            Assert.That(Application.isPlaying, Is.True);
            var projects = new[] { "startup-c05", "startup-c05" };
            _owner = new ResourceStartup(new ResourceConfiguration(), audioProjects: projects);
            projects[0] = projects[1] = "invalid/project";
            yield return ResourceSessionTests.Start(_owner);
            Assert.That(_owner.AudioProviders.Keys, Is.EquivalentTo(new[] { "startup-c05" }));
            var resources = _owner.Resources;
            var audio = _owner.AudioProviders["startup-c05"];
            Assert.That(resources.GetAudioClipProvider("startup-c05"), Is.SameAs(audio));
            RegisterTone();
            Assert.That(Assert.Throws<ResourceException>(() => audio.Load("tone")).Kind, Is.EqualTo(ResourceError.InProgress));
            Assert.That(_provider.Provides, Is.Zero, "A cold synchronous miss does not start a producer.");
            using (var loading = resources.AcquireAsync<AudioClip>("startup-c05/tone"))
            {
                yield return ResourceSessionTests.Wait(() => loading.IsDone, "official tone operation");
                Assert.That(loading.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), loading.Error?.ToString());
                using var independent = loading.TakeResult();
                using (var clip = audio.Load("tone"))
                {
                    Assert.That(clip.Clip, Is.SameAs(_clip));
                    Assert.That(resources.GetDiagnostics().LeaseCount, Is.EqualTo(2));
                }
                Assert.That(independent.IsValid, Is.True);
                using var text = _owner.CoreProvider.ReadTextAsync("ownership", "message");
                yield return ResourceSessionTests.Wait(() => text.Status != CoreResourceStatus.Pending, "composed Core text");
                Assert.That(text.Status, Is.EqualTo(CoreResourceStatus.Succeeded), text.Error?.ToString());
                Assert.That(text.TakeResult(), Is.EqualTo(ResourceSessionTests.MessageText));
                Assert.That(resources.GetDiagnostics().LeaseCount, Is.EqualTo(1), "Core's temporary text lease was returned before delivery.");
            }
            yield return ResourceSessionTests.Wait(() => _provider.Releases == 1, "last independent tone owner");
            yield return ResourceSessionTests.Stop(_owner);
            RemoveTone();
        }

        private void RegisterTone()
        {
            _clip = AudioClip.Create("StartupOwnTone", 256, 1, 8000, false);
            _provider = new ToneProvider("Startup.Tone." + Guid.NewGuid().ToString("N"));
            _locator = new ResourceLocationMap(_provider.ProviderId);
            _locator.Add("startup-c05/tone", new ResourceLocationBase("startup-c05/tone", "startup-c05/tone", _provider.ProviderId, typeof(AudioClip))
            {
                Data = _clip
            });
            Addressables.ResourceManager.ResourceProviders.Add(_provider);
            Addressables.AddResourceLocator(_locator);
        }

        private void RemoveTone()
        {
            int provides = _provider?.Provides ?? 0;
            int releases = _provider?.Releases ?? 0;
            if (_locator != null)
                Addressables.RemoveResourceLocator(_locator);
            if (_provider != null)
                Addressables.ResourceManager.ResourceProviders.Remove(_provider);
            if (_clip)
                UnityEngine.Object.DestroyImmediate(_clip);
            _locator = null;
            _provider = null;
            _clip = null;
            Assert.That(provides, Is.EqualTo(releases), "Only count this provider's real completed/released operation.");
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            var errors = new List<Exception>();
            // Yield each leaf directly from this root iterator: UTF rejects nested ExitPlayMode instructions.
            foreach (var cleanup in new[] { CleanupRuntime(), _scene.TearDown() })
            {
                var stack = new Stack<IEnumerator>();
                stack.Push(cleanup);
                try
                {
                    while (stack.Count != 0)
                    {
                        var current = stack.Peek();
                        bool advanced;
                        try
                        {
                            advanced = current.MoveNext();
                        }
                        catch (Exception error) { errors.Add(error); break; }
                        if (!advanced)
                        {
                            stack.Pop();
                            try
                            {
                                (current as IDisposable)?.Dispose();
                            }
                            catch (Exception error) { errors.Add(error); }
                            continue;
                        }
                        if (current.Current is IEnumerator nested)
                            stack.Push(nested);
                        else
                            yield return current.Current;
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
                        catch (Exception error) { errors.Add(error); }
                    }
                }
            }
            if (errors.Count == 1)
                ExceptionDispatchInfo.Capture(errors[0]).Throw();
            if (errors.Count > 1)
                throw new AggregateException(errors);
        }

        private IEnumerator CleanupRuntime()
        {
            try
            {
                _held?.Dispose();
                _held = null;
                if (_owner?.Resources != null && Application.isPlaying)
                    yield return ResourceSessionTests.Stop(_owner);
            }
            finally
            {
                try
                {
                    if (_foreign != null)
                        CoreResourceAccess.Unbind(_foreign);
                    _foreign = null;
                }
                finally { RemoveTone(); }
            }
        }

        private sealed class ToneProvider : ResourceProviderBase
        {
            internal int Provides;
            internal int Releases;
            internal ToneProvider(string id) => m_ProviderId = id;
            public override void Provide(ProvideHandle handle)
            {
                Provides++;
                handle.Complete((AudioClip)handle.Location.Data, true, null);
            }
            public override void Release(IResourceLocation location, object asset) => Releases++;
        }
    }
}
