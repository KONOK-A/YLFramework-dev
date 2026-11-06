using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using YLFramework.RedDots;
using YLFramework.Save;

namespace YLFramework.Tests.RedDots.Save
{
    [Category("YL.Module.ReddotSave")]
    public sealed class ReddotSaveTests
    {
        private string _root;

        [SetUp]
        public void Prepare()
        {
            ReddotManager.Reset();
            var output = Environment.GetEnvironmentVariable("YL_REDDOT_SAVE_TEST_OUTPUT") ??
                Path.Combine(Directory.GetCurrentDirectory(), "Temp", "ReddotSave");
            _root = Path.GetFullPath(Path.Combine(output, "cases", Guid.NewGuid().ToString("N")));
            Directory.CreateDirectory(_root);
        }

        [TearDown]
        public void Clean()
        {
            ReddotManager.Reset();
            if (TestContext.CurrentContext.Result.Outcome.Status == NUnit.Framework.Interfaces.TestStatus.Passed)
            {
                Directory.Delete(_root, true);
            }
            else
            {
                TestContext.Error.WriteLine("Preserved ReddotSave fixture: " + _root);
            }
        }

        [Test]
        public void SaveSnapshotRoundTripAndMissingCorruptCases()
        {
            using var author = new ReddotReadState("account/slot-7");
            author.MarkRead("mail", "v10");
            author.MarkRead("mail", "v2");
            using (var store = new SaveStore(_root))
            {
                store.CreateSaveItem(7);
                store.CreateSaveItem(8);
                store.CurrentSaveId = 8;
                var missing = store.ReadReddotSnapshotAsync(name: "confirmed", id: 7,
                    cancellationToken: CancellationToken.None).GetAwaiter().GetResult();
                Assert.That(missing.Status, Is.EqualTo(SaveReadStatus.Missing));
                Assert.That(missing.Value, Is.Null);
                Assert.That(missing.IsCached, Is.False);
                Assert.That(File.Exists(SnapshotPath(7)), Is.False);
                store.SaveReddotSnapshotAsync(value: author.Export(), name: "confirmed", id: 7,
                    cancellationToken: CancellationToken.None).GetAwaiter().GetResult();
                Assert.That(store.CurrentSaveId, Is.EqualTo(8), "Snapshot IO must not infer or switch the active slot.");
                Assert.That(store.ReadReddotSnapshotAsync("confirmed", 8).GetAwaiter().GetResult().Status,
                    Is.EqualTo(SaveReadStatus.Missing));
            }
            using var target = new ReddotReadState("account/slot-7");
            using var unread = target.BindUnreadSource("Mail", "mail", "mail", "v2");
            using (var reopened = new SaveStore(_root))
            {
                var loaded = reopened.ReadReddotSnapshotAsync("confirmed", 7).GetAwaiter().GetResult();
                Assert.That(loaded.Status, Is.EqualTo(SaveReadStatus.Success));
                Assert.That(loaded.IsCached, Is.False);
                Assert.That(loaded.Value.schemaVersion, Is.EqualTo(1));
                Assert.That(loaded.Value.records[0].versions, Is.EqualTo(new[] { "v10", "v2" }));
                Assert.That(target.IsRead("mail", "v2"), Is.False, "Reading never imports implicitly.");
                Assert.That(ReddotManager.GetValue("Mail"), Is.EqualTo(1));
                target.Import(loaded.Value);
                Assert.That(target.IsRead("mail", "v10"), Is.True);
                Assert.That(target.IsRead("mail", "v2"), Is.True);
                Assert.That(ReddotManager.GetValue("Mail"), Is.Zero);
                var cached = reopened.ReadReddotSnapshotAsync("confirmed", 7).GetAwaiter().GetResult();
                Assert.That(cached.IsCached, Is.True);
                Assert.That(cached.Value, Is.SameAs(loaded.Value), "Preserve SaveStore cache identity and provenance.");
                reopened.ClearCache();
                File.WriteAllText(SnapshotPath(7), "{broken");
                Assert.Throws<InvalidDataException>(() => reopened.ReadReddotSnapshotAsync("confirmed", 7)
                    .GetAwaiter().GetResult());
                Assert.That(target.IsRead("mail", "v2"), Is.True);
                Assert.That(File.ReadAllText(SnapshotPath(7)), Is.EqualTo("{broken"));
            }
        }

