using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using NUnit.Framework;
using YLFramework.Resource;
using Attempt = YLFramework.Resource.ResourceContentStore.Attempt;
using Intent = YLFramework.Resource.ResourceContentStore.Intent;
using Journal = YLFramework.Resource.ResourceContentStore.Journal;
using State = YLFramework.Resource.ResourceContentStore.State;
using Version = YLFramework.Resource.ResourceContentStore.Version;

namespace YLFramework.Tests.Resource
{
    [Category("YL.Resource.Addressables")]
    [Category("YL.Special.ResourceUpdates")]
    public class ResourceContentStoreTests
    {
        private const string Application = "test.app";
        private const string Channel = "preview";
        private static readonly Version Baseline = new(10, new string('a', 64));
        private static readonly Version Candidate = new(11, new string('b', 64));
        private static readonly Version Uncommitted = new(12, new string('c', 64));
        private string _storageRoot;

        [SetUp]
        public void SetUp()
        {
            _storageRoot = Path.Combine(Path.GetTempPath(), $"yl-content-store-{Guid.NewGuid():N}");
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_storageRoot))
            {
                Directory.Delete(_storageRoot, true);
            }
        }

        [Test]
        public void WriterIsExclusiveAcrossStoresAndCompareAndSwapRejectsStaleGeneration()
        {
            var first = Open();
            var second = Open();
            Assert.That(first.ReadState(), Is.Null);

            using (var writer = Acquire(first))
            {
                Assert.That(second.TryAcquireWriter(out var blocked), Is.False);
                Assert.That(blocked, Is.Null);
                Assert.That(writer.TryCommit(0, new State(1, Baseline, null, Baseline)), Is.True);
            }

            using (var writer = Acquire(second))
            {
                Assert.That(writer.TryCommit(0, new State(1, Candidate, null, Candidate)), Is.False);
                Assert.That(writer.ReadState().Active, Is.EqualTo(Baseline));
                Assert.That(writer.TryCommit(1, new State(2, Candidate, Baseline, Candidate)), Is.True);
            }

            AssertState(first.ReadState(), 2, Candidate, Baseline, Candidate);
        }

        [Test]
        public void ApplicationAndChannelHaveIndependentStateAndWriters()
        {
            var preview = Open();
            var release = new ResourceContentStore(_storageRoot, Application, "release");
            var otherApp = new ResourceContentStore(_storageRoot, "other.app", Channel);
            using var previewWriter = Acquire(preview);
            Assert.That(previewWriter.TryCommit(0, new State(1, Baseline, null, Baseline)), Is.True);

            using var releaseWriter = Acquire(release);
            using var otherAppWriter = Acquire(otherApp);
            Assert.That(release.ReadState(), Is.Null);
            Assert.That(otherApp.ReadState(), Is.Null);
            Assert.That(releaseWriter.TryCommit(0, new State(1, Candidate, null, Candidate)), Is.True);
            Assert.That(otherAppWriter.TryCommit(0, new State(1, Uncommitted, null, Uncommitted)), Is.True);
            Assert.That(preview.ReadState().Active, Is.EqualTo(Baseline));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void InterruptionAtAtomicReplaceLeavesOnlyTheCommittedGeneration(bool afterReplace)
        {
            var store = Open();
            using (var writer = Acquire(store))
            {
                Assert.That(writer.TryCommit(0, new State(1, Baseline, null, Baseline)), Is.True);
                var activation = new Intent(Candidate, 1, Baseline);
                writer.WriteJournal(new Journal(Candidate, activation, null, new Attempt(activation, false)));
                store.StateCommitBoundary = isAfterReplace =>
                {
                    if (isAfterReplace == afterReplace)
                    {
                        throw new IOException("interrupted at the replacement boundary");
                    }
                };
                Assert.Throws<IOException>(() => writer.TryCommit(1, new State(2, Candidate, Baseline, Candidate)));
            }

            using var recovered = Acquire(Open());
            var state = recovered.ReadState();
            var journal = recovered.ReadJournal();
            Assert.That(journal.Ready, Is.EqualTo(Candidate));
            Assert.That(journal.Attempt.Intent.Matches(state), Is.EqualTo(!afterReplace));
            if (afterReplace)
            {
                AssertState(state, 2, Candidate, Baseline, Candidate);
            }
            else
            {
                AssertState(state, 1, Baseline, null, Baseline);
            }
        }

        [Test]
        public void CanceledJournalWritePreservesThePreviouslyScheduledCandidate()
        {
            var store = Open();
            Assert.That(store.TryAcquireWriter(out var writer), Is.True);
            using (writer)
            {
                Assert.That(writer.TryCommit(0, new State(1, Baseline, null, Baseline)), Is.True);
                writer.WriteJournal(new Journal(Candidate, new Intent(Candidate, 1, Baseline), null, null));
                using var cancellation = new CancellationTokenSource();
                cancellation.Cancel();
                var replacement = new Journal(Uncommitted, new Intent(Uncommitted, 1, Baseline), null, null);

                Assert.Throws<OperationCanceledException>(() => writer.WriteJournal(replacement, cancellation.Token));
                var existing = writer.ReadJournal();
                Assert.That(existing.Ready, Is.EqualTo(Candidate));
                Assert.That(existing.Activation.Target, Is.EqualTo(Candidate));
            }
        }

        [Test]
        public void CancellationAtStateReplacementKeepsOldGenerationAndCandidate()
        {
            var store = Open();
            using var writer = Acquire(store);
            Assert.That(writer.TryCommit(0, new State(1, Baseline, null, Baseline)), Is.True);
            var activation = new Intent(Candidate, 1, Baseline);
            writer.WriteJournal(new Journal(Candidate, activation, null, new Attempt(activation, false)));
            using var cancellation = new CancellationTokenSource();
            store.StateCommitBoundary = afterReplace =>
            {
                if (!afterReplace)
                {
                    cancellation.Cancel();
                }
            };

            Assert.Throws<OperationCanceledException>(() =>
                writer.TryCommit(1, new State(2, Candidate, Baseline, Candidate), cancellation.Token));
            AssertState(writer.ReadState(), 1, Baseline, null, Baseline);
            Assert.That(writer.ReadJournal().Ready, Is.EqualTo(Candidate));
        }

        [Test]
        public void FailedNativeReplacementKeepsOldRecord()
        {
            if (Path.DirectorySeparatorChar != '\\')
            {
                Assert.Ignore("The sharing violation is reproduced with Windows file handles.");
            }

            var store = Open();
            using var writer = Acquire(store);
            Assert.That(writer.TryCommit(0, new State(1, Baseline, null, Baseline)), Is.True);
            using (File.Open(Path.Combine(store.Root, "state"), FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Assert.Throws<IOException>(() =>
                    writer.TryCommit(1, new State(2, Candidate, Baseline, Candidate)));
                AssertState(writer.ReadState(), 1, Baseline, null, Baseline);
            }

            Assert.That(writer.TryCommit(1, new State(2, Candidate, Baseline, Candidate)), Is.True);
            AssertState(writer.ReadState(), 2, Candidate, Baseline, Candidate);
        }

        [Test]
        public void PreparingHigherSequenceAndRollingBackDoNotLowerOrAdvanceHighest()
        {
            var store = Open();
            using var writer = Acquire(store);
            Assert.That(writer.TryCommit(0, new State(1, Baseline, null, Baseline)), Is.True);
            writer.WriteJournal(new Journal(Uncommitted, new Intent(Uncommitted, 1, Baseline), null, null));
            AssertState(writer.ReadState(), 1, Baseline, null, Baseline);
            Assert.That(writer.TryCommit(1, new State(2, Candidate, Baseline, Candidate)), Is.True);

            var rollback = new Intent(Baseline, 2, Candidate);
            writer.WriteJournal(new Journal(Uncommitted, null, rollback, new Attempt(rollback, true)));
            Assert.That(writer.ReadJournal().Attempt.IsRollback, Is.True);
            var invalidHighest = new State(3, Baseline, null, Uncommitted);
            Assert.Throws<ArgumentException>(() => writer.TryCommit(2, invalidHighest));
            Assert.That(writer.TryCommit(2, new State(3, Baseline, null, Candidate)), Is.True);

            AssertState(writer.ReadState(), 3, Baseline, null, Candidate);
            Assert.That(writer.ReadJournal().Attempt.Intent.Matches(writer.ReadState()), Is.False);
            Assert.Throws<ArgumentException>(() => writer.TryCommit(3, new State(4, Candidate, Baseline, Candidate)));
        }

        [Test]
        public void StateCorruptionStopsReadsAndCommitsEvenWhenReadyCandidateRemains()
        {
            var store = Open();
            using var writer = Acquire(store);
            Assert.That(writer.TryCommit(0, new State(1, Baseline, null, Baseline)), Is.True);
            writer.WriteJournal(new Journal(Candidate, new Intent(Candidate, 1, Baseline), null, null));
            var statePath = Path.Combine(store.Root, "state");
            byte[] damagedState = File.ReadAllBytes(statePath);
            damagedState[damagedState.Length / 2] ^= 1;
            File.WriteAllBytes(statePath, damagedState);

            Assert.Throws<InvalidDataException>(() => Open().ReadState());
            Assert.Throws<InvalidDataException>(() => writer.TryCommit(0, new State(1, Candidate, null, Candidate)));
            Assert.That(writer.ReadJournal().Ready, Is.EqualTo(Candidate));
            Assert.That(File.ReadAllBytes(statePath), Is.EqualTo(damagedState));
        }

        [Test]
        public void MissingStateAfterActivationWithAnEmptyJournalRefusesAnInitialCommit()
        {
            var store = Open();
            using (var writer = Acquire(store))
            {
                Assert.That(writer.TryCommit(0, new State(1, Baseline, null, Baseline)), Is.True);
                var activation = new Intent(Candidate, 1, Baseline);
                writer.WriteJournal(new Journal(Candidate, activation, null, new Attempt(activation, false)));
                Assert.That(writer.TryCommit(1, new State(2, Candidate, Baseline, Candidate)), Is.True);
            }

            using (var writer = Acquire(Open()))
            {
                Assert.That(writer.ReadJournal().Attempt.Intent.Matches(writer.ReadState()), Is.False);
                writer.WriteJournal(new Journal(null, null, null, null));
            }

            var statePath = Path.Combine(store.Root, "state");
            File.Delete(statePath);
            var restarted = Open();
            Assert.Throws<InvalidDataException>(() => restarted.ReadState());
            using var retry = Acquire(restarted);
            Assert.Throws<InvalidDataException>(() => retry.TryCommit(0, new State(1, Baseline, null, Baseline)));
            Assert.That(File.Exists(statePath), Is.False);
        }

        [Test]
        public void PublishedVersionPathWithoutStateOrJournalRefusesAnInitialCommit()
        {
            var store = Open();
            using var writer = Acquire(store);
            string versions = Directory.CreateDirectory(Path.Combine(store.Root, "versions")).FullName;
            File.WriteAllText(Path.Combine(versions, Candidate.Digest + ".json"), "unreadable cached announcement");

            Assert.Throws<InvalidDataException>(() => writer.TryCommit(0, new State(1, Baseline, null, Baseline)));
            Assert.That(File.Exists(Path.Combine(store.Root, "state")), Is.False);
        }

        [Test]
        public void InitialCommitCanRetryWhenOnlyTemporaryFilesRemain()
        {
            var store = Open();
            using (var writer = Acquire(store))
            {
                store.StateCommitBoundary = afterReplace =>
                {
                    if (!afterReplace)
                    {
                        var staged = Directory.GetFiles(store.Root, ".state-*.tmp")[0];
                        File.Copy(staged, Path.Combine(store.Root, $".state-{Guid.NewGuid():N}.tmp"));
                        throw new IOException("initial commit interrupted");
                    }
                };
                Assert.Throws<IOException>(() => writer.TryCommit(0, new State(1, Uncommitted, null, Uncommitted)));
                File.WriteAllText(Path.Combine(store.Root, $".journal-{Guid.NewGuid():N}.tmp"), "incomplete journal");
                string versions = Directory.CreateDirectory(Path.Combine(store.Root, "versions")).FullName;
                File.WriteAllText(Path.Combine(versions, $".stage-{Guid.NewGuid():N}"), "incomplete write");
            }

            var restarted = Open();
            Assert.That(restarted.ReadState(), Is.Null);
            using var retry = Acquire(restarted);
            Assert.That(retry.TryCommit(0, new State(1, Baseline, null, Baseline)), Is.True);
            AssertState(retry.ReadState(), 1, Baseline, null, Baseline);
        }

        [Test]
        public void RecordCleanupCountsOnlyRemovedUnsharedRegularFiles()
        {
            RequireWindowsCleanup();
            var store = Open();
            using var writer = Acquire(store);
            Assert.That(writer.TryCommit(0, new State(1, Baseline, null, Baseline)), Is.True);
            writer.WriteJournal(new Journal(null, null, null, null));
            var abandonedState = Path.Combine(store.Root, $".state-{Guid.NewGuid():N}.tmp");
            var abandonedJournal = Path.Combine(store.Root, $".journal-{Guid.NewGuid():N}.tmp");
            var busy = Path.Combine(store.Root, $".state-{Guid.NewGuid():N}.tmp");
            var malformed = Path.Combine(store.Root, $".state-{Guid.NewGuid():N}.tmp");
            var foreign = Path.Combine(store.Root, ".journal-not-a-guid.tmp");
            var directory = Path.Combine(store.Root, $".state-{Guid.NewGuid():N}.tmp");
            var external = Path.Combine(_storageRoot, "external.txt");
            var link = Path.Combine(store.Root, $".journal-{Guid.NewGuid():N}.tmp");
            File.Copy(Path.Combine(store.Root, "state"), abandonedState);
            File.Copy(Path.Combine(store.Root, "journal"), abandonedJournal);
            File.Copy(Path.Combine(store.Root, "state"), busy);
            File.WriteAllText(malformed, "partial payload");
            File.WriteAllText(foreign, "keep");
            Directory.CreateDirectory(directory);
            File.WriteAllText(external, "external");
            Assert.That(CreateHardLinkW(link, external, IntPtr.Zero), Is.True);

            using (File.Open(busy, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
            {
                Assert.That(writer.CleanupTemporaryRecords(default), Is.EqualTo(2));
                Assert.That(File.Exists(busy), Is.True);
            }

            Assert.That(File.Exists(abandonedState), Is.False);
            Assert.That(File.Exists(abandonedJournal), Is.False);
            Assert.That(File.ReadAllText(malformed), Is.EqualTo("partial payload"));
            Assert.That(File.Exists(foreign), Is.True);
            Assert.That(Directory.Exists(directory), Is.True);
            Assert.That(File.Exists(link), Is.True);
            Assert.That(File.ReadAllText(external), Is.EqualTo("external"));
            Assert.That(File.Exists(Path.Combine(store.Root, "journal")), Is.True);
            AssertState(writer.ReadState(), 1, Baseline, null, Baseline);
            Assert.That(writer.CleanupTemporaryRecords(default), Is.EqualTo(1));
            Assert.That(File.Exists(busy), Is.False);
        }

        [Test]
        public void CanceledRecordCleanupLeavesTheTemporaryFileForTheNextCall()
        {
            RequireWindowsCleanup();
            var store = Open();
            using var writer = Acquire(store);
            Assert.That(writer.TryCommit(0, new State(1, Baseline, null, Baseline)), Is.True);
            var temporary = Path.Combine(store.Root, $".state-{Guid.NewGuid():N}.tmp");
            File.Copy(Path.Combine(store.Root, "state"), temporary);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            Assert.Throws<OperationCanceledException>(() => writer.CleanupTemporaryRecords(cancellation.Token));
            Assert.That(File.Exists(temporary), Is.True);
            Assert.That(writer.CleanupTemporaryRecords(default), Is.EqualTo(1));
            Assert.That(File.Exists(temporary), Is.False);
        }

        [Test]
        public void StateCopiedBetweenChannelsIsRejectedEvenWithValidChecksum()
        {
            var preview = Open();
            var release = new ResourceContentStore(_storageRoot, Application, "release");
            using var previewWriter = Acquire(preview);
            using var releaseWriter = Acquire(release);
            Assert.That(previewWriter.TryCommit(0, new State(1, Baseline, null, Baseline)), Is.True);
            File.Copy(Path.Combine(preview.Root, "state"), Path.Combine(release.Root, "state"));

            Assert.Throws<InvalidDataException>(() => release.ReadState());
        }

        private ResourceContentStore Open() => new(_storageRoot, Application, Channel);

        private static ResourceContentStore.Writer Acquire(ResourceContentStore store)
        {
            Assert.That(store.TryAcquireWriter(out var writer), Is.True);
            return writer;
        }

        private static void AssertState(State state, long generation, Version active, Version previous, Version highest)
        {
            Assert.That(state.Generation, Is.EqualTo(generation));
            Assert.That(state.Active, Is.EqualTo(active));
            Assert.That(state.Previous, Is.EqualTo(previous));
            Assert.That(state.Highest, Is.EqualTo(highest));
            Assert.That(state.HighestSequence, Is.EqualTo(highest.Sequence));
            Assert.That(state.HighestDigest, Is.EqualTo(highest.Digest));
        }

        private static void RequireWindowsCleanup()
        {
            if (Path.DirectorySeparatorChar != '\\' || IntPtr.Size != 8)
            {
                Assert.Ignore("The signed content transaction uses Win64 filesystem handles.");
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CreateHardLinkW(string link, string existingFile, IntPtr security);
    }
}
