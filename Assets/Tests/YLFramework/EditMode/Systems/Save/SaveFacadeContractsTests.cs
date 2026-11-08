using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using NUnit.Framework;
using YLFramework.Save;

namespace YLFramework.Tests.SavePackage
{
    [Category("YL.Module.SaveBcl")]
#if !UNITY_EDITOR
    [NonParallelizable]
#endif
    public class SaveFacadeContractsTests
    {
        public sealed class Payload
        {
            public int Number;
        }

        private string _root;
        private string _previous;

        [SetUp]
        public void CreateExplicitOwner()
        {
            var type = typeof(SaveManager);
            Assert.That(type.GetField("_store", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null), Is.Null,
                "Never detach or replace another store owner to make a test pass.");
            _previous = (string)type.GetField("_storageRootOverride", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
            string output = Environment.GetEnvironmentVariable("YL_SAVE_TEST_OUTPUT") ?? Path.Combine(Directory.GetCurrentDirectory(), "Temp/SavePackage");
            _root = Path.Combine(output, Guid.NewGuid().ToString("N"));
            Assert.That(Directory.Exists(_root), Is.False);
            Directory.CreateDirectory(_root);
            SaveManager.SetStorageRoot(_root);
        }

        [Test]
        public void DefaultStoreIsPubliclyBorrowedAndManagerHasNoSecondBusinessSurface()
        {
            var type = typeof(SaveManager);
            var property = type.GetProperty("Store", BindingFlags.Public | BindingFlags.Static);
            Assert.That(property, Is.Not.Null, "The one default Store must be explicitly borrowable without a business forwarding layer.");
            Assert.That(property.PropertyType, Is.EqualTo(typeof(SaveStore)));
            Assert.That(property.CanWrite, Is.False);
            Assert.That(type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Where(method => !method.IsSpecialName).Select(method => method.Name),
                Is.EquivalentTo(new[] { nameof(SaveManager.Close), nameof(SaveManager.SetStorageRoot) }));
            Assert.That(type.GetProperty("DefaultSerializerSettings"), Is.Not.Null);
            Assert.That(type.GetProperty("DefaultSerializationFormat"), Is.Not.Null);
            Assert.That(type.GetProperty("SerializerSettings"), Is.Null);
            Assert.That(type.GetProperty("SerializationFormat"), Is.Null);
            Assert.That(type.GetNestedType("SaveItem"), Is.Null);
        }

        [Test]
        public void ReadingTheDefaultStoreDoesNotOverwriteItsInstanceFormatting()
        {
            var previous = SaveManager.DefaultSerializationFormat;
            try
            {
                SaveManager.DefaultSerializationFormat = Newtonsoft.Json.Formatting.Indented;
                var store = SaveManager.Store;
                store.SerializationFormat = Newtonsoft.Json.Formatting.None;
                Assert.That(SaveManager.Store, Is.SameAs(store));
                Assert.That(store.SerializationFormat, Is.EqualTo(Newtonsoft.Json.Formatting.None),
                    "Manager defaults apply at creation, not on every borrowed getter access.");
            }
            finally
            {
                SaveManager.DefaultSerializationFormat = previous;
            }
        }

        [Test]
        public void BorrowedDefaultStoreObjectAndSettingsSurviveARealColdReopen()
        {
            var store = SaveManager.Store;
            var item = store.CreateSaveItem();
            store.SaveObject(new Payload { Number = 17 }, "player", item.SaveId);
            store.SaveGlobalSetting(new Payload { Number = 9 }, "settings");
            SaveManager.Close();
            Assert.That(SaveManager.Store.LoadObject<Payload>("player", item.SaveId).Number, Is.EqualTo(17));
            Assert.That(SaveManager.Store.LoadGlobalSetting<Payload>("settings").Number, Is.EqualTo(9));
            Assert.That(SaveManager.Store.GetAllSaveItems().Count, Is.EqualTo(1));
        }

        [Test]
        public async Task BorrowedDefaultStoreKeepsItsAsyncSnapshotWithoutTheMainPackage()
        {
            var item = await SaveManager.Store.CreateSaveItemAsync(6);
            await SaveManager.Store.SaveObjectAsync(new Payload { Number = 81 }, "player", 6);
            SaveManager.Close();
            Assert.That((await SaveManager.Store.LoadObjectAsync<Payload>("player", 6)).Number, Is.EqualTo(81));
            await SaveManager.Store.SaveGlobalSettingAsync(new Payload { Number = 4 }, "options");
            Assert.That(SaveManager.Store.LoadGlobalSetting<Payload>("options").Number, Is.EqualTo(4));
            await SaveManager.Store.DeleteSaveItemAsync(item.SaveId);
            Assert.That(SaveManager.Store.GetAllSaveItems().Count, Is.Zero);
        }

        [Test]
        public void TopLevelSaveItemColdReadsTheExistingIndexKeysWithoutRewritingData()
        {
            Assert.That(typeof(SaveItem).DeclaringType, Is.Null);
            var directory = Path.Combine(_root, "SaveData");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "SaveSystemData.json");
            const string original = "{\"DataVersion\":2,\"SaveItemDict\":{\"7\":{\"SaveID\":7,\"CreateTime\":\"2025-04-03T02:01:00Z\",\"LastSaveTime\":\"2025-04-03T02:02:00Z\",\"DisplayName\":\"existing\",\"Note\":\"keep\"}},\"CurrentSaveID\":7}";
            File.WriteAllText(path, original);
            var store = SaveManager.Store;
            var item = store.GetSaveItem(7);
            Assert.That(item, Is.TypeOf<SaveItem>());
            Assert.That(item.SaveId, Is.EqualTo(7));
            Assert.That(item.DisplayName, Is.EqualTo("existing"));
            Assert.That(item.Note, Is.EqualTo("keep"));
            Assert.That(store.CurrentSaveId, Is.EqualTo(7));
            Assert.That(File.ReadAllText(path), Is.EqualTo(original), "Reading the moved CLR type must not migrate or rewrite index bytes.");
            Assert.That(store.SetSaveItemNote(7, "updated"), Is.True);
            var json = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(path));
            Assert.That(json.Properties().Select(property => property.Name), Is.EquivalentTo(new[] { "DataVersion", "SaveItemDict", "CurrentSaveID" }));
            Assert.That(((Newtonsoft.Json.Linq.JObject)json["SaveItemDict"]["7"]).Properties().Select(property => property.Name),
                Is.EquivalentTo(new[] { "SaveID", "CreateTime", "LastSaveTime", "DisplayName", "Note" }));
            Assert.That((string)json["SaveItemDict"]["7"]["Note"], Is.EqualTo("updated"));
            Assert.That(json.ToString().Contains("$type"), Is.False);
        }

