using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using YLFramework.Save;

namespace YLFramework.Tests.Extensions.Data.Save
{
    [Category("YL.Module.SaveBcl")]
    public class SaveStoreSafetyTests
    {
        public class Payload
        {
            public int Number;
        }
        public class Model : ISaveModel
        {
            public int Id
            {
                get; set;
            }
            public int Score;
            public string DateText;
        }
        public class StringPayload
        {
            public string Text;
        }

        [Test]
        public void SaveApiDoesNotExposeHistoricalFormatImport()
        {
            Assert.That(typeof(SaveStore).Assembly.GetType("YLFramework.Save.SaveMigration"), Is.Null);
            Assert.That(typeof(SaveManager).GetMethod("ImportArchiveModel"), Is.Null);
        }

        private sealed class BlockingReadConverter : JsonConverter
        {
            public readonly ManualResetEventSlim Entered = new ManualResetEventSlim();
            public readonly ManualResetEventSlim Release = new ManualResetEventSlim();
            public override bool CanConvert(Type type) => type == typeof(Payload);
            public override bool CanWrite => false;
            public override object ReadJson(JsonReader reader, Type type, object existing, JsonSerializer serializer)
            {
                var data = JObject.Load(reader);
                var number = (int)data["Number"];
                if (number == 1)
                {
                    Entered.Set();
                    if (!Release.Wait(10000))
                    {
                        throw new TimeoutException("Test read was not released.");
                    }
                }
                return new Payload { Number = number };
            }
            public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer) => throw new NotSupportedException();
        }

