using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using NUnit.Framework;
using YLFramework.Resource;

namespace YLFramework.Tests.ResourceBuild
{
    public sealed partial class ResourceContentUpdateTests
    {
        private static string ReadStoreTree(string root) => !Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(root)) ? "missing" : string.Join("\n",
            Directory.GetFileSystemEntries(global::YLFramework.Testing.PackageTestPaths.Physical(root), "*", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.Ordinal).Select(path =>
                Path.GetRelativePath(root, path) + ":" + (File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(path)) ? ResourceCatalogCodec.Digest(File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(path))) : "directory")));

        [TestCase("None", false)]
        [TestCase("Unknown", false)]
        [TestCase("Encrypted", false)]
        [TestCase("Encrypted", true)]
        public void WorkerCheckIsByteOnlyAndDoesNotCreateOrRewriteControlFiles(string encoding, bool existingStore)
        {
            if (encoding == "Encrypted")
            {
                RequireEncryptedBackend();
            }

            using var fixture = new UpdateFixture();
            if (existingStore)
            {
                fixture.Store.Prepare(fixture.Store.Check(new[] { fixture.BuiltIn }, new[] { fixture.Create("installed") }), CancellationToken.None);
                var lockPath = Path.Combine(fixture.Store.RootPath, ".ylr-store.lock");
                Assert.That(File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(lockPath)), Is.True);
                File.Delete(global::YLFramework.Testing.PackageTestPaths.Physical(lockPath));
            }
            var target = fixture.Create("check-target", encoding == "Unknown" ? "Manifest" : null,
                encrypted: encoding == "Encrypted", corruptMiddle: encoding == "Encrypted");
            var before = ReadStoreTree(fixture.Store.RootPath);
            var mainThread = Thread.CurrentThread.ManagedThreadId;
            var checkThread = mainThread;
            UpdatePlan plan = null;
            RunIOWorker(token =>
            {
                checkThread = Thread.CurrentThread.ManagedThreadId;
                plan = fixture.Store.CheckForContent(new[] { fixture.BuiltIn }, new[] { target }, token);
            });
            Assert.That(checkThread, Is.Not.EqualTo(mainThread));
            Assert.That(plan.Projects.Single().FromDigest, Is.EqualTo(fixture.BuiltIn.Document.CatalogDigest));
            Assert.That(plan.Projects.Single().ToDigest, Is.EqualTo(target.Document.CatalogDigest));
            fixture.Store.RequireCheckedPlan(plan);
            Assert.That(ReadStoreTree(fixture.Store.RootPath), Is.EqualTo(before));
            Assert.That(File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(fixture.Store.RootPath, ".ylr-store.lock"))), Is.False);
            // Unknown头或中段坏tag仍可作为交付字节被Check；只有Prepare/Activate获得内容认证权限。
            Assert.That(File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(fixture.Control("active.json"))), Is.False);
            Assert.That(File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(fixture.Control("last-known-good.json"))), Is.False);
        }

        [Test]
        public void CanceledReadGuardStaysBusyUntilWorkerExitAndThenAllowsExplicitRetry()
        {
            using var fixture = new UpdateFixture();
            using var cancellation = new CancellationTokenSource();
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var target = fixture.Create("check-target");
            IEnumerable<ResourceLocalSnapshot> TargetClosure()
            {
                entered.Set();
                if (!release.Wait(10000))
                {
                    throw new TimeoutException("The test did not release the controlled read boundary.");
                }

                yield return target;
            }
            Exception failure = null;
            var worker = new Thread(() =>
            {
                try
                {
                    fixture.Store.CheckForContent(new[] { fixture.BuiltIn }, TargetClosure(), cancellation.Token);
                }
                catch (Exception error)
                {
                    failure = error;
                }
            });
            worker.Start();
            try
            {
                Assert.That(entered.Wait(10000), Is.True);
                cancellation.Cancel();
                Assert.That(Assert.Throws<ResourceException>(() => fixture.Store.Check(new[] { fixture.BuiltIn }, new[] { target })).Kind,
                    Is.EqualTo(ResourceError.Busy), "Cancellation must not release a guard that a worker still owns.");
                Assert.That(Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(fixture.Store.RootPath)), Is.False);
            }
            finally
            {
                cancellation.Cancel();
                release.Set();
                Assert.That(worker.Join(10000), Is.True);
            }
            Assert.That(failure, Is.TypeOf<OperationCanceledException>(), failure?.ToString());
            UpdatePlan retry = null;
            RunIOWorker(token => retry = fixture.Store.CheckForContent(new[] { fixture.BuiltIn }, new[] { target }, token));
            Assert.That(retry.Projects.Single().ToDigest, Is.EqualTo(target.Document.CatalogDigest));
            Assert.That(Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(fixture.Store.RootPath)), Is.False);
        }
    }
}