        [TestCase("{\"readNamespace\":\"slot-0\",\"records\":[]}")]
        [TestCase("{\"schemaVersion\":2,\"readNamespace\":\"slot-0\",\"records\":[]}")]
        [TestCase("{\"schemaVersion\":1,\"records\":[]}")]
        [TestCase("{\"schemaVersion\":1,\"readNamespace\":\"slot-0\"}")]
        [TestCase("{\"schemaVersion\":1,\"readNamespace\":\"slot-0\",\"records\":[{\"key\":\"mail\"}]}")]
        [TestCase("{\"schemaVersion\":1,\"readNamespace\":\"slot-0\",\"records\":[{\"key\":\"\",\"versions\":[\"v1\"]}]}")]
        [TestCase("{\"schemaVersion\":1,\"readNamespace\":\"slot-0\",\"records\":[{\"key\":\"mail\",\"versions\":[\"v1\",\"v1\"]}]}")]
        [TestCase("{\"schemaVersion\":1,\"readNamespace\":\"slot-0\",\"records\":[{\"key\":\"mail\",\"versions\":[\"v1\"]},{\"key\":\"mail\",\"versions\":[\"v2\"]}]}")]
        public void MalformedSnapshotFailsWithoutImportRepairOrDefaultWrite(string json)
        {
            using var state = new ReddotReadState("slot-0");
            state.MarkRead("known", "v1");
            using var source = state.BindUnreadSource("Mail", "known", "known", "v1");
            using var store = new SaveStore(_root);
            store.CreateSaveItem(0);
            Directory.CreateDirectory(Path.GetDirectoryName(SnapshotPath(0)));
            File.WriteAllText(SnapshotPath(0), json);
            Assert.Throws<InvalidDataException>(() => store.ReadReddotSnapshotAsync("confirmed", 0)
                .GetAwaiter().GetResult());
            Assert.That(state.IsRead("known", "v1"), Is.True);
            Assert.That(ReddotManager.GetValue("Mail"), Is.Zero);
            Assert.That(File.ReadAllText(SnapshotPath(0)), Is.EqualTo(json));
            Assert.That(Directory.GetFiles(Path.GetDirectoryName(SnapshotPath(0))),
                Is.EqualTo(new[] { SnapshotPath(0) }));
        }

        [Test]
        public void BackupProvenanceIsPreservedWithoutAutomaticRecoveryOrOverwrite()
        {
            using var state = new ReddotReadState("slot-0");
            state.MarkRead("mail", "v1");
            using var store = new SaveStore(_root);
            store.CreateSaveItem(0);
            store.SaveReddotSnapshotAsync(state.Export(), "confirmed", 0).GetAwaiter().GetResult();
            var first = File.ReadAllText(SnapshotPath(0));
            state.MarkRead("mail", "v2");
            store.SaveReddotSnapshotAsync(state.Export(), "confirmed", 0).GetAwaiter().GetResult();
            File.WriteAllText(SnapshotPath(0), "{interrupted");
            store.ClearCache();
            var recovered = store.ReadReddotSnapshotAsync("confirmed", 0).GetAwaiter().GetResult();
            Assert.That(recovered.Status, Is.EqualTo(SaveReadStatus.RecoveredFromBackup));
            Assert.That(recovered.IsCached, Is.False);
            Assert.That(recovered.Value.records[0].versions, Is.EqualTo(new[] { "v1" }));
            var cached = store.ReadReddotSnapshotAsync("confirmed", 0).GetAwaiter().GetResult();
            Assert.That(cached.Status, Is.EqualTo(SaveReadStatus.RecoveredFromBackup));
            Assert.That(cached.IsCached, Is.True);
            Assert.That(cached.Value, Is.SameAs(recovered.Value));
            Assert.Throws<SaveRecoveryRequiredException>(() => store.SaveReddotSnapshotAsync(state.Export(), "confirmed", 0)
                .GetAwaiter().GetResult());
            Assert.That(File.ReadAllText(SnapshotPath(0)), Is.EqualTo("{interrupted"));
            Assert.That(File.ReadAllText(SnapshotPath(0) + ".bak"), Is.EqualTo(first));
        }

        [Test]
        public void CorruptBackupWithoutPrimaryIsFailureNotMissing()
        {
            using var store = new SaveStore(_root);
            store.CreateSaveItem(0);
            Directory.CreateDirectory(Path.GetDirectoryName(SnapshotPath(0)));
            File.WriteAllText(SnapshotPath(0) + ".bak", "{interrupted");
            Assert.Throws<InvalidDataException>(() => store.ReadReddotSnapshotAsync("confirmed", 0)
                .GetAwaiter().GetResult());
            Assert.That(File.Exists(SnapshotPath(0)), Is.False);
            Assert.That(File.ReadAllText(SnapshotPath(0) + ".bak"), Is.EqualTo("{interrupted"));
        }