        private sealed class AmbientPoisonConverter : JsonConverter
        {
            public override bool CanConvert(Type type) => true;
            public override object ReadJson(JsonReader reader, Type type, object existing, JsonSerializer serializer) => throw new InvalidOperationException("Ambient converter must not run.");
            public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer) => throw new InvalidOperationException("Ambient converter must not run.");
        }

        private sealed class ThreadReadConverter : JsonConverter
        {
            public int ReadThread;
            public override bool CanConvert(Type type) => type == typeof(Payload);
            public override bool CanWrite => false;
            public override object ReadJson(JsonReader reader, Type type, object existing, JsonSerializer serializer)
            {
                ReadThread = Thread.CurrentThread.ManagedThreadId;
                return new Payload { Number = (int)JObject.Load(reader)["Number"] };
            }
            public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer) => throw new NotSupportedException();
        }

        [Test]
        public void ReadingBackupDoesNotMutatePrimaryOrConsumeBackup()
        {
            Run(root =>
            {
                var path = Path.Combine(root, "data.json");
                File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(path), "{broken");
                File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(path + ".bak"), "{\"Number\":7}");
                var status = SaveFileIO.ReadJson<Payload>(path, new JsonSerializerSettings(), out var value, out _);
                Assert.That(status, Is.EqualTo(SaveReadStatus.RecoveredFromBackup));
                Assert.That(value.Number, Is.EqualTo(7));
                Assert.That(File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(path)), Is.EqualTo("{broken"));
                Assert.That(File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(path + ".bak")), Is.EqualTo("{\"Number\":7}"));
                Assert.That(Directory.GetFiles(global::YLFramework.Testing.PackageTestPaths.Physical(root)).Length, Is.EqualTo(2));
            });
        }

        [Test]
        public void InvalidBackupWithoutPrimaryIsFailureNotMissing()
        {
            Run(root =>
            {
                var path = Path.Combine(root, "data.json");
                File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(path + ".bak"), "not-json");
                var status = SaveFileIO.ReadJson<Payload>(path, new JsonSerializerSettings(), out _, out var error);
                Assert.That(status, Is.EqualTo(SaveReadStatus.Failed));
                Assert.That(error, Is.Not.Null.And.Not.Empty);
                Assert.That(File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(path + ".bak")), Is.EqualTo("not-json"));
            });
        }

        [Test]
        public void LockedPrimaryDoesNotFallBackAndHideIoFailure()
        {
            Run(root =>
            {
                var path = Path.Combine(root, "data.json");
                File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(path), "{\"Number\":8}");
                File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(path + ".bak"), "{\"Number\":7}");
                using (new FileStream(global::YLFramework.Testing.PackageTestPaths.Physical(path), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    Assert.Throws<IOException>(() => SaveFileIO.ReadJson<Payload>(path, new JsonSerializerSettings(), out _, out _));
                }

                Assert.That(File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(path)), Is.EqualTo("{\"Number\":8}"));
                Assert.That(File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(path + ".bak")), Is.EqualTo("{\"Number\":7}"));
            });
        }

        [Test]
        public void UnifiedStoreHasAnExplicitBclRootAndLifecycle()
        {
            var type = typeof(SaveFileIO).Assembly.GetType("YLFramework.Save.SaveStore");
            Assert.That(type, Is.Not.Null, "Missing unified SaveStore, not a second ArchiveManager.");
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.True);
        }

        [Test]
        public void SlotsObjectsSettingsAndCurrentItemSurviveAColdReopen()
        {
            Run(root =>
            {
                using (var store = Open(root))
                {
                    var first = store.CreateSaveItem();
                    var second = store.CreateSaveItem(7);
                    Assert.That((int)first.SaveId, Is.EqualTo(0));
                    Assert.That((int)second.SaveId, Is.EqualTo(7));
                    store.CurrentSaveId = 7;
                    var payload = new Payload { Number = 42 };
                    store.SaveObject(payload, "player", 7);
                    Assert.That((object)store.LoadObject<Payload>("player", 7), Is.SameAs(payload));
                    store.SaveGlobalSetting(new Payload { Number = 9 }, "options");
                }
                using (var store = Open(root))
                {
                    Assert.That((int?)store.CurrentSaveId, Is.EqualTo(7));
                    Assert.That((int)store.LoadObject<Payload>("player", 7).Number, Is.EqualTo(42));
                    Assert.That((int)store.LoadGlobalSetting<Payload>("options").Number, Is.EqualTo(9));
                    Assert.That((int)store.GetAllSaveItems().Count, Is.EqualTo(2));
                    Assert.That(File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(root, "SaveData/7/player.json"))), Does.Contain("42"));
                }
            });
        }

        [Test]
        public void RenamedSaveIdApiKeepsLegacyJsonKeysAcrossColdReopen()
        {
            Assert.That(typeof(SaveStore).GetProperty("CurrentSaveId"), Is.Not.Null);
            Assert.That(typeof(SaveManager.SaveItem).GetProperty("SaveId"), Is.Not.Null);
            Run(root =>
            {
                Directory.CreateDirectory(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(root, "SaveData/7")));
                string path = Path.Combine(root, "SaveData/SaveSystemData.json");
                string json = "{\"DataVersion\":" + SaveStore.FormatVersion +
                    ",\"SaveItemDict\":{\"7\":{\"SaveID\":7,\"CreateTime\":\"2026-10-21T00:00:00Z\",\"LastSaveTime\":\"2026-10-21T01:00:00Z\"}},\"CurrentSaveID\":7}";
                File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(path), json);
                using (var store = Open(root))
                {
                    Assert.That(store.CurrentSaveId, Is.EqualTo(7));
                    Assert.That(store.GetAllSaveItems().Single().SaveId, Is.EqualTo(7));
                    Assert.That(store.RenameSaveItem(7, "renamed"), Is.True);
                }

                var index = JObject.Parse(File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(path)));
                Assert.That((int)index["CurrentSaveID"], Is.EqualTo(7));
                Assert.That(index["CurrentSaveId"], Is.Null);
                Assert.That((int)index["SaveItemDict"]["7"]["SaveID"], Is.EqualTo(7));
                Assert.That(index["SaveItemDict"]["7"]["SaveId"], Is.Null);
                using (var reopened = Open(root))
                {
                    Assert.That(reopened.CurrentSaveId, Is.EqualTo(7));
                    Assert.That(reopened.GetAllSaveItems().Single().DisplayName, Is.EqualTo("renamed"));
                }
            });
        }

        [Test]
        public void LegacyAndFutureIndexesAreNeverAutoConvertedOrReplaced()
        {
            Run(root =>
            {
                Directory.CreateDirectory(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(root, "SaveData")));
                var path = Path.Combine(root, "SaveData/SaveSystemData.json");
                foreach (var version in new[] { 1, 999 })
                {
                    var json = "{\"DataVersion\":" + version + ",\"SaveItemDict\":{}}";
                    File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(path), json);
                    var error = Assert.Catch(() =>
                    {
                        using (Open(root))
                        {
                        }
                    });
                    Assert.That(error, Is.Not.Null);
                    Assert.That(File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(path)), Is.EqualTo(json));
                    Assert.That(Directory.GetFiles(global::YLFramework.Testing.PackageTestPaths.Physical(Path.GetDirectoryName(path))).Length, Is.EqualTo(1));
                }
            });
        }

        [Test]
        public void FailedWriteLeavesObjectCacheAndDiskAtPreviousCommit()
        {
            Run(root =>
            {
                using (var store = Open(root))
                {
                    store.CreateSaveItem();
                    var previous = new Payload { Number = 3 };
                    store.SaveObject(previous, "player", 0);
                    var path = Path.Combine(root, "SaveData/0/player.json");
                    var before = File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(path));
                    using (new FileStream(global::YLFramework.Testing.PackageTestPaths.Physical(path), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    {
                        Assert.Catch<IOException>(() => store.SaveObject(new Payload { Number = 99 }, "player", 0));
                    }

                    Assert.That(File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(path)), Is.EqualTo(before));
                    Assert.That((object)store.LoadObject<Payload>("player", 0), Is.SameAs(previous));
                    Assert.That(Directory.GetFiles(global::YLFramework.Testing.PackageTestPaths.Physical(Path.GetDirectoryName(path)), "*.tmp-*").Length, Is.EqualTo(0));
                }
            });
        }

        [Test]
        public void PreCanceledWriteDoesNotCreateAnObjectOrAdvanceCache()
        {
            Run(root =>
            {
                using (var store = Open(root))
                using (var canceled = new CancellationTokenSource())
                {
                    store.CreateSaveItem();
                    canceled.Cancel();
                    Assert.Catch<OperationCanceledException>(() => ((Task)store.SaveObjectAsync(new Payload { Number = 9 }, "player", 0, canceled.Token)).GetAwaiter().GetResult());
                    Assert.That((bool)store.HasObject("player", 0), Is.False);
                    Assert.That((object)store.LoadObject<Payload>("player", 0), Is.Null);
                }
            });
        }

        [Test]
        public void RootOwnershipRejectsCaseAliasesAndReleasesAfterDispose()
        {
            Run(root =>
            {
                using (var store = Open(root))
                {
                    Assert.Catch<IOException>(() =>
                    {
                        using (Open(root.ToUpperInvariant()))
                        {
                        }
                    });
                }

                using (var reopened = Open(root))
                {
                    Assert.That(reopened.GetAllSaveItems().Count, Is.EqualTo(0));
                }
            });
        }

        [Test]
        public void ConcurrentWritesAcrossAndWithinSlotsRemainCompleteJson()
        {
            Run(root =>
            {
                using (var store = Open(root))
                {
                    store.CreateSaveItem(0);
                    store.CreateSaveItem(1);
                    var writes = Enumerable.Range(0, 40).Select(i => (Task)store.SaveObjectAsync(new Payload { Number = i }, "player", i % 2, CancellationToken.None)).ToArray();
                    Task.WaitAll(writes);
                }
                using (var store = Open(root))
                {
                    Assert.That((int)store.LoadObject<Payload>("player", 0).Number, Is.InRange(0, 38));
                    Assert.That((int)store.LoadObject<Payload>("player", 1).Number, Is.InRange(1, 39));
                    Assert.That(Directory.GetFiles(global::YLFramework.Testing.PackageTestPaths.Physical(root), "*.tmp-*", SearchOption.AllDirectories).Length, Is.EqualTo(0));
                }
            });
        }

        [Test]
        public void ABlockedSlotDoesNotBlockSavingAnotherSlot()
        {
            Run(root =>
            {
                var converter = new BlockingReadConverter();
                var settings = new JsonSerializerSettings();
                settings.Converters.Add(converter);
                using (var store = Open(root, settings))
                {
                    store.CreateSaveItem(0);
                    store.CreateSaveItem(1);
                    store.SaveObject(new Payload { Number = 1 }, "player", 0);
                    store.ClearCache();
                    var blocked = (Task<Payload>)store.LoadObjectAsync<Payload>("player", 0, CancellationToken.None);
                    Assert.That(converter.Entered.Wait(3000), Is.True);
                    var independent = Task.Run(() =>
                    {
                        store.SaveObject(new Payload { Number = 2 }, "player", 1);
                    });
                    bool progressed;
                    try
                    {
                        progressed = independent.Wait(2000);
                    }
                    finally
                    {
                        converter.Release.Set();
                    }
                    Task.WaitAll(blocked, independent);
                    Assert.That(progressed, Is.True, "A different slot should not wait for this slot's converter.");
                    Assert.That((int)store.LoadObject<Payload>("player", 1).Number, Is.EqualTo(2));
                }
            });
        }

        [Test]
        public void DisposeCancelsQueuedOperationsAndWaitsForActiveIoBeforeReleasingOwnership()
        {
            Run(root =>
            {
                var converter = new BlockingReadConverter();
                var settings = new JsonSerializerSettings();
                settings.Converters.Add(converter);
                var store = Open(root, settings);
                try
                {
                    store.CreateSaveItem();
                    store.SaveObject(new Payload { Number = 1 }, "player", 0);
                    store.ClearCache();
                    var blocked = (Task<Payload>)store.LoadObjectAsync<Payload>("player", 0, CancellationToken.None);
                    Assert.That(converter.Entered.Wait(3000), Is.True);
                    var queued = (Task)store.DeleteSaveItemAsync(0, CancellationToken.None);
                    var closing = Task.Run(() => ((IDisposable)store).Dispose());
                    Task secondClosing = null;
                    try
                    {
                        Assert.That(SpinWait.SpinUntil(() => queued.IsCompleted, 3000), Is.True);
                        Assert.That(queued.IsCanceled, Is.True);
                        Assert.That(closing.IsCompleted, Is.False);
                        using (var secondEntered = new ManualResetEventSlim())
                        {
                            secondClosing = Task.Run(() =>
                            {
                                secondEntered.Set();
                                store.Dispose();
                            });
                            Assert.That(secondEntered.Wait(3000), Is.True);
                            Assert.That(secondClosing.Wait(200), Is.False, "Every Dispose caller must wait for the owner to finish, not only the first caller.");
                        }
                        Assert.Catch<IOException>(() =>
                        {
                            using (Open(root))
                            {
                            }
                        });
                    }
                    finally
                    {
                        converter.Release.Set();
                    }
                    Assert.Catch<OperationCanceledException>(() => blocked.GetAwaiter().GetResult());
                    closing.GetAwaiter().GetResult();
                    secondClosing.GetAwaiter().GetResult();
                    Assert.Catch<ObjectDisposedException>(() => store.GetAllSaveItems());
                    using (var reopened = Open(root))
                    {
                        Assert.That((bool)reopened.HasSaveItem(0), Is.True);
                        Assert.That((int)reopened.LoadObject<Payload>("player", 0).Number, Is.EqualTo(1));
                    }
                }
                finally
                {
                    converter.Release.Set();
                    ((IDisposable)store).Dispose();
                }
            });
        }

        [Test]
        public void BackupIndexCanBeReadButNeedsExplicitRepairBeforeMutation()
        {
            Run(root =>
            {
                using (var store = Open(root))
                {
                    store.CreateSaveItem();
                    store.SaveObject(new Payload { Number = 5 }, "player", 0);
                    store.RenameSaveItem(0, "new");
                }
                var path = Path.Combine(root, "SaveData/SaveSystemData.json");
                File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(path), "damaged-index");
                var backup = File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(path + ".bak"));
                using (var store = Open(root))
                {
                    Assert.That((int)store.LoadObject<Payload>("player", 0).Number, Is.EqualTo(5));
                    var failure = Assert.Catch(() => store.CreateSaveItem());
                    Assert.That(failure.GetType().Name, Is.EqualTo("SaveRecoveryRequiredException"));
                    Assert.That(File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(path)), Is.EqualTo("damaged-index"));
                    var preserved = (string)store.RestoreIndexBackup();
                    Assert.That(File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(preserved)), Is.EqualTo("damaged-index"));
                    Assert.That(File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(path + ".bak")), Is.EqualTo(backup));
                    Assert.That((bool)store.RenameSaveItem(0, "repaired"), Is.True);
                }
            });
        }

        [Test]
        public void ExplicitObjectRepairPreservesDamagedPrimaryAndTheValidBackup()
        {
            Run(root =>
            {
                using (var store = Open(root))
                {
                    store.CreateSaveItem();
                    store.SaveObject(new Payload { Number = 5 }, "player", 0);
                    store.SaveObject(new Payload { Number = 6 }, "player", 0);
                }
                var path = Path.Combine(root, "SaveData/0/player.json");
                File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(path), "damaged-object");
                var backup = File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(path + ".bak"));
                using (var store = Open(root))
                {
                    Assert.That((int)store.LoadObject<Payload>("player", 0).Number, Is.EqualTo(5));
                    Assert.That(Assert.Catch(() => store.SaveObject(new Payload { Number = 7 }, "player", 0)).GetType().Name, Is.EqualTo("SaveRecoveryRequiredException"));
                    var preserved = (string)store.RestoreObjectBackup("player", 0);
                    Assert.That(File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(preserved)), Is.EqualTo("damaged-object"));
                    Assert.That(File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(path)), Is.EqualTo(backup));
                    Assert.That(File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(path + ".bak")), Is.EqualTo(backup));
                    store.SaveObject(new Payload { Number = 8 }, "player", 0);
                    Assert.That((int)store.LoadObject<Payload>("player", 0).Number, Is.EqualTo(8));
                }
            });
        }

        [Test]
        public void InvalidSettingsAndMissingIndexWithOrphansAreNotReplacedWithDefaults()
        {
            Run(root =>
            {
                using (var store = Open(root))
                {
                    Directory.CreateDirectory(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(root, "Setting")));
                    var path = Path.Combine(root, "Setting/options.json");
                    File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(path), "bad-settings");
                    Assert.Catch<InvalidDataException>(() => store.LoadGlobalSetting<Payload>("options"));
                    Assert.That(File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(path)), Is.EqualTo("bad-settings"));
                }
                Directory.CreateDirectory(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(root, "SaveData/3")));
                File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(root, "SaveData/3/player.json")), "{\"Number\":3}");
                Assert.Catch<InvalidDataException>(() =>
                {
                    using (Open(root))
                    {
                    }
                });
                Assert.That(File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(root, "SaveData/SaveSystemData.json"))), Is.False);
                Assert.That(File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(root, "SaveData/3/player.json"))), Is.EqualTo("{\"Number\":3}"));
            });
        }

        [Test]
        public void IndexFailureRollsBackCreateRenameAndDeleteWithoutAdvancingMemory()
        {
            Run(root =>
            {
                using (var store = Open(root))
                {
                    var item = store.CreateSaveItem();
                    var value = new Payload { Number = 17 };
                    store.SaveObject(value, "player", 0);
                    var path = Path.Combine(root, "SaveData/SaveSystemData.json");
                    var bytes = File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(path));
                    using (new FileStream(global::YLFramework.Testing.PackageTestPaths.Physical(path), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    {
                        Assert.Catch<IOException>(() => store.CreateSaveItem());
                        Assert.Catch<IOException>(() => store.RenameSaveItem(0, "failed"));
                        Assert.Catch<IOException>(() => store.DeleteSaveItem(0));
                    }
                    Assert.That(File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(path)), Is.EqualTo(bytes));
                    Assert.That((string)item.DisplayName, Is.Null);
                    Assert.That((int)store.GetAllSaveItems().Count, Is.EqualTo(1));
                    Assert.That((object)store.LoadObject<Payload>("player", 0), Is.SameAs(value));
                    Assert.That(File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(root, "SaveData/0/player.json"))), Is.True);
                }
            });
        }

        [Test]
        public void ModelIdsSelectionAndDeletionUseTheSameSlotsAndCache()
        {
            Run(root =>
            {
                using (var store = Open(root))
                {
                    var model = (Model)store.CreateModel<Model>(5);
                    model.Score = 20;
                    store.SaveModel(model);
                    store.CurrentSaveId = 5;
                    Assert.That((object)store.LoadModel<Model>(), Is.SameAs(model));
                    Assert.That((object)store.LoadObject<Model>(SaveStore.ModelFileName, 5), Is.SameAs(model));
                    Assert.Catch<IOException>(() => store.CreateModel<Model>(5));
                }
                using (var store = Open(root))
                {
                    Assert.That((int)store.LoadModel<Model>().Id, Is.EqualTo(5));
                    Assert.That((int)store.LoadModel<Model>().Score, Is.EqualTo(20));
                    store.DeleteSaveItem(5);
                    Assert.That((int?)store.CurrentSaveId, Is.Null);
                    Assert.That((bool)store.HasSaveItem(5), Is.False);
                    Assert.That(Directory.GetFiles(global::YLFramework.Testing.PackageTestPaths.Physical(root), "ArchiveModel.json", SearchOption.AllDirectories).Length, Is.EqualTo(1), "Deleted content remains recoverable, outside the live slot.");
                }
            });
        }

        [Test]
        public void UnsafeNamesAndNullObjectsNeverWriteOutsideTheirSlot()
        {
            Run(root =>
            {
                using (var store = Open(root))
                {
                    store.CreateSaveItem();
                    foreach (var name in new[] { "..", "../escape", "a/b", "a\\b", "CON", "nul.json", "tail.", "tail ", "a:b", "x?y", "" })
                    {
                        Assert.Catch<ArgumentException>(() => store.SaveObject(new Payload(), name, 0));
                    }

                    Assert.Catch<ArgumentNullException>(() => store.SaveObject(null, "valid", 0));
                    Assert.That((int)store.GetObjectNames(0).Count, Is.EqualTo(0));
                    Assert.That(File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(root, "escape.json"))), Is.False);
                }
            });
        }

        [Test]
        public void DuplicateIndexPropertiesCannotSilentlyDiscardSlots()
        {
            Run(root =>
            {
                Directory.CreateDirectory(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(root, "SaveData")));
                var path = Path.Combine(root, "SaveData/SaveSystemData.json");
                const string Json = "{\"DataVersion\":2,\"SaveItemDict\":{},\"SaveItemDict\":{}}";
                File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(path), Json);
                Assert.Catch<InvalidDataException>(() =>
                {
                    using (Open(root))
                    {
                    }
                });
                Assert.That(File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(path)), Is.EqualTo(Json));
            });
        }

        [Test]
        public void AmbientJsonDefaultsCannotCorruptTheIndexOrImplicitlyInstantiateTypes()
        {
            Run(root =>
            {
                var original = JsonConvert.DefaultSettings;
                try
                {
                    JsonConvert.DefaultSettings = () => new JsonSerializerSettings { TypeNameHandling = TypeNameHandling.All, Converters = { new AmbientPoisonConverter() } };
                    using (var store = Open(root))
                    {
                        store.CreateSaveItem();
                        store.SaveObject(new Payload { Number = 4 }, "player", 0);
                    }
                    using (var store = Open(root))
                    {
                        Assert.That(store.LoadObject<Payload>("player", 0).Number, Is.EqualTo(4));
                    }

                    Assert.That(File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(root, "SaveData/SaveSystemData.json"))), Does.Not.Contain("$type"));
                }
                finally
                {
                    JsonConvert.DefaultSettings = original;
                }
            });
        }

        [Test]
        public void SynchronousReadKeepsConvertersOnTheCallingThread()
        {
            Run(root =>
            {
                var converter = new ThreadReadConverter();
                var settings = new JsonSerializerSettings();
                settings.Converters.Add(converter);
                using (var store = Open(root, settings))
                {
                    store.CreateSaveItem();
                    store.SaveObject(new Payload { Number = 8 }, "player", 0);
                    store.ClearCache();
                    var caller = Thread.CurrentThread.ManagedThreadId;
                    Assert.That((int)store.LoadObject<Payload>("player", 0).Number, Is.EqualTo(8));
                    Assert.That(converter.ReadThread, Is.EqualTo(caller));
                }
            });
        }

        [Test]
        public void DateLikeStringsRoundTripWithoutTimezoneOrLexicalConversion()
        {
            Run(root =>
            {
                const string Text = "2026-01-02T03:04:05+03:00";
                using (var store = Open(root))
                {
                    store.CreateSaveItem();
                    store.SaveObject(new StringPayload { Text = Text }, "player", 0);
                    store.ClearCache();
                    Assert.That((string)store.LoadObject<StringPayload>("player", 0).Text, Is.EqualTo(Text));
                }
            });
        }

        [Test]
        public void DirectoryAtAnObjectPathIsAnIoErrorNotMissingData()
        {
            Run(root =>
            {
                using (var store = Open(root))
                {
                    store.CreateSaveItem();
                    Directory.CreateDirectory(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(root, "SaveData/0/player.json")));
                    Assert.Catch<UnauthorizedAccessException>(() => store.LoadObject<Payload>("player", 0));
                }
            });
        }

        [Test]
        public void BackupProvenanceRemainsVisibleAfterCacheHits()
        {
            Run(root =>
            {
                using (var store = Open(root))
                {
                    store.CreateSaveItem();
                    store.SaveObject(new Payload { Number = 5 }, "player", 0);
                    store.SaveObject(new Payload { Number = 6 }, "player", 0);
                }
                File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(root, "SaveData/0/player.json")), "bad-primary");
                using (var store = Open(root))
                {
                    var first = store.ReadObject<Payload>("player", 0);
                    var second = store.ReadObject<Payload>("player", 0);
                    Assert.That(first.Status, Is.EqualTo(SaveReadStatus.RecoveredFromBackup));
                    Assert.That(second.Status, Is.EqualTo(SaveReadStatus.RecoveredFromBackup));
                    Assert.That(second.IsCached, Is.True);
                    Assert.That(first.Value, Is.SameAs(second.Value));
                    Assert.That(File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(root, "SaveData/0/player.json"))), Is.EqualTo("bad-primary"));
                }
            });
        }

        [Test]
        public void CancelingAQueuedSaveNeverOverwritesItsPreviousCommit()
        {
            Run(root =>
            {
                var converter = new BlockingReadConverter();
                var settings = new JsonSerializerSettings();
                settings.Converters.Add(converter);
                using (var store = Open(root, settings))
                using (var cancellation = new CancellationTokenSource())
                {
                    store.CreateSaveItem();
                    store.SaveObject(new Payload { Number = 1 }, "player", 0);
                    store.ClearCache();
                    var blocked = store.LoadObjectAsync<Payload>("player", 0);
                    Assert.That(converter.Entered.Wait(3000), Is.True);
                    var queued = store.SaveObjectAsync(new Payload { Number = 99 }, "player", 0, cancellation.Token);
                    cancellation.Cancel();
                    try
                    {
                        Assert.Catch<OperationCanceledException>(() => queued.GetAwaiter().GetResult());
                    }
                    finally
                    {
                        converter.Release.Set();
                    }
                    Assert.That(blocked.GetAwaiter().GetResult().Number, Is.EqualTo(1));
                    Assert.That(store.LoadObject<Payload>("player", 0).Number, Is.EqualTo(1));
                    Assert.That(JObject.Parse(File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(root, "SaveData/0/player.json"))))["Number"].Value<int>(), Is.EqualTo(1));
                }
            });
        }

        [Test]
        public void ModelCreationFailureKeepsTheIndexAndOriginalSlotsUnchanged()
        {
            Run(root =>
            {
                using (var store = Open(root))
                {
                    store.CreateSaveItem();
                    var path = Path.Combine(root, "SaveData/SaveSystemData.json");
                    var before = File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(path));
                    using (new FileStream(global::YLFramework.Testing.PackageTestPaths.Physical(path), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    {
                        Assert.Catch<IOException>(() => store.CreateModel<Model>(9));
                    }

                    Assert.That(File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(path)), Is.EqualTo(before));
                    Assert.That(store.HasSaveItem(9), Is.False);
                    Assert.That(Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(root, "SaveData/9"))), Is.False);
                    Assert.That(Directory.GetFiles(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(root, "SaveData")), "ArchiveModel.json", SearchOption.AllDirectories).Length, Is.EqualTo(1), "Failed staged model must be retained.");
                }
            });
        }

        [Test]
        public void AsyncModelCreationSavingAndLoadAllUseStableIdsAndOneCache()
        {
            Run(root =>
            {
                using (var store = Open(root))
                {
                    store.CreateSaveItem(0);
                    var last = store.CreateModelAsync<Model>(9).GetAwaiter().GetResult();
                    last.Score = 90;
                    store.SaveModelAsync(last).GetAwaiter().GetResult();
                    var first = store.CreateModelAsync<Model>(3).GetAwaiter().GetResult();
                    first.Score = 30;
                    store.SaveModelAsync(first).GetAwaiter().GetResult();
                    store.ClearCache();
                    var all = store.LoadAllModelsAsync<Model>().GetAwaiter().GetResult();
                    Assert.That(all.Select(model => model.Id).ToArray(), Is.EqualTo(new[] { 3, 9 }));
                    Assert.That(all.Select(model => model.Score).ToArray(), Is.EqualTo(new[] { 30, 90 }));
                    Assert.That(store.LoadObject<Model>(SaveStore.ModelFileName, 3), Is.SameAs(all[0]));
                    Assert.That(store.CurrentSaveId, Is.Null);
                }
            });
        }

        [Test]
        public void CancellationDuringRealTemporaryFileIoHasOnlyOldOrFullyCommittedState()
        {
            Run(root =>
            {
                using (var store = Open(root))
                using (var cancellation = new CancellationTokenSource())
                using (var observed = new ManualResetEventSlim())
                {
                    store.CreateSaveItem();
                    var previous = new StringPayload { Text = "old" };
                    store.SaveObject(previous, "player", 0);
                    var path = Path.Combine(root, "SaveData/0/player.json");
                    var before = File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(path));
                    var replacement = new StringPayload { Text = new string('x', 16 * 1024 * 1024) };
                    using (var stop = new ManualResetEventSlim())
                    using (var ready = new ManualResetEventSlim())
                    {
                        // Unity Mono使用DefaultWatcher轮询后端，会漏掉快速创建并移动的tmp；直接观测真实文件而非依赖事件。
                        Exception observerError = null;
                        var observer = new Thread(() =>
                        {
                            ready.Set();
                            try
                            {
                                while (!stop.IsSet)
                                {
                                    if (Directory.EnumerateFiles(global::YLFramework.Testing.PackageTestPaths.Physical(Path.GetDirectoryName(path)), "player.json.tmp-*").Any())
                                    {
                                        cancellation.Cancel();
                                        observed.Set();
                                        return;
                                    }
                                    Thread.Yield();
                                }
                            }
                            catch (Exception error)
                            {
                                observerError = error;
                            }
                        })
                        {
                            IsBackground = true,
                            Name = "Save temporary-file cancellation observer"
                        };
                        observer.Start();
                        var committed = false;
                        try
                        {
                            Assert.That(ready.Wait(5000), Is.True);
                            try
                            {
                                store.SaveObjectAsync(replacement, "player", 0, cancellation.Token).GetAwaiter().GetResult();
                                committed = true;
                            }
                            catch (OperationCanceledException) { }
                        }
                        finally
                        {
                            stop.Set();
                            Assert.That(observer.Join(5000), Is.True);
                        }
                        Assert.That(observerError, Is.Null);
                        Assert.That(observed.Wait(3000), Is.True, "Cancellation must be triggered by a real temporary file, not just before dispatch.");
                        if (committed)
                        {
                            Assert.That(JObject.Parse(File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(path)))["Text"].Value<string>().Length, Is.EqualTo(16 * 1024 * 1024));
                            Assert.That(store.LoadObject<StringPayload>("player", 0), Is.SameAs(replacement));
                            Assert.That(File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(path + ".bak")), Is.EqualTo(before));
                        }
                        else
                        {
                            Assert.That(File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(path)), Is.EqualTo(before));
                            Assert.That(store.LoadObject<StringPayload>("player", 0), Is.SameAs(previous));
                        }
                        Assert.That(Directory.GetFiles(global::YLFramework.Testing.PackageTestPaths.Physical(Path.GetDirectoryName(path)), "*.tmp-*").Length, Is.EqualTo(0));
                    }
                }
            });
        }

        [Test]
        public void AtomicReplacementFailureKeepsThePrimaryCacheAndBackupDestinationIntact()
        {
            Run(root =>
            {
                using (var store = Open(root))
                {
                    store.CreateSaveItem();
                    var previous = new Payload { Number = 1 };
                    store.SaveObject(previous, "player", 0);
                    var path = Path.Combine(root, "SaveData/0/player.json");
                    var before = File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(path));
                    Directory.CreateDirectory(global::YLFramework.Testing.PackageTestPaths.Physical(path + ".bak"));
                    var error = Assert.Catch(() => store.SaveObject(new Payload { Number = 99 }, "player", 0));
                    Assert.That(error is IOException || error is UnauthorizedAccessException, Is.True);
                    Assert.That(File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(path)), Is.EqualTo(before));
                    Assert.That(store.LoadObject<Payload>("player", 0), Is.SameAs(previous));
                    Assert.That(Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(path + ".bak")), Is.True);
                    Assert.That(Directory.GetFiles(global::YLFramework.Testing.PackageTestPaths.Physical(Path.GetDirectoryName(path)), "*.tmp-*").Length, Is.EqualTo(0));
                }
            });
        }

        [Test]
        public void CurrentModelsPreserveStringValuesAndRejectWrongIdsOnLoad()
        {
            Run(root =>
            {
                using (var store = Open(root))
                {
                    var model = store.CreateModel<Model>(3);
                    model.DateText = "2026-01-02T03:04:05+03:00";
                    store.SaveModel(model);
                    store.ClearCache();
                    Assert.That(store.LoadModel<Model>(3).DateText, Is.EqualTo("2026-01-02T03:04:05+03:00"));
                    File.WriteAllText(global::YLFramework.Testing.PackageTestPaths.Physical(Path.Combine(root, "SaveData/3/" + SaveStore.ModelFileName + ".json")), "{\"Id\":99,\"Score\":1}");
                    store.ClearCache();
                    Assert.Catch<InvalidDataException>(() => store.LoadModel<Model>(3));
                }
            });
        }

        private static SaveStore Open(string root, JsonSerializerSettings settings = null)
        {
            return new SaveStore(root, settings, Formatting.Indented);
        }

        private static void Run(Action<string> check)
        {
            var output = Environment.GetEnvironmentVariable("YL_SAVE_TEST_OUTPUT") ??
                Path.Combine(Directory.GetCurrentDirectory(), "Temp", "Extensions.Data.Save");
            var root = Path.Combine(output, "cases", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(global::YLFramework.Testing.PackageTestPaths.Physical(root));
            try
            {
                check(root);
                Directory.Delete(global::YLFramework.Testing.PackageTestPaths.Physical(root), true);
            }
            catch
            {
                Console.Error.WriteLine("Preserved failed Save fixture: " + root);
                throw;
            }
        }
    }
}
