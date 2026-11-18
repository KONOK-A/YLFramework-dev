using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.AddressableAssets.ResourceLocators;
using UnityEngine.ResourceManagement.ResourceLocations;
using UnityEngine.ResourceManagement.ResourceProviders;
using UnityEngine.TestTools;
using YLFramework.Integrations.HybridCLR;
using YLFramework.Resource;

namespace YLFramework.Tests.Integrations.HybridCLR
{
    public class HybridCLRLoaderTests
    {
        internal ResourceSystem ResourcesSystem;
        internal HybridCLRLoader Loader;
        internal HybridCLRProcessState Process;
        internal HybridCLRRuntimeCalls Calls;
        internal ResourceStartup Startup;
        private ResourceLocationMap _locator;
        private FixtureTextProvider _provider;
        protected int MetadataCalls;
        protected int AssemblyCalls;
        internal static ResourceAddress Address => new ResourceAddress("hybridclr-test", HybridCLRManifest.ManifestPath);
        internal const string ManifestKey = "hybridclr-test/hybridclr/manifest.json";
        private static string EntryKey => "hybridclr-test/" + HybridCLRManifest.PayloadAddress(typeof(HybridCLRLoaderTests).Assembly.GetName().Name, false);
        internal int Provides => _provider?.Provides ?? 0;
        internal int PendingProvides => _provider?.PendingCount ?? 0;
        internal bool HoldResources
        {
            set => _provider.Hold = value;
        }

        internal IEnumerator Initialize(string variant = "Good", bool nativeSubstitute = false, HybridCLRProcessState shared = null,
            bool includeCodeResources = true, bool bindCoreResourceProvider = false)
        {
            Assert.That(ResourceSystem.Current, Is.Null, "Do not take over another Resource authority.");
            Startup = new ResourceStartup(new ResourceConfiguration(), bindCoreResourceProvider);
            using var init = Startup.StartAsync();
            yield return Wait(() => init.IsDone, "official Addressables startup");
            Assert.That(init.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), init.Error?.ToString());
            ResourcesSystem = init.TakeResult().Resources;
            Assert.That(ResourceSystem.Current, Is.SameAs(ResourcesSystem));
            Assert.That(ResourcesSystem.IsAvailable && ResourcesSystem.IsSessionActive, Is.True);

