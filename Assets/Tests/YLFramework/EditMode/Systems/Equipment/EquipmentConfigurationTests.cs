using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using YLFramework.Events;

namespace YLFramework.Tests.Extensions.Equipment
{
    public class EquipmentConfigurationTests
    {
        private readonly List<ScriptableObject> _objects = new();
        private Type _rootType;
        private Type _dataType;
        private string _folder;

        [SetUp]
        public void SetUp()
        {
            _rootType = typeof(EventChannel).Assembly.GetType("YLFramework.Equipment.EquipmentsConfig");
            _dataType = typeof(EventChannel).Assembly.GetType("YLFramework.Equipment.EquipmentConfigData");
            Assert.That(_rootType, Is.Not.Null, "YL equipment definitions have not been migrated.");
            Assert.That(_dataType, Is.Not.Null);
        }

        [TearDown]
        public void TearDown()
        {
            if (_folder != null)
            {
                AssetDatabase.DeleteAsset(_folder);
            }

            foreach (var value in _objects)
            {
                if (value && !EditorUtility.IsPersistent(value))
                {
                    UnityEngine.Object.DestroyImmediate(value);
                }
            }

            _objects.Clear();
        }

        private ScriptableObject Create(Type type)
        {
            var value = ScriptableObject.CreateInstance(type);
            _objects.Add(value);
            return value;
        }

        private IList Rows(ScriptableObject root) => (IList)_rootType.GetProperty("Equipments").GetValue(root);
        private void Id(ScriptableObject row, int id) => _dataType.GetField("id").SetValue(row, id);
        private object Find(ScriptableObject root, int id)
        {
            try
            {
                return _rootType.GetMethod("GetEquipmentConfigData").Invoke(root, new object[] { id });
            }
            catch (TargetInvocationException error)
            {
                ExceptionDispatchInfo.Capture(error.InnerException).Throw();
                throw;
            }
        }

        [Test]
        public void EmptyTableCanBeQueriedWithoutLoadingAssets()
        {
            var root = Create(_rootType);
            Assert.That(Rows(root), Is.Empty);
            Assert.That(Find(root, 1001), Is.Null);
        }

        [Test]
        public void CacheDetectsEqualLengthReplacementAndChangedId()
        {
            var root = Create(_rootType);
            var first = Create(_dataType);
            Id(first, 7);
            Rows(root).Add(first);
            Assert.That(Find(root, 7), Is.SameAs(first));
            var second = Create(_dataType);
            Id(second, 9);
            Rows(root)[0] = second;
            Assert.That(Find(root, 7), Is.Null);
            Assert.That(Find(root, 9), Is.SameAs(second));
            Id(second, 11);
            Assert.That(Find(root, 9), Is.Null);
            Assert.That(Find(root, 11), Is.SameAs(second));
        }

        [Test]
        public void DuplicateIdDoesNotPublishAPartialCache()
        {
            var root = Create(_rootType);
            var first = Create(_dataType);
            Id(first, 9);
            Rows(root).Add(first);
            var second = Create(_dataType);
            Id(second, 9);
            Rows(root).Add(second);
            var error = Assert.Throws<InvalidOperationException>(() => Find(root, 500));
            Assert.That(error.Message, Does.Contain("9"));
            Id(second, 10);
            Assert.That(Find(root, 9), Is.SameAs(first));
            Assert.That(Find(root, 10), Is.SameAs(second));
        }

        [Test]
        public void ReadingSavedTableDoesNotWriteAndReferencesSurviveReimport()
        {
            var parent = "Assets/Tests/YLFramework/Fixtures/Extensions";
            _folder = parent + "/EquipmentConfig-" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder(parent, Path.GetFileName(_folder));
            var root = Create(_rootType);
            var row = Create(_dataType);
            Id(row, 1001);
            _dataType.GetField("parent").SetValue(row, root);
            Rows(root).Add(row);
            var path = _folder + "/Root.asset";
            AssetDatabase.CreateAsset(root, path);
            AssetDatabase.AddObjectToAsset(row, root);
            EditorUtility.SetDirty(root);
            AssetDatabase.SaveAssetIfDirty(root);
            var before = File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(path));
            Assert.That(EditorUtility.IsDirty(root), Is.False);
            Assert.That(Rows(root).Count, Is.EqualTo(1));
            Assert.That(Find(root, 1001), Is.SameAs(row));
            Assert.That(EditorUtility.IsDirty(root), Is.False);
            CollectionAssert.AreEqual(before, File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(path)));
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            var reopened = AssetDatabase.LoadAssetAtPath(path, _rootType) as ScriptableObject;
            var restored = Find(reopened, 1001);
            Assert.That(restored, Is.Not.Null);
            Assert.That(_dataType.GetField("parent").GetValue(restored), Is.SameAs(reopened));
        }
    }
}
