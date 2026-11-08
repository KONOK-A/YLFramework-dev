using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using YLFramework.Save;

namespace YLFramework.Tests.Extensions.Data.Save
{
    [Category("YL.Module.Data")]
    public class SaveMissingSlotNativeTests
    {
        private static readonly FieldInfo _storeField = typeof(SaveManager).GetField(
            "_store", BindingFlags.Static | BindingFlags.NonPublic);
        private static readonly FieldInfo _rootField = typeof(SaveManager).GetField(
            "_storageRootOverride", BindingFlags.Static | BindingFlags.NonPublic);
        private string _previousRoot;
        private string _root;
        private bool _ownsRoot;

        public class Payload
        {
            public int Number;
        }

        [SetUp]
        public void SetUp()
        {
            Assert.That(_storeField.GetValue(null), Is.Null, "Do not close or detach another Save owner.");
            _previousRoot = (string)_rootField.GetValue(null);
            var output = Environment.GetEnvironmentVariable("YL_SAVE_TEST_OUTPUT") ??
                Path.Combine(Directory.GetCurrentDirectory(), "Temp", "Extensions.Data.Save");
            _root = Path.Combine(output, "native-missing-slot-cases", Guid.NewGuid().ToString("N"));
            var physicalRoot = global::YLFramework.Testing.PackageTestPaths.Physical(_root);
            Assert.That(Directory.Exists(physicalRoot), Is.False);
            Directory.CreateDirectory(physicalRoot);
            _ownsRoot = true;
            SaveManager.SetStorageRoot(_root);
            AssertEmptyRoot();
        }

        [TearDown]
        public void TearDown()
        {
            if (!_ownsRoot)
            {
                return;
            }

            try
            {
                SaveManager.Close();
            }
            finally
            {
                SaveManager.SetStorageRoot(_previousRoot);
                _ownsRoot = false;
            }

            Assert.That(_storeField.GetValue(null), Is.Null);
            Assert.That(_rootField.GetValue(null), Is.EqualTo(_previousRoot));
            if (TestContext.CurrentContext.Result.Outcome.Status == NUnit.Framework.Interfaces.TestStatus.Passed)
            {
                Directory.Delete(global::YLFramework.Testing.PackageTestPaths.Physical(_root), true);
            }
            else
            {
                TestContext.WriteLine("Preserved failed native missing-slot fixture: " + _root);
            }
        }

        [TestCase(0)]
        [TestCase(7)]
        public void SynchronousSaveOnAnUnknownSlotThrowsWithoutLoggingOrWriting(int saveId)
        {
            Assert.Throws<KeyNotFoundException>(() => SaveManager.Store.SaveObject(new Payload { Number = 42 }, "player", saveId));

            LogAssert.NoUnexpectedReceived();
            AssertEmptyRoot();
        }

        [TestCase(0)]
        [TestCase(7)]
        public void SynchronousLoadOnAnUnknownSlotThrowsWithoutLoggingOrWriting(int saveId)
        {
            Assert.Throws<KeyNotFoundException>(() => SaveManager.Store.LoadObject<Payload>("player", saveId));

            LogAssert.NoUnexpectedReceived();
            AssertEmptyRoot();
        }

        [TestCase(0)]
        [TestCase(7)]
        public void QueryAndDeleteOnAnUnknownSlotReturnFalseWithoutLogging(int saveId)
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                Assert.That(SaveManager.Store.HasSaveItem(saveId), Is.False);
                Assert.That(SaveManager.Store.HasObject("player", saveId), Is.False);
                Assert.That(SaveManager.Store.DeleteObject("player", saveId), Is.False);
            }

            LogAssert.NoUnexpectedReceived();
            AssertEmptyRoot();
        }

        [TestCase(0)]
        [TestCase(7)]
        public void ReadOnAnUnknownSlotHasTheSameExplicitErrorAsBothLoadPaths(int saveId)
        {
            Assert.Throws<KeyNotFoundException>(() => SaveManager.Store.ReadObject<Payload>("player", saveId));

            LogAssert.NoUnexpectedReceived();
            AssertEmptyRoot();
        }

        [TestCase(0)]
        [TestCase(7)]
        public void AsynchronousObjectOperationsOnAnUnknownSlotThrowInsteadOfLogging(int saveId)
        {
            Assert.ThrowsAsync<KeyNotFoundException>(() => SaveManager.Store.SaveObjectAsync(
                new Payload { Number = 42 }, "player", saveId));
            Assert.ThrowsAsync<KeyNotFoundException>(() => SaveManager.Store.LoadObjectAsync<Payload>("player", saveId));
            Assert.ThrowsAsync<KeyNotFoundException>(() => SaveManager.Store.ReadObjectAsync<Payload>("player", saveId));

            LogAssert.NoUnexpectedReceived();
            AssertEmptyRoot();
        }

        private void AssertEmptyRoot()
        {
            Assert.That(SaveManager.Store.GetAllSaveItems().Count, Is.Zero);
            Assert.That(SaveManager.Store.CurrentSaveId, Is.Null);
            var entries = Directory.GetFileSystemEntries(
                global::YLFramework.Testing.PackageTestPaths.Physical(_root), "*", SearchOption.AllDirectories);
            Assert.That(entries.Length, Is.EqualTo(1));
            Assert.That(Path.GetFileName(entries[0]), Is.EqualTo(".yl-save.lock"));
        }
    }
}