            var id = "hybridclr-test-" + Guid.NewGuid().ToString("N");
            _locator = new ResourceLocationMap(id);
            _provider = new FixtureTextProvider(id);
            Addressables.ResourceManager.ResourceProviders.Add(_provider);
            Addressables.AddResourceLocator(_locator);
            TextAsset entry = null;
            TextAsset metadata = null;
            if (includeCodeResources)
            {
                var manifestAsset = PreparedText(variant);
                var manifest = HybridCLRManifest.Parse(manifestAsset.text);
                Assert.That(manifest.SchemaVersion, Is.EqualTo(2));
                var assembly = typeof(HybridCLRLoaderTests).Assembly;
                var original = PreparedText("Entry");
                Assert.That(HybridCLRMetadata.ReadMvid(original.bytes), Is.EqualTo(assembly.ManifestModule.ModuleVersionId),
                    "The fixture must contain the current real test DLL, not synthetic bytes.");
                entry = variant == "Conflict" ? PreparedText("ConflictEntry") : original;
                metadata = PreparedText("MetadataPayload");
                Assert.That(HybridCLRMetadata.ReadMvid(metadata.bytes), Is.EqualTo(typeof(HybridCLRLoader).Assembly.ManifestModule.ModuleVersionId));
                var descriptor = manifest.HotUpdateAssemblies.Single();
                Assert.That(descriptor.Name, Is.EqualTo(variant == "External" ? "Missing.External" : assembly.GetName().Name));
                Assert.That(descriptor.Identity, Is.EqualTo(variant == "External"
                    ? "Missing.External, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null" : assembly.GetName().FullName));
                Assert.That(descriptor.Length, Is.EqualTo(entry.bytes.LongLength));
                Assert.That(descriptor.Mvid, Is.EqualTo(variant == "BadMvid"
                    ? "74a011fb-8799-4f16-9d21-8fe27d01771d" : assembly.ManifestModule.ModuleVersionId.ToString("D")));
                Assert.That(descriptor.Sha256, Is.EqualTo(variant == "BadHash" ? new string('a', 64) : HybridCLRManifest.Hash(entry.bytes)));
                if (variant == "Conflict")
                {
                    Assert.That(entry.bytes.Length, Is.EqualTo(original.bytes.Length + 1));
                    CollectionAssert.AreEqual(original.bytes, entry.bytes.Take(original.bytes.Length));
                    Assert.That(entry.bytes.Last(), Is.EqualTo(1));
                }
                Add(ManifestKey, manifestAsset);
                if (variant != "Missing")
                {
                    Add("hybridclr-test/" + descriptor.Address, entry);
                }
                foreach (var payload in manifest.AotMetadata)
                {
                    Assert.That(payload.Identity, Is.EqualTo(typeof(HybridCLRLoader).Assembly.GetName().FullName));
                    Assert.That(payload.Mvid, Is.EqualTo(typeof(HybridCLRLoader).Assembly.ManifestModule.ModuleVersionId.ToString("D")));
                    Assert.That(payload.Length, Is.EqualTo(metadata.bytes.LongLength));
                    Assert.That(payload.Sha256, Is.EqualTo(HybridCLRManifest.Hash(metadata.bytes)));
                    Add("hybridclr-test/" + payload.Address, metadata);
                }
            }
            else
            {
                // A tripwire has no code asset; even a swallowed manifest read must fail EditorFast tests.
                _provider.ForbidLoads = true;
                Add(ManifestKey, null);
            }
            Process = shared ?? new HybridCLRProcessState();
            MetadataCalls = 0;
            AssemblyCalls = 0;
            Calls = new HybridCLRRuntimeCalls
            {
                IsSimulation = !nativeSubstitute,
                LoadMetadata = bytes =>
                {
                    MetadataCalls++;
                    CollectionAssert.AreEqual(metadata.bytes, bytes);
                    return 0;
                },
                LoadAssembly = bytes =>
                {
                    AssemblyCalls++;
                    CollectionAssert.AreEqual(entry.bytes, bytes);
                    return typeof(HybridCLRLoaderTests).Assembly;
                },
                LoadedAssemblies = () => nativeSubstitute ? Array.Empty<Assembly>() : AppDomain.CurrentDomain.GetAssemblies()
            };
            Loader = new HybridCLRLoader(ResourcesSystem, "base-test", Calls, Process);
        }

        private void Add(string key, TextAsset asset) => _locator.Add(key,
            new ResourceLocationBase(key, key, _provider.ProviderId, typeof(TextAsset)) { Data = asset });

        private static TextAsset PreparedText(string name)
        {
            var asset = UnityEngine.Resources.Load<TextAsset>("YLHybridCLRTests/" + name);
            Assert.That(asset, Is.Not.Null, "The owned host must run HybridCLRTestFixtures.Prepare before PlayMode: " + name);
            Assert.That(asset.bytes, Is.Not.Empty);
            return asset;
        }

        internal void FinishPendingResources() => _provider?.FinishPending();

