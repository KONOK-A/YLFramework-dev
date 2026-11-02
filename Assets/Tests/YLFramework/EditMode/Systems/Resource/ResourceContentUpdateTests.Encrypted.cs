using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using NUnit.Framework;
using UnityEngine;
using YLFramework.Resource;

namespace YLFramework.Tests.ResourceBuild
{
    public sealed partial class ResourceContentUpdateTests
    {
        private static readonly byte[] _testMaterial = Enumerable.Range(1, 32).Select(x => (byte)x).ToArray();

        private sealed class ContentKeys : IResourceContentKeyProvider
        {
            internal bool Available = true;
            internal byte[] Material = (byte[])_testMaterial.Clone();
            internal int Calls;
            internal int CallingThread;
            internal Action Copying;

            public bool TryCopyKey(string projectId, string keyId, byte[] destination)
            {
                Calls++;
                CallingThread = Thread.CurrentThread.ManagedThreadId;
                Assert.That(projectId, Is.EqualTo("catalog"));
                Assert.That(keyId, Is.EqualTo("test-content"));
                Assert.That(destination.Length, Is.EqualTo(32));
                Copying?.Invoke();
                if (!Available)
                {
                    return false;
                }

                Buffer.BlockCopy(Material, 0, destination, 0, 32);
                return true;
            }
        }

        private static void RequireEncryptedBackend()
        {
            if ((Application.platform != RuntimePlatform.WindowsEditor && Application.platform != RuntimePlatform.WindowsPlayer) || IntPtr.Size != 8)
            {
                Assert.Ignore("Encrypted update integration is supported only on Windows x64 Editor/Mono Player.");
            }
        }

