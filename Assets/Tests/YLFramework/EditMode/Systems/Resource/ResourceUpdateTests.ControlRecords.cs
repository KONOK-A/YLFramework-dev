using System;
using System.Collections;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using YLFramework.Resource;

namespace YLFramework.Tests.ResourceBuild
{
    public partial class ResourceUpdateTests
    {
        [TestCase("prepared", "Releases")]
        [TestCase("commit-journal.json", "ReleaseFloorsBefore")]
        [TestCase("commit-journal.json", "ReleaseFloorsAfter")]
        [TestCase("active.json", "ReleaseFloors")]
        [TestCase("last-known-good.json", "ReleaseFloors")]
        public void UnsignedLocalControlRecordsWriteVersionTwoAndExplicitEmptyCollections(string slot, string collection)
        {
            var store = NewStore();
            var prepared = Prepare(store);
            store.Checkpoint = point =>
            {
                if (point == "BeforeCommitDecision")
                {
                    throw new IOException("interrupted before the commit decision");
                }
            };
            Assert.Throws<IOException>(() => store.Commit(prepared));
            var path = slot == "prepared"
                ? Directory.GetFiles(ProjectPath(store, "prepared"), "*.json").Single()
                : ProjectPath(store, slot);
            var record = ResourceCatalogCodec.ParseObject(File.ReadAllBytes(path));
            Assert.That((long)record["Version"], Is.EqualTo(2), path);
            Assert.That(record[collection], Is.TypeOf<JArray>(), path + ":" + collection);
            Assert.That(((JArray)record[collection]).Count, Is.Zero);
            Assert.That(Store(store.RootPath).Mount().Single().Document.CatalogDigest,
                Is.EqualTo(Snapshot().Document.CatalogDigest));
        }

        [TestCase("active.json", 1)]
        [TestCase("active.json", 3)]
        [TestCase("last-known-good.json", 1)]
        [TestCase("last-known-good.json", 3)]
        [TestCase("prepared", 1)]
        [TestCase("prepared", 3)]
        [TestCase("prepared.next", 1)]
        [TestCase("prepared.next", 3)]
        [TestCase("commit-journal.json", 1)]
        [TestCase("commit-journal.json", 3)]
        [TestCase("journal.ActiveBefore", 1)]
        [TestCase("journal.GoodBefore", 3)]
        [TestCase("active.json.next", 1)]
        [TestCase("last-known-good.json.next", 3)]
        public void UnsupportedControlRecordBlocksTheRootBeforeAnyRecoveryWrite(string slot, int version)
        {
            var store = NewStore();
            store.Commit(Prepare(store));
            var prepared = Prepare(store, 3);
            var journal = slot.StartsWith("journal.", StringComparison.Ordinal) || slot == "commit-journal.json";
            if (journal)
            {
                store.Checkpoint = point =>
                {
                    if (point == "BeforeCommitDecision")
                    {
                        throw new IOException("interrupted second commit");
                    }
                };
                Assert.Throws<IOException>(() => store.Commit(prepared));
            }

            var preparedPath = Directory.GetFiles(ProjectPath(store, "prepared"), "*.json").Single();
            var path = slot.StartsWith("prepared", StringComparison.Ordinal) ? preparedPath
                : journal ? ProjectPath(store, "commit-journal.json")
                : ProjectPath(store, slot.EndsWith(".next", StringComparison.Ordinal) ? slot.Substring(0, slot.Length - 5) : slot);
            var record = ResourceCatalogCodec.ParseObject(File.ReadAllBytes(path));
            var changed = slot.StartsWith("journal.", StringComparison.Ordinal) ? (JObject)record[slot.Substring(8)] : record;
            changed["Version"] = version;
            if (version == 1)
            {
                foreach (var field in new[] { "ReleaseFloors", "ReleaseFloorsBefore", "ReleaseFloorsAfter", "Releases" })
                {
                    changed.Remove(field);
                }
            }
            else
            {
                changed["FutureControlField"] = new JObject { ["ReleaseFloors"] = "must not be interpreted by this reader" };
            }
            if (slot.EndsWith(".next", StringComparison.Ordinal))
            {
                path += ".next";
            }

            File.WriteAllBytes(path, ResourceCatalogCodec.CanonicalBytes(record));
            var truncated = ProjectPath(store, slot == "last-known-good.json.next" ? "active.json.next" : "last-known-good.json.next");
            File.WriteAllText(truncated, "{\"Version\":");
            var lockPath = Path.Combine(store.RootPath, ".ylr-store.lock");
            File.Delete(lockPath);
            var before = ResourceReleaseFixture.Snapshot(store.RootPath);
            var reopened = Store(store.RootPath);
            var error = Assert.Throws<ResourceException>(() => reopened.Mount());
            Assert.That(error.Kind, Is.EqualTo(ResourceError.Unsupported));
            Assert.That(reopened.RecoveryNotes, Is.Empty, "An unsupported protocol is not damaged content or a fallback reason.");
            Assert.That(File.Exists(lockPath), Is.False, "Reject incompatible metadata before creating a write lock.");
            CollectionAssert.AreEqual(before, ResourceReleaseFixture.Snapshot(store.RootPath));
        }