        [Test]
        public void CancellationAndLockedFileIoRemainVisibleAndDoNotChangeKnownState()
        {
            using var state = new ReddotReadState("slot-0");
            state.MarkRead("mail", "v1");
            using var store = new SaveStore(_root);
            store.CreateSaveItem(0);
            store.SaveReddotSnapshotAsync(state.Export(), "confirmed", 0).GetAwaiter().GetResult();
            var before = File.ReadAllBytes(SnapshotPath(0));
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            Assert.Catch<OperationCanceledException>(() => store.ReadReddotSnapshotAsync("confirmed", 0, cancellation.Token)
                .GetAwaiter().GetResult());
            Assert.Catch<OperationCanceledException>(() => store.SaveReddotSnapshotAsync(state.Export(), "confirmed", 0,
                cancellation.Token).GetAwaiter().GetResult());
            store.ClearCache();
            using (new FileStream(SnapshotPath(0), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                Assert.Throws<IOException>(() => store.ReadReddotSnapshotAsync("confirmed", 0).GetAwaiter().GetResult());
                Assert.Throws<IOException>(() => store.SaveReddotSnapshotAsync(state.Export(), "confirmed", 0)
                    .GetAwaiter().GetResult());
            }
            Assert.That(state.IsRead("mail", "v1"), Is.True);
            Assert.That(File.ReadAllBytes(SnapshotPath(0)), Is.EqualTo(before));
            Assert.That(File.Exists(SnapshotPath(0) + ".bak"), Is.False);
        }

        [Test]
        public void InvalidWritesAndUnknownSlotsNeverCreateSlotsOrObjects()
        {
            using var state = new ReddotReadState("slot-0");
            state.MarkRead("mail", "v1");
            using var store = new SaveStore(_root);
            Assert.Throws<System.Collections.Generic.KeyNotFoundException>(() => store.ReadReddotSnapshotAsync("confirmed", 7)
                .GetAwaiter().GetResult());
            Assert.Throws<System.Collections.Generic.KeyNotFoundException>(() => store.SaveReddotSnapshotAsync(state.Export(),
                "confirmed", 7).GetAwaiter().GetResult());
            Assert.That(store.GetAllSaveItems(), Is.Empty);
            Assert.That(Directory.Exists(Path.Combine(_root, "SaveData")), Is.False);
            store.CreateSaveItem(0);
            var invalid = state.Export();
            invalid.schemaVersion = 0;
            Assert.Throws<InvalidDataException>(() => store.SaveReddotSnapshotAsync(invalid, "confirmed", 0)
                .GetAwaiter().GetResult());
            Assert.Throws<ArgumentException>(() => store.SaveReddotSnapshotAsync(state.Export(), "../confirmed", 0)
                .GetAwaiter().GetResult());
            Assert.That(File.Exists(SnapshotPath(0)), Is.False);
            Assert.That(store.CurrentSaveId, Is.Null);
        }

        [Test]
        public void WorkerThreadSnapshotIoDoesNotImportOrAccessManager()
        {
            using var state = new ReddotReadState("slot-0");
            using var source = state.BindUnreadSource("Mail", "mail", "mail", "v1");
            using var store = new SaveStore(_root);
            store.CreateSaveItem(0);
            var snapshot = new ReddotReadSnapshot
            {
                schemaVersion = 1,
                readNamespace = "other-account",
                records = new[] { new ReddotReadRecord { key = "mail", versions = new[] { "v1" } } }
            };
            Task.Run(() => store.SaveReddotSnapshotAsync(snapshot, "confirmed", 0)).GetAwaiter().GetResult();
            store.ClearCache();
            var result = Task.Run(() => store.ReadReddotSnapshotAsync("confirmed", 0)).GetAwaiter().GetResult();
            Assert.That(result.Value.readNamespace, Is.EqualTo("other-account"));
            Assert.That(state.IsRead("mail", "v1"), Is.False);
            Assert.That(ReddotManager.GetValue("Mail"), Is.EqualTo(1));
            Assert.Throws<InvalidDataException>(() => state.Import(result.Value));
            Assert.That(ReddotManager.GetValue("Mail"), Is.EqualTo(1));
        }

        private string SnapshotPath(int id) => Path.Combine(_root, "SaveData", id.ToString(), "confirmed.json");
    }
}
