using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using YLFramework.Resource;

namespace YLFramework.Tests.ResourceBuild
{
    public class ArtifactStoreQueryTests
    {
        [Test]
        public void MissingAndUnconfiguredQueriesDoNotCreateAStoreOrInitializeResources()
        {
            var root = Path.Combine(Path.GetTempPath(), "kilo", "e05-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            var store = new ArtifactStore(root, new[] { new ResourceProjectMount("query", "StandaloneWindows64", "app-v1") });
            var authority = ResourceSystem.Current;
            Assert.That(Query(new ResourceConfiguration()).Status, Is.EqualTo(ArtifactQueryStatus.NotConfigured));
            var configured = new ResourceConfiguration(store);
            Assert.That(Query(configured).Status, Is.EqualTo(ArtifactQueryStatus.Missing));
            Assert.That(Query(configured).Status, Is.EqualTo(ArtifactQueryStatus.Missing));
            Assert.That(Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(root)), Is.False);
            Assert.That(ResourceSystem.Current, Is.SameAs(authority));
        }

        private static ArtifactStoreReport Query(ResourceConfiguration configuration, int maxEntries = 10000, CancellationToken cancellation = default)
        {
            ArtifactStoreReport result = null;
            Run(configuration.QueryArtifactsAsync(value => result = value, maxEntries, cancellation));
            Assert.That(result, Is.Not.Null);
            return result;
        }

        private static ArtifactStoreReport Query(ArtifactStore store, int maxEntries = 10000, CancellationToken cancellation = default)
            => Query(new ResourceConfiguration(store), maxEntries, cancellation);

        private static void Run(IEnumerator routine)
        {
            try
            {
                while (routine.MoveNext())
                {
                }
            }
            finally
            {
                (routine as IDisposable)?.Dispose();
            }
        }

        [Test]
        public void CompleteClosurePreparedAndOverlappingProtectionReasonsAreReadOnly()
        {
            using var fixture = new StoreFixture(true);
            fixture.Store.Commit(fixture.Prepare(2));
            var update = fixture.Prepare(3);
            File.Delete(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(fixture.Store.RootPath, ".ylr-store.lock")));
            var before = Tree(fixture.Store.RootPath);
            var report = Query(fixture.Store);
            Assert.That(report.Status, Is.EqualTo(ArtifactQueryStatus.Ready), string.Join("\n", report.Issues));
            Assert.That(report.Projects.Select(x => x.ProjectId), Is.EquivalentTo(new[] { "catalog", "other" }));
            Assert.That(report.PreparedUpdates.Single().Projects.Count, Is.EqualTo(2));
            foreach (var project in fixture.Projects)
            {
                var active = report.Entries.Single(x => x.Path == fixture.Version(project, 2));
                Assert.That(active.Protection, Is.EqualTo(ArtifactProtection.Active | ArtifactProtection.PreparedFrom |
                    (project == "other" ? ArtifactProtection.PreparedTo : ArtifactProtection.None)));
                Assert.That(report.Entries.Single(x => x.Path == fixture.Version(project, 1)).Protection, Is.EqualTo(ArtifactProtection.LastKnownGood));
            }
            Assert.That(report.Entries.Single(x => x.Path == fixture.Version("catalog", 3)).Protection, Is.EqualTo(ArtifactProtection.PreparedTo));
            Assert.That(report.Entries.Any(x => x.IsCleanupCandidate), Is.False);
            Assert.That(report.IsSizeComplete, Is.True);
            Assert.That(report.ObservedBytes, Is.EqualTo(FileBytes(fixture.Store.RootPath)));
            Assert.That(report.CleanupCandidateBytes, Is.Zero);
            Assert.Throws<NotSupportedException>(() => ((IList<ArtifactEntry>)report.Entries).Clear());
            Assert.Throws<NotSupportedException>(() => ((IList<ArtifactTransition>)report.PreparedUpdates.Single().Projects).Clear());
            Query(fixture.Store);
            CollectionAssert.AreEqual(before, Tree(fixture.Store.RootPath));
            fixture.Store.Commit(update);
            var committed = Query(fixture.Store);
            Assert.That(committed.Entries.Single(x => x.Path == fixture.Version("other", 2)).Protection,
                Is.EqualTo(ArtifactProtection.Active | ArtifactProtection.LastKnownGood), "Unchanged projects belong to the whole transaction closure.");
        }