        [TestCase("Check")]
        [TestCase("Prepare")]
        [TestCase("Cleanup")]
        [TestCase("GetPreparedUpdates")]
        public void UnsupportedIndexCannotBeBypassedByAnotherStoreEntry(string operation)
        {
            var store = NewStore();
            store.Commit(Prepare(store));
            var current = store.Mount();
            var plan = store.Check(current, new[] { Snapshot("a", 3) });
            var path = ProjectPath(store);
            var index = ResourceCatalogCodec.ParseObject(File.ReadAllBytes(path));
            index["Version"] = 3;
            File.WriteAllBytes(path, ResourceCatalogCodec.CanonicalBytes(index));
            var before = ResourceReleaseFixture.Snapshot(store.RootPath);
            Action action = operation switch
            {
                "Check" => () => store.Check(current, new[] { Snapshot("a", 3) }),
                "Prepare" => () => store.Prepare(plan, default),
                "Cleanup" => () => store.Cleanup(current),
                _ => () => store.GetPreparedUpdates()
            };
            var error = Assert.Throws<ResourceException>(() => action());
            Assert.That(error.Kind, Is.EqualTo(ResourceError.Unsupported));
            CollectionAssert.AreEqual(before, ResourceReleaseFixture.Snapshot(store.RootPath));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void UnreadablePersistentFloorHistoryBlocksOnlyThatRootWithoutReplacingItWithAnEmptyHistory(bool truncated)
        {
            var store = NewStore();
            store.Commit(Prepare(store));
            foreach (var name in new[] { "active.json", "last-known-good.json" })
            {
                var path = ProjectPath(store, name);
                if (truncated)
                {
                    File.WriteAllText(path, "{\"Version\":");
                }
                else
                {
                    var record = ResourceCatalogCodec.ParseObject(File.ReadAllBytes(path));
                    record["Version"] = 2;
                    record["ReleaseFloors"] = null;
                    File.WriteAllBytes(path, ResourceCatalogCodec.CanonicalBytes(record));
                }
            }
            var before = ResourceReleaseFixture.Snapshot(store.RootPath);
            Assert.Throws<InvalidDataException>(() => Store(store.RootPath).Mount());
            CollectionAssert.AreEqual(before, ResourceReleaseFixture.Snapshot(store.RootPath));
            var independent = NewStore();
            independent.Commit(Prepare(independent));
            Assert.That(independent.Mount().Single().Document.CatalogDigest, Is.EqualTo(Snapshot("a", 2).Document.CatalogDigest));
            CollectionAssert.AreEqual(before, ResourceReleaseFixture.Snapshot(store.RootPath));
        }

        [UnityTest]
        public IEnumerator RuntimeInitializationReportsUnsupportedInsteadOfSilentlyMountingLastKnownGood()
        {
            yield return new EnterPlayMode();
            var store = NewStore();
            store.Commit(Prepare(store));
            var path = ProjectPath(store);
            var index = ResourceCatalogCodec.ParseObject(File.ReadAllBytes(path));
            index["Version"] = 3;
            File.WriteAllBytes(path, ResourceCatalogCodec.CanonicalBytes(index));
            var before = ResourceReleaseFixture.Snapshot(store.RootPath);
            using var initialized = ResourceSystem.InitializeAsync(new ResourceConfiguration(Store(store.RootPath)));
            yield return Wait(initialized);
            Assert.That(initialized.Status, Is.EqualTo(ResourceRequestStatus.Failed));
            Assert.That(initialized.Error.Kind, Is.EqualTo(ResourceError.Unsupported));
            Assert.That(ResourceSystem.Current, Is.Null);
            Assert.That(AssetBundle.GetAllLoadedAssetBundles(), Is.Empty);
            CollectionAssert.AreEqual(before, ResourceReleaseFixture.Snapshot(store.RootPath));
            yield return new ExitPlayMode();
        }
    }
}
