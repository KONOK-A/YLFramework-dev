using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using YLFramework.Integrations.HybridCLR;
using YLFramework.Resource;
using ResourceLocation = YLFramework.Resource.ResourceLocation;

namespace YLFramework.Tests.Integrations.HybridCLR
{
    public class HybridCLRLoaderTests
    {
        internal ResourceSystem ResourcesSystem;
        internal HybridCLRLoader Loader;
        internal HybridCLRProcessState Process;
        internal HybridCLRRuntimeCalls Calls;
        protected int MetadataCalls;
        protected int AssemblyCalls;
        internal static ResourceAddress Address => new ResourceAddress("hybridclr-test", HybridCLRManifest.ManifestPath);

        internal void Initialize(string variant = "Good", bool nativeSubstitute = false, HybridCLRProcessState shared = null)
        {
            Assert.That(ResourceSystem.Current, Is.Null, "Do not take over another Resource authority.");
            var name = typeof(HybridCLRLoaderTests).Assembly.GetName().Name;
            var external = variant == "External" ? "Missing.External" : name;
            var entries = new[]
            {
                new ResourceCatalogEntry("manifest", Address, new ResourceLocation(ResourceSourceId.Resources, "YLHybridCLRTests/" + variant, typeof(TextAsset))),
                new ResourceCatalogEntry("entry", new ResourceAddress("hybridclr-test", HybridCLRManifest.PayloadAddress(external, false)),
                    new ResourceLocation(ResourceSourceId.Resources, variant == "Missing" ? "YLHybridCLRTests/Absent" : variant == "Conflict" ? "YLHybridCLRTests/ConflictEntry" : "YLHybridCLRTests/Entry", typeof(TextAsset))),
                new ResourceCatalogEntry("metadata", new ResourceAddress("hybridclr-test", HybridCLRManifest.PayloadAddress("YLFramework.HybridCLR", true)),
                    new ResourceLocation(ResourceSourceId.Resources, "YLHybridCLRTests/MetadataPayload", typeof(TextAsset)))
            };
            using var init = ResourceSystem.InitializeAsync(new ResourceConfiguration(new[] { new ResourceCatalog("hybridclr-test", entries) }));
            Assert.That(init.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), init.Error?.ToString());
            ResourcesSystem = init.TakeResult();
            Process = shared ?? new HybridCLRProcessState();
            MetadataCalls = 0;
            AssemblyCalls = 0;
            Calls = new HybridCLRRuntimeCalls
            {
                IsSimulation = !nativeSubstitute,
                LoadMetadata = bytes =>
                {
                    MetadataCalls++;
                    return 0;
                },
                LoadAssembly = bytes =>
                {
                    AssemblyCalls++;
                    return typeof(HybridCLRLoaderTests).Assembly;
                },
                LoadedAssemblies = () => nativeSubstitute ? Array.Empty<Assembly>() : AppDomain.CurrentDomain.GetAssemblies()
            };
            Loader = new HybridCLRLoader(ResourcesSystem, "base-test", Calls, Process);
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            Loader?.Dispose();
            Loader = null;
            if (ResourcesSystem != null)
            {
                using var shutdown = ResourcesSystem.ShutdownAsync();
                for (var i = 0; i < 240 && !shutdown.IsDone; i++)
                {
                    yield return null;
                }

                Assert.That(shutdown.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), shutdown.Error?.ToString());
            }
            ResourcesSystem = null;
        }

        [UnityTest]
        public IEnumerator SimulationReusesLoadedAssembliesAndRetainsOnlyOneManifestLease()
        {
            Initialize();
            var first = Loader.LoadAsync(Address);
            yield return first;
            Assert.That(first.Exception, Is.Null, first.Error);
            Assert.That(first.Result.IsSimulation, Is.True);
            Assert.That(first.Result.Assemblies.Values.Single(), Is.SameAs(typeof(HybridCLRLoaderTests).Assembly));
            Assert.That(MetadataCalls + AssemblyCalls, Is.Zero);
            Assert.That(ResourcesSystem.GetDiagnosticsSnapshot().LeaseCount, Is.EqualTo(1));
            var repeat = Loader.LoadAsync(Address);
            yield return repeat;
            Assert.That(repeat.Result, Is.SameAs(first.Result));
            Assert.That(ResourcesSystem.GetDiagnosticsSnapshot().LeaseCount, Is.EqualTo(1));
            Loader.Dispose();
            Assert.That(ResourcesSystem.IsAvailable, Is.True);
            Assert.That(ResourcesSystem.GetDiagnosticsSnapshot().LeaseCount, Is.Zero);
        }

        [UnityTest]
        public IEnumerator BadHashAndMissingDllFailBeforeAnyIrreversibleCall()
        {
            Initialize("BadHash", true);
            var request = Loader.LoadAsync(Address);
            yield return request;
            Assert.That(request.Exception, Is.Not.Null);
            Assert.That(request.RequiresRestart, Is.False);
            Assert.That(MetadataCalls + AssemblyCalls, Is.Zero);
            Assert.That(ResourcesSystem.GetDiagnosticsSnapshot().LeaseCount, Is.Zero);
        }

        [UnityTest]
        public IEnumerator ACorrectHashWithAnIncorrectPeMvidFailsBeforeNativeLoading()
        {
            Initialize("BadMvid", true);
            var request = Loader.LoadAsync(Address);
            yield return request;
            Assert.That(request.Exception, Is.Not.Null);
            Assert.That(request.RequiresRestart, Is.False);
            Assert.That(MetadataCalls + AssemblyCalls, Is.Zero);
        }

        [UnityTest]
        public IEnumerator MissingDllIsATerminalResourceFailure()
        {
            Initialize("Missing", true);
            var request = Loader.LoadAsync(Address);
            yield return request;
            Assert.That(request.Exception, Is.Not.Null);
            Assert.That(request.IsDone, Is.True);
            Assert.That(request.RequiresRestart, Is.False);
            Assert.That(MetadataCalls + AssemblyCalls, Is.Zero);
        }

        [UnityTest]
        public IEnumerator InFlightRequestsJoinAndAbortDoesNotModifyTheirState()
        {
            Initialize();
            var request = Loader.LoadAsync(Address);
            var repeat = Loader.LoadAsync(Address);
            if (!request.IsDone)
            {
                Assert.That(repeat, Is.SameAs(request));
            }

            var done = request.IsDone;
            Assert.Throws<NotSupportedException>(() => request.Abort());
            Assert.That(request.IsDone, Is.EqualTo(done));
            yield return request;
            Assert.That(request.Exception, Is.Null, request.Error);
        }

        [UnityTest]
        public IEnumerator UnknownExternalDllCannotBeAdvertisedAsEditorSimulationSuccess()
        {
            Initialize("External");
            var request = Loader.LoadAsync(Address);
            yield return request;
            Assert.That(request.Exception, Is.TypeOf<NotSupportedException>());
            Assert.That(request.RequiresRestart, Is.False);
            Assert.That(AssemblyCalls, Is.Zero);
        }

        [UnityTest]
        public IEnumerator MetadataFailureRetainsNativeErrorAndRequiresRestartForEveryLaterLoader()
        {
            Initialize("Metadata", true);
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
            Initialize("Metadata", true);
            var request = Loader.LoadAsync(Address);
            yield return request;
            Assert.That(request.Exception, Is.Null, request.Error);
            Assert.That(MetadataCalls, Is.EqualTo(1));
            Assert.That(AssemblyCalls, Is.EqualTo(1));
            Loader.Dispose();
            Loader = new HybridCLRLoader(ResourcesSystem, "base-test", Calls, Process);
            var repeat = Loader.LoadAsync(Address);
            yield return repeat;
            Assert.That(repeat.Exception, Is.Null, repeat.Error);
            Assert.That(MetadataCalls, Is.EqualTo(1));
            Assert.That(AssemblyCalls, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator CachedResourcesCanCompleteSynchronouslyWithoutHangingOrDoubleLoading()
        {
            Initialize("Good", true);
            using var manifest = ResourcesSystem.Acquire<TextAsset>(Address);
            using var entry = ResourcesSystem.Acquire<TextAsset>(new ResourceAddress("hybridclr-test", HybridCLRManifest.PayloadAddress(typeof(HybridCLRLoaderTests).Assembly.GetName().Name, false)));
            var request = Loader.LoadAsync(Address);
            Assert.That(request.IsDone, Is.True);
            Assert.That(request.Exception, Is.Null, request.Error);
            Assert.That(AssemblyCalls, Is.EqualTo(1));
            var repeat = Loader.LoadAsync(Address);
            Assert.That(repeat.IsDone, Is.True);
            Assert.That(AssemblyCalls, Is.EqualTo(1));
            yield break;
        }

        [UnityTest]
        public IEnumerator OtherInstancesCannotCompeteForTheSameNativeLoad()
        {
            Initialize("Metadata", true);
            using var other = new HybridCLRLoader(ResourcesSystem, "base-test", Calls, Process);
            var first = Loader.LoadAsync(Address);
            var pending = !first.IsDone;
            var second = other.LoadAsync(Address);
            if (pending)
            {
                Assert.That(second.IsDone, Is.True);
                Assert.That(second.Stage, Is.EqualTo("Busy"));
                Assert.That(second.Exception, Is.Not.Null);
            }
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
            Initialize("Good", true);
            var first = Loader.LoadAsync(Address);
            yield return first;
            Assert.That(first.Exception, Is.Null, first.Error);
            var shared = Process;
            Loader.Dispose();
            using (var shutdown = ResourcesSystem.ShutdownAsync())
            {
                yield return shutdown;
                Assert.That(shutdown.Status, Is.EqualTo(ResourceRequestStatus.Succeeded));
            }
            Initialize("Conflict", true, shared);
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
            Initialize("Metadata", true);
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
            Assert.That(ResourcesSystem.GetDiagnosticsSnapshot().LeaseCount, Is.Zero);
        }

        [UnityTest]
        public IEnumerator TheManifestBarrierKeepsShutdownBusyUntilTheLoaderReleasesIt()
        {
            Initialize();
            var loaded = Loader.LoadAsync(Address);
            yield return loaded;
            using var busy = ResourcesSystem.ShutdownAsync();
            Assert.That(busy.Error.Kind, Is.EqualTo(ResourceError.Busy));
            Assert.That(ResourcesSystem.IsAvailable, Is.True);
            Assert.That(Loader.LoadAsync(Address).Result, Is.SameAs(loaded.Result));
            Loader.Dispose();
            using var shutdown = ResourcesSystem.ShutdownAsync();
            yield return shutdown;
            Assert.That(shutdown.Status, Is.EqualTo(ResourceRequestStatus.Succeeded), shutdown.Error?.ToString());
            Assert.Throws<ObjectDisposedException>(() => Loader.LoadAsync(Address));
            ResourcesSystem = null;
        }

        [UnityTest]
        public IEnumerator CompletionObserverFailureCannotReverseTheCommittedSuccess()
        {
            Initialize();
            var request = Loader.LoadAsync(Address);
            if (!request.IsDone)
            {
                LogAssert.Expect(LogType.Exception, new Regex("Intentional HybridCLR observer failure"));
                request.AddCompleteEvent(_ => throw new InvalidOperationException("Intentional HybridCLR observer failure"));
            }
            else
            {
                Assert.Throws<InvalidOperationException>(() => request.AddCompleteEvent(_ => throw new InvalidOperationException("Late observer")));
            }

            yield return request;
            Assert.That(request.Exception, Is.Null);
            Assert.That(request.IsDone, Is.True);
            Assert.That(Loader.LoadAsync(Address).Result, Is.SameAs(request.Result));
        }

        [UnityTest]
        public IEnumerator WrongThreadIsRejectedBeforeARequestCanOwnResources()
        {
            Initialize();
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
            Assert.That(ResourcesSystem.GetDiagnosticsSnapshot().LeaseCount, Is.Zero);
            yield break;
        }

        [UnityTest]
        public IEnumerator CompletionReentryDoesNotClearNewRequestsOrAllowDisposalDuringLoad()
        {
            Initialize();
            var request = Loader.LoadAsync(Address);
            if (!request.IsDone)
            {
                Assert.Throws<InvalidOperationException>(() => Loader.Dispose());
            }

            HybridCLRLoadRequest reentrant = null;
            request.AddCompleteEvent(_ => reentrant = Loader.LoadAsync(Address));
            yield return request;
            Assert.That(reentrant, Is.Not.Null);
            yield return reentrant;
            Assert.That(reentrant.Exception, Is.Null, reentrant.Error);
            Assert.That(ResourcesSystem.GetDiagnosticsSnapshot().LeaseCount, Is.EqualTo(1));
        }
    }
}
