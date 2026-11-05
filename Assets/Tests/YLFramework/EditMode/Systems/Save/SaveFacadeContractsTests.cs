using System;
using System.IO;
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
        public void PlainFacadeObjectAndSettingsSurviveARealColdReopen()
        {
            var item = SaveManager.CreateSaveItem();
            SaveManager.SaveObject(new Payload { Number = 17 }, "player", item);
            SaveManager.SaveGlobalSetting(new Payload { Number = 9 }, "settings");
            SaveManager.Close();
            Assert.That(SaveManager.LoadObject<Payload>("player", item.SaveId).Number, Is.EqualTo(17));
            Assert.That(SaveManager.LoadGlobalSetting<Payload>("settings").Number, Is.EqualTo(9));
            Assert.That(SaveManager.GetSaveItemCount(), Is.EqualTo(1));
        }

        [Test]
        public async Task AsyncFacadeAndModelKeepTheirOwnSnapshotWithoutTheMainPackage()
        {
            var item = await SaveManager.CreateSaveItemAsync(6);
            await SaveManager.SaveObjectAsync(new Payload { Number = 81 }, "player", 6);
            SaveManager.Close();
            Assert.That((await SaveManager.LoadObjectAsync<Payload>("player", 6)).Number, Is.EqualTo(81));
            await SaveManager.SaveGlobalSettingAsync(new Payload { Number = 4 }, "options");
            Assert.That(SaveManager.LoadGlobalSetting<Payload>("options").Number, Is.EqualTo(4));
            await SaveManager.DeleteSaveItemAsync(item.SaveId);
            Assert.That(SaveManager.GetSaveItemCount(), Is.Zero);
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