        [Test]
        public void VersionsCanceledStagesAndPartialPairsHaveNonOverlappingMeasuredCandidates()
        {
            using var fixture = new StoreFixture();
            using (var canceled = new CancellationTokenSource())
            {
                fixture.Store.Checkpoint = stage =>
                {
                    if (stage == "FileCopied")
                    {
                        canceled.Cancel();
                    }
                };
                Assert.Throws<OperationCanceledException>(() => fixture.Prepare(2, canceled.Token));
                fixture.Store.Checkpoint = null;
            }
            fixture.Store.Commit(fixture.Prepare(2));
            fixture.Store.Commit(fixture.Prepare(3));
            var metadata = fixture.Part(7);
            var orphan = Path.Combine(fixture.ProjectRoot("catalog"), "parts", "orphan.part");
            File.WriteAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(orphan), new byte[11]);
            var before = Tree(fixture.Store.RootPath);
            var report = Query(fixture.Store);
            Assert.That(report.Status, Is.EqualTo(ArtifactQueryStatus.Ready), string.Join("\n", report.Issues));
            var partial = report.Entries.Single(x => x.Path == metadata);
            Assert.That(partial.Kind, Is.EqualTo(ArtifactKind.Partial));
            Assert.That(partial.ObservedBytes, Is.EqualTo(7 + new FileInfo(metadata).Length + new FileInfo(metadata + ".next").Length));
            Assert.That(partial.IsCleanupCandidate, Is.True);
            Assert.That(report.Entries.Single(x => x.Path == orphan).IsCleanupCandidate, Is.False);
            Assert.That(report.Entries.Where(x => x.Kind == ArtifactKind.Staging).All(x => x.IsCleanupCandidate), Is.True);
            var obsolete = report.Entries.Single(x => x.Path == fixture.Version("catalog", 1));
            Assert.That(obsolete.IsCleanupCandidate, Is.True);
            Assert.That(obsolete.ObservedBytes, Is.EqualTo(FileBytes(obsolete.Path)));
            Assert.That(report.ObservedBytes, Is.EqualTo(FileBytes(fixture.Store.RootPath)));
            Assert.That(report.CleanupCandidateBytes, Is.EqualTo(report.Entries.Where(x => x.IsCleanupCandidate).Sum(x => x.ObservedBytes)));
            CollectionAssert.AreEqual(before, Tree(fixture.Store.RootPath));
            var cleanup = fixture.Store.Cleanup(fixture.Store.Mount());
            Assert.That(cleanup.DeletedPaths, Does.Contain(obsolete.Path));
            Assert.That(File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(metadata)), Is.False);
            Assert.That(File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(orphan)), Is.True);
        }

        [TestCase("owner")]
        [TestCase("mirrors")]
        [TestCase("prepared")]
        public void InvalidOwnershipOrClosureNeverProducesCleanupCandidates(string fault)
        {
            using var fixture = new StoreFixture(true);
            fixture.Store.Commit(fixture.Prepare(2));
            fixture.Prepare(3);
            if (fault == "owner")
            {
                File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(fixture.Store.RootPath, ".ylr-store-owner.json")), "broken");
            }

            if (fault == "mirrors")
            {
                File.Delete(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(fixture.ProjectRoot("other"), "active.json")));
            }

            if (fault == "prepared")
            {
                File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(Directory.GetFiles(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(fixture.ProjectRoot("catalog"), "prepared"))).Single()), "broken");
            }

            var before = Tree(fixture.Store.RootPath);
            var report = Query(fixture.Store);
            Assert.That(report.Status, Is.EqualTo(ArtifactQueryStatus.Invalid));
            Assert.That(report.CleanupCandidateBytes, Is.Null);
            Assert.That(report.Entries.Any(x => x.IsCleanupCandidate), Is.False);
            Assert.That(report.IsSizeComplete, Is.False);
            CollectionAssert.AreEqual(before, Tree(fixture.Store.RootPath));
        }

        [TestCase("journal")]
        [TestCase("next")]
        public void PendingJournalAndReplacementAreReportedWithoutRecovery(string fault)
        {
            using var fixture = new StoreFixture();
            var update = fixture.Prepare(2);
            if (fault == "journal")
            {
                fixture.Store.Checkpoint = stage =>
                {
                    if (stage == "JournalVerified")
                    {
                        throw new IOException("fixture interrupted commit");
                    }
                };
                Assert.Throws<IOException>(() => fixture.Store.Commit(update));
                fixture.Store.Checkpoint = null;
            }
            else
            {
                File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(fixture.ProjectRoot("catalog"), "active.json.next")), "truncated replacement");
            }

            var before = Tree(fixture.Store.RootPath);
            var report = Query(fixture.Store);
            Assert.That(report.Status, Is.EqualTo(ArtifactQueryStatus.RecoveryRequired));
            Assert.That(report.CleanupCandidateBytes, Is.Null);
            CollectionAssert.AreEqual(before, Tree(fixture.Store.RootPath));
        }

        [Test]
        public void CorruptCompleteUnknownDirectoriesAndReadFailuresAreNotSafeCandidates()
        {
            using var fixture = new StoreFixture();
            fixture.Store.Commit(fixture.Prepare(2));
            fixture.Store.Commit(fixture.Prepare(3));
            var obsolete = fixture.Version("catalog", 1);
            File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(obsolete, ".ylr-complete.json")), "broken Complete");
            var unknown = Path.Combine(fixture.ProjectRoot("catalog"), "versions", "unowned");
            Directory.CreateDirectory(global::YLFramework.Testing.PackageTestPaths.Physical(unknown));
            File.WriteAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(unknown, "do-not-scan")), new byte[101]);
            var report = Query(fixture.Store);
            var entry = report.Entries.Single(x => x.Path == obsolete);
            Assert.That(entry.State, Is.EqualTo(ArtifactState.Invalid));
            Assert.That(entry.IsCleanupCandidate, Is.False);
            Assert.That(report.Entries.Single(x => x.Path == unknown).ObservedBytes, Is.Zero);
            Assert.That(report.Entries.Single(x => x.Path == unknown).IsSizeComplete, Is.False);
            Assert.That(report.IsSizeComplete, Is.False);
            using (var locked = new FileStream(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(fixture.Store.RootPath, ".ylr-store-owner.json")), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                report = Query(fixture.Store);
                Assert.That(report.Status, Is.EqualTo(ArtifactQueryStatus.Unreadable));
                Assert.That(report.CleanupCandidateBytes, Is.Null);
            }
        }

        [Test]
        public void CancellationBudgetsTransactionsAndFilesystemChangesInvalidateCandidateConclusions()
        {
            using var fixture = new StoreFixture();
            var update = fixture.Prepare(2);
            Assert.That(Query(fixture.Store, 1).Status, Is.EqualTo(ArtifactQueryStatus.Incomplete));
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            Assert.That(Query(fixture.Store, cancellation: cancellation.Token).Status, Is.EqualTo(ArtifactQueryStatus.Canceled));
            ArtifactStoreReport report = null;
            var query = fixture.Store.QueryAsync(value => report = value);
            Assert.That(query.MoveNext(), Is.True);
            fixture.Reopen().DiscardPreparedUpdate(update);
            Run(query);
            Assert.That(report.Status, Is.EqualTo(ArtifactQueryStatus.Changed));
            Assert.That(report.CleanupCandidateBytes, Is.Null);
            query = fixture.Store.QueryAsync(value => report = value);
            Assert.That(query.MoveNext(), Is.True);
            File.AppendAllText(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(fixture.Version("catalog", 1), ".ylr-version.json")), " ");
            Run(query);
            Assert.That(report.Status, Is.EqualTo(ArtifactQueryStatus.Changed));
            Assert.That(report.Entries.Any(x => x.IsCleanupCandidate), Is.False);
        }

        [Test]
        public void BusyQueriesDoNotEnterTheStoreOrChangeItsResponsibilities()
        {
            using var fixture = new StoreFixture();
            var observed = false;
            fixture.Store.Checkpoint = stage =>
            {
                if (stage != "FileCopied" || observed)
                {
                    return;
                }

                observed = true;
                var before = Tree(fixture.Store.RootPath);
                Assert.That(Query(fixture.Store).Status, Is.EqualTo(ArtifactQueryStatus.Busy));
                CollectionAssert.AreEqual(before, Tree(fixture.Store.RootPath));
            };
            fixture.Prepare(2);
            fixture.Store.Checkpoint = null;
            Assert.That(observed, Is.True);
            var query = fixture.Store.QueryAsync(_ => { });
            Exception error = null;
            var worker = new Thread(() =>
            {
                try
                {
                    query.MoveNext();
                }
                catch (Exception cause)
                {
                    error = cause;
                }
            });
            worker.Start();
            worker.Join();
            Assert.That(error, Is.TypeOf<InvalidOperationException>());
            (query as IDisposable)?.Dispose();
        }

        [Test]
        public void PreviewDoesNotAuthorizeDeletingVersionsThatBecomePreparedLater()
        {
            using var fixture = new StoreFixture();
            fixture.Store.Commit(fixture.Prepare(2));
            fixture.Store.Commit(fixture.Prepare(3));
            var obsolete = fixture.Version("catalog", 1);
            Assert.That(Query(fixture.Store).Entries.Single(x => x.Path == obsolete).IsCleanupCandidate, Is.True);
            fixture.Prepare(1);
            var cleanup = fixture.Store.Cleanup(fixture.Store.Mount());
            Assert.That(cleanup.ProtectedVersions, Does.Contain(obsolete));
            Assert.That(Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(obsolete)), Is.True);
            Assert.That(Query(fixture.Store).Entries.Single(x => x.Path == obsolete).Protection, Is.Not.EqualTo(ArtifactProtection.None));
        }

        [Test]
        public void DirectoryLinksAreNotFollowedAndBundleBytesAreNotHashedForDisplay()
        {
            using var fixture = new StoreFixture();
            fixture.Store.Commit(fixture.Prepare(2));
            fixture.Store.Commit(fixture.Prepare(3));
            var target = Path.Combine(Path.GetDirectoryName(fixture.Store.RootPath), "outside");
            Directory.CreateDirectory(global::YLFramework.Testing.PackageTestPaths.Physical(target));
            File.WriteAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(target, "outside.bin")), new byte[113]);
            var link = Path.Combine(fixture.ProjectRoot("catalog"), "versions", "linked");
            using var process = Process.Start(new ProcessStartInfo(Environment.GetEnvironmentVariable("COMSPEC"), "/d /c mklink /J \"" + link + "\" \"" + target + "\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            Assert.That(process.WaitForExit(10000), Is.True);
            Assert.That(process.ExitCode, Is.Zero, process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd());
            try
            {
                var obsolete = fixture.Version("catalog", 1);
                var bundle = Directory.GetFiles(global::YLFramework.Testing.PackageTestPaths.Physical(obsolete)).First(x => Path.GetFileName(x).StartsWith("b_", StringComparison.Ordinal));
                using var locked = new FileStream(global::YLFramework.Testing.PackageTestPaths.Physical(bundle), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                var report = Query(fixture.Store);
                Assert.That(report.Status, Is.EqualTo(ArtifactQueryStatus.Ready), string.Join("\n", report.Issues));
                Assert.That(report.Entries.Single(x => x.Path == link).State, Is.EqualTo(ArtifactState.Linked));
                Assert.That(report.Entries.Single(x => x.Path == link).ObservedBytes, Is.Zero);
                Assert.That(report.Entries.Single(x => x.Path == link).IsSizeComplete, Is.False);
                Assert.That(report.Entries.Single(x => x.Path == obsolete).State, Is.EqualTo(ArtifactState.CompleteRecord), "Statting logical lengths must not read every Bundle.");
                Assert.That(File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(target, "outside.bin"))).Length, Is.EqualTo(113));
            }
            finally
            {
                Directory.Delete(global::YLFramework.Testing.PackageTestPaths.Physical(link));
            }
        }

        [Test]
        public void OwnedRemaindersAndStandalonePartialCandidatesFollowExistingRetryCleanup()
        {
            using var fixture = new StoreFixture();
            fixture.Store.Commit(fixture.Prepare(2));
            fixture.Store.Commit(fixture.Prepare(3));
            var obsolete = fixture.Version("catalog", 1);
            File.Delete(global::YLFramework.Testing.PackageTestPaths.Physical(Directory.GetFiles(global::YLFramework.Testing.PackageTestPaths.Physical(obsolete)).First(x => Path.GetFileName(x).StartsWith("b_", StringComparison.Ordinal))));
            var metadata = fixture.Part(3);
            File.Delete(global::YLFramework.Testing.PackageTestPaths.Physical(metadata));
            File.Delete(global::YLFramework.Testing.PackageTestPaths.Physical(Path.ChangeExtension(metadata, ".part")));
            var report = Query(fixture.Store);
            Assert.That(report.Status, Is.EqualTo(ArtifactQueryStatus.Ready), string.Join("\n", report.Issues));
            Assert.That(report.Entries.Single(x => x.Path == obsolete).State, Is.EqualTo(ArtifactState.Incomplete));
            Assert.That(report.Entries.Single(x => x.Path == obsolete).IsCleanupCandidate, Is.True);
            Assert.That(report.Entries.Single(x => x.Path == metadata + ".next").IsCleanupCandidate, Is.True);
            var cleanup = fixture.Store.Cleanup(fixture.Store.Mount());
            Assert.That(cleanup.HasSucceeded, Is.True, string.Join("\n", cleanup.Errors));
            Assert.That(Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(obsolete)), Is.False);
            Assert.That(File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(metadata + ".next")), Is.False);
        }

        [TestCase("owner")]
        [TestCase("next")]
        [TestCase("unregistered")]
        public void PartialOwnershipAndCandidateConflictsArePreserved(string fault)
        {
            using var fixture = new StoreFixture();
            fixture.Prepare(2);
            var metadata = fixture.Part(3);
            if (fault == "unregistered")
            {
                File.Delete(global::YLFramework.Testing.PackageTestPaths.Physical(metadata));
                File.Delete(global::YLFramework.Testing.PackageTestPaths.Physical(metadata + ".next"));
            }
            else
            {
                var path = fault == "owner" ? metadata : metadata + ".next";
                var record = JObject.Parse(File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(path)));
                record["OwnerId"] = new string('0', 32);
                File.WriteAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(path), ResourceCatalogCodec.CanonicalBytes(record));
            }
            var before = Tree(fixture.Store.RootPath);
            var report = Query(fixture.Store);
            Assert.That(report.Entries.Where(x => x.Path.StartsWith(Path.GetDirectoryName(metadata), StringComparison.Ordinal)).Any(x => x.IsCleanupCandidate), Is.False);
            CollectionAssert.AreEqual(before, Tree(fixture.Store.RootPath));
            fixture.Store.Cleanup(fixture.Store.Mount());
            Assert.That(File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(Path.ChangeExtension(metadata, ".part"))), Is.True);
        }

        [Test]
        public void ExistingUnownedRootsAndUnreadableVersionRecordsAreNotEmptyHealthyStores()
        {
            using var fixture = new StoreFixture();
            Directory.CreateDirectory(global::YLFramework.Testing.PackageTestPaths.Physical(fixture.Store.RootPath));
            Assert.That(Query(fixture.Store).Status, Is.EqualTo(ArtifactQueryStatus.Invalid));
            Directory.Delete(global::YLFramework.Testing.PackageTestPaths.Physical(fixture.Store.RootPath));
            fixture.Store.Commit(fixture.Prepare(2));
            fixture.Store.Commit(fixture.Prepare(3));
            var obsolete = fixture.Version("catalog", 1);
            using var input = new FileStream(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(obsolete, ".ylr-version.json")), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            var report = Query(fixture.Store);
            Assert.That(report.Entries.Single(x => x.Path == obsolete).State, Is.EqualTo(ArtifactState.Unreadable));
            Assert.That(report.Entries.Single(x => x.Path == obsolete).IsSizeComplete, Is.False);
            Assert.That(report.Entries.Single(x => x.Path == obsolete).IsCleanupCandidate, Is.False);
        }

        [Test]
        public void PartialReplacementAfterValidationButBeforeItsFirstSizeStatInvalidatesTheQuery()
        {
            using var fixture = new StoreFixture();
            fixture.Prepare(2);
            for (var i = 0; i < 40; i++)
            {
                fixture.Part(7, i);
            }

            ArtifactStoreReport report = null;
            var query = fixture.Store.QueryAsync(value => report = value);
            var changed = false;
            try
            {
                while (query.MoveNext())
                {
                    var scan = ScanContext(query);
                    var observed = (IDictionary)Field(scan, "_observations");
                    foreach (var entry in (IEnumerable)Field(scan, "_entries"))
                    {
                        var path = (string)Field(entry, "Path");
                        if (changed || !path.EndsWith(".json", StringComparison.Ordinal) || !path.Contains(Path.DirectorySeparatorChar + "parts" + Path.DirectorySeparatorChar) ||
                            !observed.Contains(path) || observed.Contains(path + ".next"))
                        {
                            continue;
                        }

                        var next = path + ".next";
                        var record = JObject.Parse(File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(next)));
                        record["OwnerId"] = new string('0', 32);
                        File.WriteAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(next), ResourceCatalogCodec.CanonicalBytes(record));
                        File.SetLastWriteTimeUtc(next, DateTime.UtcNow.AddSeconds(1));
                        changed = true;
                    }
                    if (changed)
                    {
                        break;
                    }
                }
            }
            finally
            {
                if (!changed)
                {
                    (query as IDisposable)?.Dispose();
                }
            }
            // 修复后，在验证前 next 已被观测；选择一个尚未统计完的 partial 组来验证同一跨段改写。
            if (!changed)
            {
                query = fixture.Store.QueryAsync(value => report = value);
                while (query.MoveNext())
                {
                    var scan = ScanContext(query);
                    var observed = (IDictionary)Field(scan, "_observations");
                    var entry = ((IEnumerable)Field(scan, "_entries")).Cast<object>().FirstOrDefault(x =>
                        ((string)Field(x, "Path")).Contains(Path.DirectorySeparatorChar + "parts" + Path.DirectorySeparatorChar) &&
                        !(bool)Field(x, "IsEligible") && observed.Contains((string)Field(x, "Path") + ".next"));
                    if (entry == null)
                    {
                        continue;
                    }

                    var path = (string)Field(entry, "Path") + ".next";
                    var record = JObject.Parse(File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(path)));
                    record["OwnerId"] = new string('0', 32);
                    File.WriteAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(path), ResourceCatalogCodec.CanonicalBytes(record));
                    File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(1));
                    changed = true;
                    break;
                }
            }
            Assert.That(changed, Is.True, "The fixture must actually interleave a write with a partial file-group scan.");
            Run(query);
            Assert.That(report.Status, Is.EqualTo(ArtifactQueryStatus.Changed));
            Assert.That(report.Entries.Any(x => x.IsCleanupCandidate), Is.False);
        }

        [Test]
        public void UnknownPreparedEntriesAreChargedBeforeFilteringAndJsonDirectoriesRemainUnknown()
        {
            using var fixture = new StoreFixture();
            fixture.Store.Commit(fixture.Prepare(2));
            var root = Path.Combine(fixture.ProjectRoot("catalog"), "prepared");
            var directory = Path.Combine(root, "foreign.json");
            Directory.CreateDirectory(global::YLFramework.Testing.PackageTestPaths.Physical(directory));
            for (var i = 0; i < 128; i++)
            {
                File.WriteAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(root, "foreign-" + i)), new byte[] { 1 });
            }

            var query = fixture.Store.QueryAsync(_ => { });
            Assert.That(query.MoveNext(), Is.True);
            var charged = ((IEnumerable)Field(ScanContext(query), "_entries")).Cast<object>().Count(x => ((string)Field(x, "Path")).StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal));
            (query as IDisposable)?.Dispose();
            Assert.That(charged, Is.GreaterThan(0), "Prepared directory items must be stepped and charged before trying to filter the whole directory for valid records.");
            Assert.That(Query(fixture.Store, 32).Status, Is.EqualTo(ArtifactQueryStatus.Incomplete));
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var report = Query(fixture.Store);
                Assert.That(report.Status, Is.EqualTo(ArtifactQueryStatus.Ready), string.Join("\n", report.Issues));
                var entry = report.Entries.Single(x => x.Path == directory);
                Assert.That(entry.State, Is.EqualTo(ArtifactState.Unknown));
                Assert.That(entry.IsSizeComplete, Is.False);
                Assert.That(entry.IsCleanupCandidate, Is.False);
            }
        }

        [Test]
        public void FinalProtectionChecksEachConsumeAScanStep()
        {
            using var fixture = new StoreFixture(true);
            fixture.Store.Commit(fixture.Prepare(2));
            fixture.Prepare(3);
            ArtifactStoreReport report = null;
            var query = fixture.Store.QueryAsync(value => report = value);
            Assert.That(query.MoveNext(), Is.True);
            var scan = ScanContext(query);
            Run(query);
            Assert.That(report.Status, Is.EqualTo(ArtifactQueryStatus.Ready));
            var observations = ((IDictionary)Field(scan, "_observations")).Count;
            var checks = (IEnumerable)scan.GetType().GetMethod("VerifyObservations", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(scan, null);
            Assert.That(checks.Cast<object>().Count(), Is.EqualTo(observations + report.ProtectedVersions.Count), "Every protected path check must yield to the same budget/cancellation driver; not one uncharged O(P*E) tail.");
        }

        [Test]
        public void StationaryUnknownPreparedJsonDirectoriesAreNotReportedAsConcurrentChange()
        {
            using var fixture = new StoreFixture();
            fixture.Prepare(2);
            var path = Path.Combine(fixture.ProjectRoot("catalog"), "prepared", "foreign.json");
            Directory.CreateDirectory(global::YLFramework.Testing.PackageTestPaths.Physical(path));
            for (var i = 0; i < 2; i++)
            {
                var report = Query(fixture.Store);
                Assert.That(report.Status, Is.EqualTo(ArtifactQueryStatus.Ready));
                var entry = report.Entries.Single(x => x.Path == path);
                Assert.That(entry.State, Is.EqualTo(ArtifactState.Unknown));
                Assert.That(entry.IsSizeComplete || entry.IsCleanupCandidate, Is.False);
            }
        }

        private static object Field(object target, string name) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).GetValue(target);
        private static object ScanContext(IEnumerator query) => query.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Single(x => x.FieldType.Name == "ArtifactScan").GetValue(query);

        private static long FileBytes(string root) => Directory.GetFiles(global::YLFramework.Testing.PackageTestPaths.Physical(root), "*", SearchOption.AllDirectories).Sum(x => new FileInfo(x).Length);
        private static string[] Tree(string root) => Directory.GetFileSystemEntries(global::YLFramework.Testing.PackageTestPaths.Physical(root), "*", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.Ordinal)
            .Select(x => x + ":" + File.GetAttributes(global::YLFramework.Testing.PackageTestPaths.Physical(x)) + ":" + File.GetLastWriteTimeUtc(x).Ticks + ":" +
                (File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(x)) ? Path.GetFileName(x) == ".ylr-store.lock" ? "lock:" + new FileInfo(x).Length : ResourceCatalogCodec.Digest(File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(x))) : "directory")).ToArray();

        // 复用 Catalog v1 夹具及正式 Open/Check/Prepare/Commit。文件内容只服务 Store IO，不冒充 native Bundle 验收。
        private sealed class StoreFixture : IDisposable
        {
            private readonly string _root = Path.Combine(Path.GetTempPath(), "kilo", "q05-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            private readonly Dictionary<(string, int), ResourceLocalSnapshot> _snapshots = new();
            internal readonly string[] Projects;
            internal ArtifactStore Store
            {
                get;
            }
            internal StoreFixture(bool multi = false)
            {
                Projects = multi ? new[] { "catalog", "other" } : new[] { "catalog" };
                foreach (var project in Projects)
                {
                    for (var version = 1; version <= (project == "catalog" ? 3 : 2); version++)
                    {
                        _snapshots.Add((project, version), Create(project, version));
                    }
                }

                Store = Reopen();
            }
            internal ArtifactStore Reopen() => new(Path.Combine(_root, "s"), Projects.Select(x => new ResourceProjectMount(x, "StandaloneWindows64", "app-v1", _snapshots[(x, 1)])));
            internal string ProjectRoot(string project) => Path.Combine(Store.RootPath, project, "StandaloneWindows64");
            internal string Version(string project, int version) => Path.Combine(ProjectRoot(project), "versions", _snapshots[(project, version)].Document.CatalogDigest);
            internal PreparedUpdate Prepare(int version, CancellationToken cancellation = default) => Store.Prepare(Store.Check(Store.Mount(),
                Projects.Select(x => _snapshots[(x, x == "catalog" ? version : Math.Min(version, 2))])), cancellation);

            private ResourceLocalSnapshot Create(string project, int version)
            {
                var payload = ResourceCatalogCodecTests.Payload();
                payload["ProjectId"] = project;
                payload["UnityCompatibility"] = Application.unityVersion;
                var bundle = payload["Bundles"][0];
                var oldFile = (string)bundle["RelativeFile"];
                var name = "ylr_" + ResourceCatalogCodec.Digest(Encoding.UTF8.GetBytes(project)).Substring(0, 32) + "_a_0123456789abcdef0123456789abcdef";
                var file = "b_" + ResourceCatalogCodec.Digest(Encoding.UTF8.GetBytes(name)).Substring(0, 32);
                bundle["LogicalId"] = name;
                bundle["NativeName"] = name;
                bundle["RelativeFile"] = file;
                payload["Assets"][0]["BundleId"] = name;
                foreach (var item in payload["SupportFiles"])
                {
                    if ((string)item["RelativeFile"] == oldFile + ".manifest")
                    {
                        item["RelativeFile"] = file + ".manifest";
                    }
                }

                var files = new Dictionary<string, byte[]>();
                foreach (JObject record in ((JArray)payload["Bundles"]).Concat((JArray)payload["SupportFiles"]))
                {
                    var binary = record["LogicalId"] != null || (string)record["RelativeFile"] == (string)payload["ManifestFile"];
                    var bytes = Encoding.UTF8.GetBytes((binary ? "UnityFS\0" : "") + project + ":" + version + ":" + record["RelativeFile"]);
                    record["Length"] = bytes.Length;
                    record["Sha256"] = ResourceCatalogCodec.Digest(bytes);
                    files.Add((string)record["RelativeFile"], bytes);
                }
                var catalogBytes = ResourceCatalogCodec.Seal(ResourceCatalogCodec.CanonicalBytes(payload));
                var document = ResourceCatalogCodec.Read(catalogBytes, project, "StandaloneWindows64", Application.unityVersion, "app-v1");
                var path = Path.Combine(_root, "i", project, document.CatalogDigest);
                Directory.CreateDirectory(global::YLFramework.Testing.PackageTestPaths.Physical(path));
                foreach (var item in files)
                {
                    File.WriteAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(path, item.Key)), item.Value);
                }

                File.WriteAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(path, "resource-catalog.json")), catalogBytes);
                File.WriteAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(path, ".ylr-complete.json")), ResourceCatalogCodec.CanonicalBytes(new JObject
                {
                    ["Version"] = 1,
                    ["ProjectGuid"] = new string('1', 32),
                    ["ProjectId"] = project,
                    ["Platform"] = document.Platform,
                    ["BuildId"] = document.BuildId,
                    ["CatalogDigest"] = document.CatalogDigest
                }));
                return ResourceLocalSnapshot.Open(path, project, document.CatalogDigest, document.Platform, "app-v1");
            }

            internal string Part(int length, int identity = 0)
            {
                var doc = _snapshots[("catalog", 3)].Document;
                var file = doc.SupportFiles[0];
                var source = "https://fixture.invalid/releases/" + identity + "/" + doc.CatalogDigest + "/" + file.RelativeFile;
                var token = ResourceCatalogCodec.Digest(Encoding.UTF8.GetBytes(source + ":" + file.Sha256)).Substring(0, 32);
                var path = Path.Combine(ProjectRoot("catalog"), "parts", token + ".json");
                var record = new JObject
                {
                    ["Version"] = 1,
                    ["OwnerId"] = JObject.Parse(File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(Store.RootPath, ".ylr-store-owner.json"))))["OwnerId"],
                    ["State"] = "Partial",
                    ["ProjectId"] = doc.ProjectId,
                    ["Platform"] = doc.Platform,
                    ["CatalogDigest"] = doc.CatalogDigest,
                    ["File"] = file.RelativeFile,
                    ["Length"] = file.Length,
                    ["Sha256"] = file.Sha256,
                    ["Source"] = source,
                    ["ETag"] = "\"fixture\""
                };
                File.WriteAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(path), ResourceCatalogCodec.CanonicalBytes(record));
                File.WriteAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(path + ".next"), ResourceCatalogCodec.CanonicalBytes(record));
                File.WriteAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(Path.ChangeExtension(path, ".part")), new byte[length]);
                return path;
            }
            public void Dispose()
            {
                if (Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(_root)))
                {
                    Directory.Delete(global::YLFramework.Testing.PackageTestPaths.Physical(_root), true);
                }
            }
        }
    }
}