        [Test]
        public void DefaultsApplyAtCreationAndSurviveCloseAndRootChangesWithoutResettingSettings()
        {
            var format = SaveManager.DefaultSerializationFormat;
            var settings = SaveManager.DefaultSerializerSettings;
            try
            {
                SaveManager.DefaultSerializationFormat = Newtonsoft.Json.Formatting.Indented;
                var first = SaveManager.Store;
                Assert.That(first.SerializerSettings, Is.SameAs(settings));
                Assert.That(first.SerializationFormat, Is.EqualTo(Newtonsoft.Json.Formatting.Indented));
                SaveManager.DefaultSerializationFormat = Newtonsoft.Json.Formatting.None;
                Assert.That(SaveManager.Store, Is.SameAs(first));
                Assert.That(first.SerializationFormat, Is.EqualTo(Newtonsoft.Json.Formatting.Indented));
                SaveManager.Close();
                Assert.Throws<ObjectDisposedException>(() => first.GetAllSaveItems());
                var second = SaveManager.Store;
                Assert.That(second, Is.Not.SameAs(first));
                Assert.That(second.SerializationFormat, Is.EqualTo(Newtonsoft.Json.Formatting.None));
                Assert.That(second.SerializerSettings, Is.SameAs(settings));
                second.SerializationFormat = Newtonsoft.Json.Formatting.Indented;
                Assert.That(SaveManager.Store.SerializationFormat, Is.EqualTo(Newtonsoft.Json.Formatting.Indented));
                SaveManager.SetStorageRoot(Path.Combine(_root, "next-root"));
                Assert.Throws<ObjectDisposedException>(() => second.GetAllSaveItems());
                Assert.That(SaveManager.DefaultSerializerSettings, Is.SameAs(settings));
                Assert.That(SaveManager.DefaultSerializationFormat, Is.EqualTo(Newtonsoft.Json.Formatting.None));
                Assert.That(SaveManager.Store.SerializerSettings, Is.SameAs(settings));
                Assert.That(SaveManager.Store.SerializationFormat, Is.EqualTo(Newtonsoft.Json.Formatting.None));
            }
            finally
            {
                SaveManager.DefaultSerializationFormat = format;
            }
        }

        [TearDown]
        public void ReturnOnlyThisOwner()
        {
            SaveManager.Close();
            SaveManager.SetStorageRoot(_previous);
            Assert.That(typeof(SaveManager).GetField("_store", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null), Is.Null);
            if (_root != null && Directory.Exists(_root) && TestContext.CurrentContext.Result.Outcome.Status != NUnit.Framework.Interfaces.TestStatus.Failed)
            {
                Directory.Delete(_root, true);
            }
        }
    }
}