        internal static IEnumerator Wait(Func<bool> completed, string operation)
        {
            var deadline = Time.realtimeSinceStartupAsDouble + 15;
            while (!completed() && Time.realtimeSinceStartupAsDouble < deadline)
            {
                yield return null;
            }
            Assert.That(completed(), Is.True, operation + " timed out.");
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            FinishPendingResources();
            yield return Wait(() => Process?.ActiveJob == null, "HybridCLR fixture code drain");
            Loader?.Dispose();
            Loader = null;
            if (Startup != null)
            {
                using var shutdown = Startup.StopAsync();
                yield return Wait(() => shutdown.IsDone, "HybridCLR fixture resource stop");
                Assert.That(shutdown.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), shutdown.Error?.ToString());
            }
            if (ResourcesSystem != null)
            {
                var ledger = ResourcesSystem.GetDiagnostics();
                Assert.That(ledger.State, Is.EqualTo(ResourceSystemState.Closed));
                Assert.That(ledger.LeaseCount, Is.Zero);
                Assert.That(ledger.RequestCount, Is.Zero);
                Assert.That(ledger.PendingWorkCount, Is.Zero);
                Assert.That(ledger.DrainingHandleCount, Is.Zero);
                Assert.That(ledger.Errors, Is.Empty);
            }
            ResourcesSystem = null;
            Startup = null;
            if (_locator != null)
            {
                Addressables.RemoveResourceLocator(_locator);
                _locator = null;
            }
            if (_provider != null)
            {
                Assert.That(_provider.PendingCount, Is.Zero);
                Assert.That(_provider.Releases, Is.EqualTo(_provider.Provides), "Every provided official operation must return its reference.");
                Addressables.ResourceManager.ResourceProviders.Remove(_provider);
                _provider = null;
            }
        }

        [UnityTest]
        public IEnumerator SimulationReusesLoadedAssembliesAndRetainsOnlyOneManifestLease()
        {
            yield return Initialize();
            HoldResources = true;
            var first = Loader.LoadAsync(Address);
            yield return Wait(() => PendingProvides == 1, "pending manifest producer");
            Assert.That(first.IsDone, Is.False, "This regression needs a real pending resource load.");
            var waiter = first.AsTask();
            Assert.That(waiter.IsCompleted, Is.False);
            FinishPendingResources();
            yield return Wait(() => first.IsDone, "simulation code load");
            Assert.That(waiter.Status, Is.EqualTo(TaskStatus.RanToCompletion));
            Assert.That(waiter.Result, Is.SameAs(first));
            Assert.That(first.Exception, Is.Null, first.Error);
            Assert.That(first.Result.IsSimulation, Is.True);
            Assert.That(first.Result.Assemblies.Values.Single(), Is.SameAs(typeof(HybridCLRLoaderTests).Assembly));
            Assert.That(MetadataCalls + AssemblyCalls, Is.Zero);
            AssertManifestLease();
            var repeat = Loader.LoadAsync(Address);
            yield return repeat;
            Assert.That(repeat.Result, Is.SameAs(first.Result));
            AssertManifestLease();
            Loader.Dispose();
            Assert.That(ResourcesSystem.IsAvailable, Is.True);
            Assert.That(ResourcesSystem.GetDiagnostics().LeaseCount, Is.Zero);
        }

        [UnityTest]
        public IEnumerator BadHashFailsBeforeAnyIrreversibleCall()
        {
            yield return Initialize("BadHash", true);
            var request = Loader.LoadAsync(Address);
            yield return request;
            Assert.That(request.Exception, Is.TypeOf<InvalidDataException>());
            Assert.That(request.Error, Does.Contain("SHA256 mismatch"));
            Assert.That(request.RequiresRestart, Is.False);
            Assert.That(MetadataCalls + AssemblyCalls, Is.Zero);
            Assert.That(ResourcesSystem.GetDiagnostics().LeaseCount, Is.Zero);
        }

        [UnityTest]
        public IEnumerator ACorrectHashWithAnIncorrectPeMvidFailsBeforeNativeLoading()
        {
            yield return Initialize("BadMvid", true);
            var request = Loader.LoadAsync(Address);
            yield return request;
            Assert.That(request.Exception, Is.TypeOf<InvalidDataException>());
            Assert.That(request.Error, Does.Contain("MVID"));
            Assert.That(request.RequiresRestart, Is.False);
            Assert.That(MetadataCalls + AssemblyCalls, Is.Zero);
        }

        [UnityTest]
        public IEnumerator MissingDllIsATerminalResourceFailure()
        {
            yield return Initialize("Missing", true);
            var request = Loader.LoadAsync(Address);
            yield return request;
            Assert.That(request.Exception, Is.TypeOf<ResourceException>());
            Assert.That(((ResourceException)request.Exception).Kind, Is.EqualTo(ResourceError.AddressNotFound));
            Assert.That(request.IsDone, Is.True);
            Assert.That(request.RequiresRestart, Is.False);
            Assert.That(MetadataCalls + AssemblyCalls, Is.Zero);
        }

        [UnityTest]
        public IEnumerator InFlightRequestsJoinAndAbortDoesNotModifyTheirState()
        {
            yield return Initialize();
            HoldResources = true;
            var request = Loader.LoadAsync(Address);
            yield return Wait(() => PendingProvides == 1, "joined manifest producer");
            var repeat = Loader.LoadAsync(Address);
            Assert.That(request.IsDone, Is.False);
            Assert.That(repeat, Is.SameAs(request));

            var done = request.IsDone;
            Assert.Throws<NotSupportedException>(() => request.Abort());
            Assert.That(request.IsDone, Is.EqualTo(done));
            FinishPendingResources();
            yield return request;
            Assert.That(request.Exception, Is.Null, request.Error);
        }

        [UnityTest]
        public IEnumerator UnknownExternalDllCannotBeAdvertisedAsEditorSimulationSuccess()
        {
            yield return Initialize("External");
            var request = Loader.LoadAsync(Address);
            yield return request;
            Assert.That(request.Exception, Is.TypeOf<NotSupportedException>());
            Assert.That(request.RequiresRestart, Is.False);
            Assert.That(AssemblyCalls, Is.Zero);
        }

        [UnityTest]
        public IEnumerator MetadataFailureRetainsNativeErrorAndRequiresRestartForEveryLaterLoader()
        {
            yield return Initialize("Metadata", true);
            Calls.LoadMetadata = bytes =>
            {
                MetadataCalls++;
                return 42;
            };
            var request = Loader.LoadAsync(Address);
            yield return request;
            Assert.That(request.Exception, Is.Not.Null);
            Assert.That(request.NativeErrorCode, Is.EqualTo(42));
            Assert.That(request.RequiresRestart, Is.True);
            Assert.That(AssemblyCalls, Is.Zero);
            Loader.Dispose();
            Loader = new HybridCLRLoader(ResourcesSystem, "base-test", Calls, Process);
            var retry = Loader.LoadAsync(Address);
            yield return retry;
            Assert.That(retry.RequiresRestart, Is.True);
            Assert.That(MetadataCalls, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator NativeSuccessIsIdempotentAcrossLoaderInstances()
        {
            yield return Initialize("Metadata", true);
            var request = Loader.LoadAsync(Address);
            yield return request;
            Assert.That(request.Exception, Is.Null, request.Error);
            Assert.That(MetadataCalls, Is.EqualTo(1));
            Assert.That(AssemblyCalls, Is.EqualTo(1));
            Loader.Dispose();
            Assert.That(Process.Assemblies.Count, Is.EqualTo(2));
            Assert.That(Process.Projects.Count, Is.EqualTo(1));
            Assert.That(ResourcesSystem.GetDiagnostics().LeaseCount, Is.Zero);
            Loader = new HybridCLRLoader(ResourcesSystem, "base-test", Calls, Process);
            var repeat = Loader.LoadAsync(Address);
            yield return repeat;
            Assert.That(repeat.Exception, Is.Null, repeat.Error);
            Assert.That(MetadataCalls, Is.EqualTo(1));
            Assert.That(AssemblyCalls, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator PreloadedResourcesJoinWithoutHangingOrDoubleLoading()
        {
            yield return Initialize("Good", true);
            using var manifestRequest = ResourcesSystem.AcquireAsync<TextAsset>(ManifestKey);
            using var entryRequest = ResourcesSystem.AcquireAsync<TextAsset>(EntryKey);
            yield return Wait(() => manifestRequest.IsDone && entryRequest.IsDone, "explicit code preload");
            Assert.That(manifestRequest.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), manifestRequest.Error?.ToString());
            Assert.That(entryRequest.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), entryRequest.Error?.ToString());
            using var manifest = manifestRequest.TakeResult();
            using var entry = entryRequest.TakeResult();
            using var retained = manifest.Retain("HybridCLR preload");
            Assert.That(retained.Key, Is.EqualTo(ManifestKey));
            manifest.Dispose();
            var provides = Provides;
            var request = Loader.LoadAsync(Address);
            var joined = Loader.LoadAsync(Address);
            Assert.That(joined, Is.SameAs(request));
            yield return Wait(() => request.IsDone, "preloaded code load");
            Assert.That(request.Exception, Is.Null, request.Error);
            Assert.That(AssemblyCalls, Is.EqualTo(1));
            var repeat = Loader.LoadAsync(Address);
            Assert.That(repeat.IsDone, Is.True);
            Assert.That(repeat.Result, Is.SameAs(request.Result));
            Assert.That(AssemblyCalls, Is.EqualTo(1));
            Assert.That(Provides, Is.EqualTo(provides), "Preloading must share the official producer, not start a second source.");
            retained.Dispose();
            entry.Dispose();
            AssertManifestLease();
        }

        [UnityTest]
        public IEnumerator OtherInstancesCannotCompeteForTheSameNativeLoad()
        {
            yield return Initialize("Metadata", true);
            HoldResources = true;
            using var other = new HybridCLRLoader(ResourcesSystem, "base-test", Calls, Process);
            var first = Loader.LoadAsync(Address);
            yield return Wait(() => PendingProvides == 1, "exclusive manifest producer");
            var second = other.LoadAsync(Address);
            Assert.That(first.IsDone, Is.False);
            Assert.That(second.IsDone, Is.True);
            Assert.That(second.Stage, Is.EqualTo("Busy"));
            Assert.That(second.Exception, Is.Not.Null);
            FinishPendingResources();
            yield return first;
            var retry = other.LoadAsync(Address);
            yield return retry;
            Assert.That(retry.Exception, Is.Null, retry.Error);
            Assert.That(AssemblyCalls, Is.EqualTo(1));
            Assert.That(MetadataCalls, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator ChangedCodeCannotBeLoadedAfterDisposingTheOriginalLoader()
        {
            yield return Initialize("Good", true);
            var first = Loader.LoadAsync(Address);
            yield return first;
            Assert.That(first.Exception, Is.Null, first.Error);
            var shared = Process;
            yield return TearDown();
            yield return Initialize("Conflict", true, shared);
            var conflict = Loader.LoadAsync(Address);
            yield return conflict;
            Assert.That(conflict.Exception, Is.Not.Null);
            Assert.That(conflict.RequiresRestart, Is.True);
            Assert.That(AssemblyCalls, Is.Zero);
            Assert.That(MetadataCalls, Is.Zero);
        }

        [UnityTest]
        public IEnumerator PartialNativeFailureCannotRetryOrReleaseTheProcessRestartRequirement()
        {
            yield return Initialize("Metadata", true);
            Calls.LoadAssembly = bytes =>
            {
                AssemblyCalls++;
                throw new BadImageFormatException("Intentional native substitute failure after metadata.");
            };
            var request = Loader.LoadAsync(Address);
            yield return request;
            Assert.That(request.Exception, Is.TypeOf<BadImageFormatException>());
            Assert.That(request.RequiresRestart, Is.True);
            Assert.That(MetadataCalls, Is.EqualTo(1));
            Assert.That(AssemblyCalls, Is.EqualTo(1));
            var retry = Loader.LoadAsync(Address);
            yield return retry;
            Assert.That(retry.RequiresRestart, Is.True);
            Assert.That(AssemblyCalls, Is.EqualTo(1));
            Assert.That(ResourcesSystem.GetDiagnostics().LeaseCount, Is.Zero);
        }

        [UnityTest]
        public IEnumerator TheManifestBarrierMakesStopBusyAndFreezesNewWorkUntilReleased()
        {
            yield return Initialize();
            var loaded = Loader.LoadAsync(Address);
            yield return loaded;
            Assert.That(loaded.Exception, Is.Null, loaded.Error);
            using var busy = Startup.StopAsync();
            Assert.That(busy.Status, Is.EqualTo(ResourceRequestStatus.Failed));
            Assert.That(busy.Error.Kind, Is.EqualTo(ResourceError.Busy));
            Assert.That(ResourcesSystem.IsAvailable, Is.False);
            Assert.That(ResourcesSystem.IsSessionActive, Is.True);
            Assert.That(ResourceSystem.Current, Is.SameAs(ResourcesSystem));
            Assert.That(ResourcesSystem.State, Is.EqualTo(ResourceSystemState.ShuttingDown));
            AssertManifestLease();
            Assert.Throws<InvalidOperationException>(() => Loader.LoadAsync(Address));
            Loader.Dispose();
            using var shutdown = Startup.StopAsync();
            yield return shutdown;
            Assert.That(shutdown.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), shutdown.Error?.ToString());
            Assert.That(ResourceSystem.Current, Is.Null);
            Assert.That(ResourcesSystem.IsSessionActive, Is.False);
            Assert.Throws<ObjectDisposedException>(() => Loader.LoadAsync(Address));
            ResourcesSystem = null;
        }

        [UnityTest]
        public IEnumerator CompletionObserverFailureCannotReverseTheCommittedSuccess()
        {
            yield return Initialize();
            HoldResources = true;
            var request = Loader.LoadAsync(Address);
            yield return Wait(() => PendingProvides == 1, "observed manifest producer");
            Assert.That(request.IsDone, Is.False);
            LogAssert.Expect(LogType.Exception, new Regex("Intentional HybridCLR observer failure"));
            request.AddCompleteEvent(_ => throw new InvalidOperationException("Intentional HybridCLR observer failure"));
            FinishPendingResources();
            yield return request;
            Assert.That(request.Exception, Is.Null);
            Assert.That(request.IsDone, Is.True);
            Assert.That(Loader.LoadAsync(Address).Result, Is.SameAs(request.Result));
        }

        [UnityTest]
        public IEnumerator WrongThreadIsRejectedBeforeARequestCanOwnResources()
        {
            yield return Initialize();
            Exception observed = null;
            var thread = new Thread(() =>
            {
                try
                {
                    Loader.LoadAsync(Address);
                }
                catch (Exception error)
                {
                    observed = error;
                }
            });
            thread.Start();
            Assert.That(thread.Join(5000), Is.True);
            Assert.That(observed, Is.TypeOf<InvalidOperationException>());
            Assert.That(ResourcesSystem.GetDiagnostics().LeaseCount, Is.Zero);
            Assert.That(Provides, Is.Zero);
        }

        [UnityTest]
        public IEnumerator CompletionReentryDoesNotClearNewRequestsOrAllowDisposalDuringLoad()
        {
            yield return Initialize();
            HoldResources = true;
            var request = Loader.LoadAsync(Address);
            yield return Wait(() => PendingProvides == 1, "reentrant manifest producer");
            Assert.That(request.IsDone, Is.False);
            Assert.Throws<InvalidOperationException>(() => Loader.Dispose());

            HybridCLRLoadRequest reentrant = null;
            request.AddCompleteEvent(_ => reentrant = Loader.LoadAsync(Address));
            FinishPendingResources();
            yield return request;
            Assert.That(reentrant, Is.Not.Null);
            yield return reentrant;
            Assert.That(reentrant.Exception, Is.Null, reentrant.Error);
            AssertManifestLease();
        }

        [UnityTest]
        public IEnumerator CancelingAnIndependentManifestWaiterCannotCancelTheCodeLoad()
        {
            yield return Initialize();
            HoldResources = true;
            using var cancellation = new CancellationTokenSource();
            using var waiter = ResourcesSystem.AcquireAsync<TextAsset>(ManifestKey, cancellation.Token);
            var load = Loader.LoadAsync(Address);
            yield return Wait(() => PendingProvides == 1, "shared manifest producer");
            Assert.That(Provides, Is.EqualTo(1));
            Assert.That(waiter.IsDone || load.IsDone, Is.False);
            cancellation.Cancel();
            yield return Wait(() => waiter.IsDone, "independent waiter cancellation");
            Assert.That(waiter.Status, Is.EqualTo(ResourceRequestStatus.Canceled));
            Assert.That(waiter.Error.Kind, Is.EqualTo(ResourceError.Canceled));
            Assert.That(load.IsDone, Is.False);
            FinishPendingResources();
            yield return Wait(() => load.IsDone, "remaining HybridCLR consumer");
            Assert.That(load.Exception, Is.Null, load.Error);
            Assert.That(MetadataCalls + AssemblyCalls, Is.Zero);
            AssertManifestLease();
            Loader.Dispose();
            yield return Wait(() => _provider.Releases == _provider.Provides, "last official producer release");
            Assert.That(ResourcesSystem.GetDiagnostics().LeaseCount, Is.Zero);
        }

        [UnityTest]
        public IEnumerator StopDuringManifestLoadDrainsLateCompletionWithoutLoadingNativeCode()
        {
            yield return Initialize(nativeSubstitute: true);
            HoldResources = true;
            var load = Loader.LoadAsync(Address);
            yield return Wait(() => PendingProvides == 1, "held manifest before stop");
            yield return StopBeforeLateCompletion(load);
            Assert.That(Provides, Is.EqualTo(1), "A late manifest must not start DLL or metadata loads.");
        }

        [UnityTest]
        public IEnumerator StopDuringPayloadLoadReleasesTheManifestAndRejectsLateCode()
        {
            yield return Initialize("Metadata", true);
            _provider.HeldKey = EntryKey;
            var load = Loader.LoadAsync(Address);
            yield return Wait(() => PendingProvides == 1, "held DLL after validated metadata");
            AssertManifestLease();
            Assert.That(MetadataCalls + AssemblyCalls, Is.Zero, "All payloads must validate before irreversible calls.");
            yield return StopBeforeLateCompletion(load);
            Assert.That(Process.Assemblies, Is.Empty);
            Assert.That(Process.Projects, Is.Empty);
        }

        private IEnumerator StopBeforeLateCompletion(HybridCLRLoadRequest load)
        {
            using var stop = Startup.StopAsync();
            yield return Wait(() => load.IsDone, "invalidated HybridCLR load");
            var error = load.Exception;
            Assert.That(error, Is.Not.Null);
            Assert.That(load.Result, Is.Null);
            Assert.That(load.RequiresRestart, Is.False);
            Assert.That(Process.ActiveJob, Is.Null);
            Assert.That(stop.IsDone, Is.False, "Stop must retain the still-pending official handle.");
            Assert.That(ResourcesSystem.IsAvailable, Is.False);
            Assert.That(ResourcesSystem.IsSessionActive, Is.True);
            Loader.Dispose();
            FinishPendingResources();
            yield return Wait(() => stop.IsDone, "late official completion drain");
            Assert.That(stop.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), stop.Error?.ToString());
            Assert.That(load.Exception, Is.SameAs(error));
            Assert.That(load.Result, Is.Null);
            Assert.That(load.RequiresRestart, Is.False);
            Assert.That(MetadataCalls + AssemblyCalls, Is.Zero);
            Assert.That(ResourcesSystem.GetDiagnostics().LeaseCount, Is.Zero);
            Assert.That(ResourcesSystem.GetDiagnostics().DrainingHandleCount, Is.Zero);
            Assert.That(_provider.Releases, Is.EqualTo(_provider.Provides));
            Assert.That(ResourceSystem.Current, Is.Null);
            Assert.That(ResourcesSystem.IsSessionActive, Is.False);
        }

        private void AssertManifestLease()
        {
            var ledger = ResourcesSystem.GetDiagnostics();
            Assert.That(ledger.LeaseCount, Is.EqualTo(1));
            var lease = ledger.Leases.Single();
            Assert.That(lease.Key, Is.EqualTo(ManifestKey));
            Assert.That(lease.Internal || lease.Disposed || lease.Settled, Is.False);
        }

        // Only completion timing is substituted. The locator, operations, handles and DLL bytes are real.
        // Native callbacks above isolate irreversible C++ calls; these tests are not Native/AOT acceptance.
        private sealed class FixtureTextProvider : ResourceProviderBase
        {
            private readonly List<ProvideHandle> _pending = new();
            internal bool Hold;
            internal string HeldKey;
            internal bool ForbidLoads;
            internal int Provides;
            internal int Releases;
            internal int PendingCount => _pending.Count;

            internal FixtureTextProvider(string id) => m_ProviderId = id;
            public override Type GetDefaultType(IResourceLocation location) => typeof(TextAsset);

            public override void Provide(ProvideHandle handle)
            {
                Provides++;
                handle.SetWaitForCompletionCallback(() => throw new InvalidOperationException("HybridCLR tests must not block official operations."));
                if (ForbidLoads)
                {
                    handle.Complete<TextAsset>(null, false, new InvalidOperationException("EditorFast must not read code resources."));
                }
                else if (Hold || handle.Location.PrimaryKey == HeldKey)
                {
                    _pending.Add(handle);
                }
                else
                {
                    Complete(handle);
                }
            }

            internal void FinishPending()
            {
                Hold = false;
                HeldKey = null;
                var pending = _pending.ToArray();
                _pending.Clear();
                foreach (var handle in pending)
                {
                    Complete(handle);
                }
            }

            private static void Complete(ProvideHandle handle)
            {
                var asset = handle.Location.Data as TextAsset;
                handle.Complete(asset, asset != null, asset ? null : new FileNotFoundException(handle.Location.PrimaryKey));
            }

            // Raw Resources fixtures are borrowed. Never destroy or unload somebody else's TextAsset.
            public override void Release(IResourceLocation location, object asset) => Releases++;
        }
    }
}