        private static void RunIOWorker(Action<CancellationToken> action, CancellationToken cancellation = default)
        {
            Exception failure = null;
            var worker = new Thread(() =>
            {
                try
                {
                    action(cancellation);
                }
                catch (Exception error)
                {
                    failure = error;
                }
            });
            worker.Start();
            Assert.That(worker.Join(10000), Is.True, "Pure update IO must settle without requesting Unity/main-thread work.");
            if (failure != null)
            {
                ExceptionDispatchInfo.Capture(failure).Throw();
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void InstallationForContentCopiesOnWorkerAndCapturesKeysOnlyAfterMainThreadHandoff(bool encrypted)
        {
            if (encrypted)
            {
                RequireEncryptedBackend();
            }

            using var fixture = new UpdateFixture();
            var target = fixture.Create("worker-target", encrypted: encrypted);
            var plan = fixture.Store.Check(fixture.Store.Mount(), new[] { target });
            var mainThread = Thread.CurrentThread.ManagedThreadId;
            var copyingThreads = new List<int>();
            fixture.Store.Checkpoint = point =>
            {
                if (point == "FileCopied")
                {
                    copyingThreads.Add(Thread.CurrentThread.ManagedThreadId);
                }
            };
            var keys = new ContentKeys();
            RunIOWorker(token => fixture.Store.InstallPreparationForContent(plan, token));
            Assert.That(copyingThreads, Is.Not.Empty);
            Assert.That(copyingThreads.All(x => x != mainThread), Is.True);
            Assert.That(keys.Calls, Is.Zero);
            Assert.That(fixture.Store.GetPreparedUpdates(), Is.Empty);
            using var receipt = fixture.Store.CapturePreparation(plan, encrypted ? new ResourceDecryptionOptions(keys) : null);
            Assert.That(keys.Calls, Is.EqualTo(encrypted ? 1 : 0));
            if (encrypted)
            {
                Assert.That(keys.CallingThread, Is.EqualTo(mainThread));
            }

            RunIOWorker(token => receipt.Validate(token));
            fixture.Store.CompletePreparation(plan, receipt);
            Assert.That(fixture.Store.GetPreparedUpdates().Count, Is.EqualTo(1));
        }

        [Test]
        public void CanceledInstallationWorkerCannotRecordPreparedAndExplicitRetryUsesTheSameAuthority()
        {
            using var fixture = new UpdateFixture();
            var plan = fixture.Store.Check(fixture.Store.Mount(), new[] { fixture.Create("worker-target") });
            using var cancellation = new CancellationTokenSource();
            fixture.Store.Checkpoint = point =>
            {
                if (point == "FileCopied")
                {
                    cancellation.Cancel();
                }
            };
            Assert.Throws<OperationCanceledException>(() => RunIOWorker(token => fixture.Store.InstallPreparationForContent(plan, token), cancellation.Token));
            Assert.That(fixture.Store.GetPreparedUpdates(), Is.Empty);
            Assert.That(File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(fixture.Control("active.json"))), Is.False);
            fixture.Store.Checkpoint = null;
            RunIOWorker(token => fixture.Store.InstallPreparationForContent(plan, token));
            using var receipt = fixture.Store.CapturePreparation(plan);
            RunIOWorker(token => receipt.Validate(token));
            fixture.Store.CompletePreparation(plan, receipt);
            Assert.That(fixture.Store.GetPreparedUpdates().Count, Is.EqualTo(1));
        }

        [TestCase("missing")]
        [TestCase("wrong")]
        [TestCase("middle")]
        public void DirectPrepareAuthenticatesTheWholeCiphertextBeforePrepared(string fault)
        {
            RequireEncryptedBackend();
            using var fixture = new UpdateFixture();
            var target = fixture.Create("encrypted", encrypted: true, corruptMiddle: fault == "middle");
            var plan = fixture.Store.Check(fixture.Store.Mount(), new[] { target });
            var keys = new ContentKeys { Available = fault != "missing" };
            if (fault == "wrong")
            {
                keys.Material[0] ^= 1;
            }

            Assert.That(keys.Calls, Is.Zero, "Check/Mount cannot request content keys.");
            var error = Assert.Throws<ResourceException>(() => fixture.Store.Prepare(plan, CancellationToken.None, new ResourceDecryptionOptions(keys)));
            Assert.That(error.Kind, Is.EqualTo(fault == "missing" ? ResourceError.ContentKeyUnavailable : ResourceError.ContentAuthenticationFailed));
            Assert.That(fixture.Store.GetPreparedUpdates(), Is.Empty);
            Assert.That(File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(fixture.Control("active.json"))), Is.False);
            Assert.That(File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(fixture.Control("last-known-good.json"))), Is.False);
            foreach (var file in target.Document.Bundles.Cast<ResourceCatalogFile>().Concat(target.Document.SupportFiles))
            {
                CollectionAssert.AreEqual(File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(target.DirectoryPath, file.RelativeFile))),
                    File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(fixture.Owned(target), file.RelativeFile))), "Content/key failure must retain delivery bytes for an explicit retry.");
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void InstalledAndUnchangedTargetsReacquireTheCurrentKey(bool activateFirst)
        {
            RequireEncryptedBackend();
            using var fixture = new UpdateFixture();
            var target = fixture.Create("encrypted", encrypted: true);
            var keys = new ContentKeys();
            var options = new ResourceDecryptionOptions(keys);
            var plan = fixture.Store.Check(fixture.Store.Mount(), new[] { target });
            var prepared = fixture.Store.Prepare(plan, CancellationToken.None, options);
            if (activateFirst)
            {
                fixture.Store.Commit(prepared, options);
            }

            plan = fixture.Store.Check(fixture.Store.Mount(), new[] { target });
            Assert.That(plan.Projects.Single().IsChanged, Is.EqualTo(!activateFirst));
            var before = keys.Calls;
            keys.Available = false;
            var error = Assert.Throws<ResourceException>(() => fixture.Store.Prepare(plan, CancellationToken.None, options));
            Assert.That(error.Kind, Is.EqualTo(ResourceError.ContentKeyUnavailable));
            Assert.That(keys.Calls, Is.GreaterThan(before));
            Assert.That(fixture.Store.Mount().Single().Document.CatalogDigest,
                Is.EqualTo(activateFirst ? target.Document.CatalogDigest : fixture.BuiltIn.Document.CatalogDigest));
        }

        [Test]
        public void CommitAndRollbackRevalidateKeysBeforeAnyJournalOrPointerChange()
        {
            RequireEncryptedBackend();
            using var fixture = new UpdateFixture();
            var encrypted = fixture.Create("encrypted", encrypted: true);
            var keys = new ContentKeys();
            var options = new ResourceDecryptionOptions(keys);
            var prepared = fixture.Store.Prepare(fixture.Store.Check(fixture.Store.Mount(), new[] { encrypted }), CancellationToken.None, options);
            keys.Available = false;
            Assert.That(Assert.Throws<ResourceException>(() => fixture.Store.Commit(prepared, options)).Kind, Is.EqualTo(ResourceError.ContentKeyUnavailable));
            Assert.That(File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(fixture.Control("commit-journal.json"))), Is.False);
            Assert.That(File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(fixture.Control("active.json"))), Is.False);
            keys.Available = true;
            fixture.Store.Commit(prepared, options);
            var plain = fixture.Create("plain-next");
            fixture.Store.Commit(fixture.Store.Prepare(fixture.Store.Check(fixture.Store.Mount(), new[] { plain }), CancellationToken.None));
            var rollback = fixture.Store.Rollback(fixture.Store.Mount());
            var active = File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(fixture.Control("active.json")));
            var good = File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(fixture.Control("last-known-good.json")));
            var calls = keys.Calls;
            Assert.That(Assert.Throws<ResourceException>(() => fixture.Store.Commit(rollback)).Kind, Is.EqualTo(ResourceError.ContentKeyUnavailable));
            CollectionAssert.AreEqual(active, File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(fixture.Control("active.json"))));
            CollectionAssert.AreEqual(good, File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(fixture.Control("last-known-good.json"))));
            Assert.That(fixture.Store.Mount().Single().Document.CatalogDigest, Is.EqualTo(plain.Document.CatalogDigest));
            Assert.That(keys.Calls, Is.EqualTo(calls), "Byte/journal recovery does not request content keys.");
            fixture.Store.Commit(rollback, options);
            Assert.That(fixture.Store.Mount().Single().Document.CatalogDigest, Is.EqualTo(encrypted.Document.CatalogDigest));
        }

        [Test]
        public void ProviderRunsOnTheCallingThreadOutsideTheStoreLockAndCopiesOncePerKey()
        {
            RequireEncryptedBackend();
            using var fixture = new UpdateFixture();
            var target = fixture.Create("encrypted", encrypted: true);
            var plan = fixture.Store.Check(fixture.Store.Mount(), new[] { target });
            var keys = new ContentKeys { Copying = () => fixture.Store.Check(new[] { fixture.BuiltIn }, new[] { target }) };
            fixture.Store.Prepare(plan, CancellationToken.None, new ResourceDecryptionOptions(keys));
            Assert.That(keys.Calls, Is.EqualTo(1), "Bundle and Manifest sharing one (ProjectId,KeyId) use an operation-owned copy.");
            Assert.That(keys.CallingThread, Is.EqualTo(Thread.CurrentThread.ManagedThreadId));
            CollectionAssert.AreEqual(_testMaterial, keys.Material, "Cleanup clears only private key copies, never application material.");
        }

        [Test]
        public void ReceiptRequiresValidationAndRejectsAStoreChangeBeforeFinalization()
        {
            using var fixture = new UpdateFixture();
            var target = fixture.Create("first-target");
            var other = fixture.Create("other-target");
            var plan = fixture.Store.Check(fixture.Store.Mount(), new[] { target });
            var next = fixture.Store.Check(fixture.Store.Mount(), new[] { other });
            fixture.Store.InstallPreparation(plan, CancellationToken.None);
            using var receipt = fixture.Store.CapturePreparation(plan);
            Assert.Throws<InvalidOperationException>(() => fixture.Store.CompletePreparation(plan, receipt));
            receipt.Validate();
            fixture.Store.InstallPreparation(next, CancellationToken.None);
            Assert.That(Assert.Throws<ResourceException>(() => fixture.Store.CompletePreparation(plan, receipt)).Kind, Is.EqualTo(ResourceError.StaleBase));
            Assert.That(fixture.Store.GetPreparedUpdates(), Is.Empty);
        }

        [Test]
        public void CanceledValidationCannotProduceAPreparedReceipt()
        {
            using var fixture = new UpdateFixture();
            var plan = fixture.Store.Check(fixture.Store.Mount(), new[] { fixture.Create("target") });
            fixture.Store.InstallPreparation(plan, CancellationToken.None);
            using var receipt = fixture.Store.CapturePreparation(plan);
            Assert.Throws<OperationCanceledException>(() => receipt.Validate(new CancellationToken(true)));
            Assert.Throws<InvalidOperationException>(() => fixture.Store.CompletePreparation(plan, receipt));
            Assert.That(fixture.Store.GetPreparedUpdates(), Is.Empty);
        }

        [Test]
        public void ValidatedReceiptIsBoundToItsPlanAndCanOnlyBeConsumedOnce()
        {
            using var fixture = new UpdateFixture();
            var target = fixture.Create("target");
            var plan = fixture.Store.Check(fixture.Store.Mount(), new[] { target });
            var sameTransition = fixture.Store.Check(fixture.Store.Mount(), new[] { target });
            fixture.Store.InstallPreparation(plan, CancellationToken.None);
            using var receipt = fixture.Store.CapturePreparation(plan);
            receipt.Validate();
            Assert.Throws<InvalidOperationException>(() => fixture.Store.CompletePreparation(sameTransition, receipt));
            fixture.Store.CompletePreparation(plan, receipt);
            Assert.Throws<InvalidOperationException>(() => fixture.Store.CompletePreparation(plan, receipt));
            Assert.That(fixture.Store.GetPreparedUpdates().Count, Is.EqualTo(1));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ValidatedInputsStayReadOnlyThroughFinalizationUntilExplicitDisposal(bool encrypted)
        {
            RequireEncryptedBackend();
            using var fixture = new UpdateFixture();
            var target = fixture.Create("target", encrypted: encrypted);
            var plan = fixture.Store.Check(fixture.Store.Mount(), new[] { target });
            fixture.Store.InstallPreparation(plan, CancellationToken.None);
            var path = Path.Combine(fixture.Owned(target), target.Document.Bundles[0].RelativeFile);
            var bytes = File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(path));
            using (var receipt = fixture.Store.CapturePreparation(plan, encrypted ? new ResourceDecryptionOptions(new ContentKeys()) : null))
            {
                receipt.Validate();
                Assert.Throws<IOException>(() => File.WriteAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(path), bytes));
                fixture.Store.CompletePreparation(plan, receipt);
                Assert.Throws<IOException>(() => File.WriteAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(path), bytes), "Recording Prepared does not prematurely release authenticated inputs.");
            }
            Assert.DoesNotThrow(() => File.WriteAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(path), bytes));
        }

        [Test]
        public void WorkerMountUsesTheOriginalKeyFreeRecoveryAuthority()
        {
            using var fixture = new UpdateFixture();
            var target = fixture.Create("target");
            fixture.Store.Commit(fixture.Store.Prepare(fixture.Store.Check(fixture.Store.Mount(), new[] { target }), CancellationToken.None));
            ResourceLocalSnapshot selected = null;
            Exception failure = null;
            var worker = new Thread(() =>
            {
                try
                {
                    selected = fixture.Store.MountForContent(CancellationToken.None).Single();
                }
                catch (Exception error)
                {
                    failure = error;
                }
            });
            worker.Start();
            Assert.That(worker.Join(10000), Is.True, "Pure IO mount must settle without waiting for Unity/main-thread work.");
            Assert.That(failure, Is.Null, failure?.ToString());
            Assert.That(selected.Document.CatalogDigest, Is.EqualTo(target.Document.CatalogDigest));
            Assert.That(File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(fixture.Control("commit-journal.json"))), Is.False);
        }
    }
}
