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
        public void SynchronousSaveOnAnUnknownSlotLogsTheExistingErrorAndDoesNotWrite(int saveId)
        {
            LogAssert.Expect(LogType.Error, "#YLFramework 没有该存档项，非法访问 :" + saveId);

            SaveManager.SaveObject(new Payload { Number = 42 }, "player", saveId);

            LogAssert.NoUnexpectedReceived();
            AssertEmptyRoot();
        }

        [TestCase(0)]
        [TestCase(7)]
        public void SynchronousLoadOnAnUnknownSlotLogsTheExistingErrorAndReturnsNull(int saveId)
        {
            LogAssert.Expect(LogType.Error, "#YLFramework 没有该存档项，非法访问 :" + saveId);

            Assert.That(SaveManager.LoadObject<Payload>("player", saveId), Is.Null);

            LogAssert.NoUnexpectedReceived();
            AssertEmptyRoot();
        }

        [TestCase(0)]
        [TestCase(7)]
        public void TryLoadAndDeleteOnAnUnknownSlotReturnFalseWithoutLogging(int saveId)
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                Assert.That(SaveManager.TryLoadObject(out Payload value, "player", saveId), Is.False);
                Assert.That(value, Is.Null);
                Assert.That(SaveManager.DeleteObject("player", saveId), Is.False);
            }

            LogAssert.NoUnexpectedReceived();
            AssertEmptyRoot();
        }

        [TestCase(0)]
        [TestCase(7)]
        public void ReadOnAnUnknownSlotThrowsInsteadOfUsingTheSynchronousLoadLogBoundary(int saveId)
        {
            Assert.Throws<KeyNotFoundException>(() => SaveManager.ReadObject<Payload>("player", saveId));

            LogAssert.NoUnexpectedReceived();
            AssertEmptyRoot();
        }

        [TestCase(0)]
        [TestCase(7)]
        public void AsynchronousObjectOperationsOnAnUnknownSlotThrowInsteadOfLogging(int saveId)
        {
            Assert.ThrowsAsync<KeyNotFoundException>(() => SaveManager.SaveObjectAsync(
                new Payload { Number = 42 }, "player", saveId));
            Assert.ThrowsAsync<KeyNotFoundException>(() => SaveManager.LoadObjectAsync<Payload>("player", saveId));
            Assert.ThrowsAsync<KeyNotFoundException>(() => SaveManager.ReadObjectAsync<Payload>("player", saveId));

            LogAssert.NoUnexpectedReceived();
            AssertEmptyRoot();
        }

        private void AssertEmptyRoot()
        {
            Assert.That(SaveManager.GetSaveItemCount(), Is.Zero);
            Assert.That(SaveManager.CurrentSaveId, Is.Null);
            var entries = Directory.GetFileSystemEntries(
                global::YLFramework.Testing.PackageTestPaths.Physical(_root), "*", SearchOption.AllDirectories);
            Assert.That(entries.Length, Is.EqualTo(1));
            Assert.That(Path.GetFileName(entries[0]), Is.EqualTo(".yl-save.lock"));
        }
    }
}
