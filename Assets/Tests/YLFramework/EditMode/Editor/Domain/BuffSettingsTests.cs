using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;
using YLFramework.Buff;
using YLFramework.Editor.Buff;
using YLFramework.Editor.Common;
using BuffSettings = YLFramework.Editor.Buff.BuffSystemSettings;
using Object = UnityEngine.Object;

namespace YLFramework.Tests.Extensions.Domain
{
    public class BuffSettingsTests
    {
        private const string SettingsDirectory = "ProjectSettings/YLFramework/Core";
        private const string LegacyPath = SettingsDirectory + "/YLFramework.BuffSystem.BuffSystemSettings.asset";
        private const string CurrentPath = SettingsDirectory + "/YLFramework.Editor.Buff.BuffSystemSettings.asset";
        private const string ForeignPath = "ProjectSettings/Unrelated.BuffSystem.BuffSystemSettings.asset";
        private const string ScriptGuid = "fa93ecda799c52b4fab6e3d7efc0d510";
        private const BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;
        private const BindingFlags PrivateStatic = BindingFlags.NonPublic | BindingFlags.Static;
        private static readonly FieldInfo SettingsInstance = typeof(ProjectSettingsScriptableObject<BuffSettings>)
            .GetField("_instance", PrivateStatic);
        private static readonly FieldInfo ProviderInstance = typeof(BuffSettingsProvider)
            .GetField("_instance", PrivateStatic);
        private static readonly string[] ProviderCaches =
            { "_serializedObject", "_serializedProperties", "_reorderableListType" };

        private readonly Dictionary<string, (byte[] Bytes, DateTime Written)> _ownedFiles =
            new Dictionary<string, (byte[], DateTime)>(StringComparer.OrdinalIgnoreCase);
        private readonly List<Object> _ownedObjects = new List<Object>();
        private readonly List<SerializedObject> _ownedSerializedObjects = new List<SerializedObject>();
        private BuffSettings _previousSettings;
        private BuffSettingsProvider _previousProvider;
        private bool _ownsScope;
        private string _owner;
        private string[] _initialEntries;
        private string[] _missingDirectories;
        private (byte[] Bytes, DateTime Written)? _foreignBefore;
        private string _assetFolder;
        private string _assetFolderGuid;
        private EffectData _effect;
        private string _effectGuid;
        private (byte[] Bytes, DateTime Written) _effectBefore;
        private (byte[] Bytes, DateTime Written) _effectMetaBefore;

        [SetUp]
        public void SetUp()
        {
            // The coordinator selects an owned receiver or an empty Source. Never adopt existing settings.
            Assert.That(EditorApplication.isPlayingOrWillChangePlaymode, Is.False);
            Assert.That(EditorApplication.isCompiling || EditorApplication.isUpdating, Is.False);
            Assert.That(Path.GetFullPath(global::YLFramework.Testing.PackageTestPaths.Physical("Assets")), Is.EqualTo(Path.GetFullPath(global::YLFramework.Testing.PackageTestPaths.Physical(Application.dataPath))).IgnoreCase);
            Assert.That(Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical("ProjectSettings")), Is.True);
            foreach (string path in new[] { LegacyPath, CurrentPath })
            {
                Assert.That(File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(path)) || Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(path)), Is.False,
                    "Pre-existing Buff settings are not owned by this test: " + path);
                Assert.That(File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(path + ".meta")) || Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(path + ".meta")), Is.False,
                    "Pre-existing metadata is not owned by this test: " + path);
            }

            for (string path = Path.GetFullPath(global::YLFramework.Testing.PackageTestPaths.Physical(SettingsDirectory)); path != null; path = Path.GetDirectoryName(path))
            {
                if (Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(path)) || File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(path)))
                {
                    Assert.That((File.GetAttributes(global::YLFramework.Testing.PackageTestPaths.Physical(path)) & FileAttributes.ReparsePoint) != 0, Is.False,
                        "Refusing linked settings paths: " + path);
                }
            }

            Assert.That(SettingsInstance, Is.Not.Null);
            Assert.That(ProviderInstance, Is.Not.Null);
            _ownedFiles.Clear();
            _ownedObjects.Clear();
            _ownedSerializedObjects.Clear();
            _owner = "BuffSettingsCurrent-" + Guid.NewGuid().ToString("N");
            _initialEntries = SettingsEntries();
            _missingDirectories = new[] { SettingsDirectory, "ProjectSettings/YLFramework" }
                .Where(path => !Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(path))).ToArray();
            _foreignBefore = File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(ForeignPath)) ? Snapshot(ForeignPath) : null;
            _assetFolder = null;
            _assetFolderGuid = null;
            _effect = null;
            _effectGuid = null;
            _previousSettings = (BuffSettings)SettingsInstance.GetValue(null);
            _previousProvider = (BuffSettingsProvider)ProviderInstance.GetValue(null);
            _ownsScope = true;
            SettingsInstance.SetValue(null, null);
            ProviderInstance.SetValue(null, null);
        }

        [TearDown]
        public void TearDown()
        {
            if (!_ownsScope)
            {
                return;
            }

            TrackCachedSettings();
            SettingsInstance.SetValue(null, _previousSettings);
            ProviderInstance.SetValue(null, _previousProvider);
            try
            {
                foreach (var serialized in _ownedSerializedObjects)
                {
                    serialized.Dispose();
                }

                foreach (var instance in _ownedObjects)
                {
                    if (instance && !EditorUtility.IsPersistent(instance))
                    {
                        Object.DestroyImmediate(instance);
                    }
                }

                var errors = new List<string>();
                foreach (var file in _ownedFiles)
                {
                    if (!File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(file.Key)))
                    {
                        continue;
                    }

                    if (!Matches(file.Key, file.Value))
                    {
                        errors.Add("Refusing to delete changed test output: " + file.Key);
                        continue;
                    }

                    File.Delete(global::YLFramework.Testing.PackageTestPaths.Physical(file.Key));
                }

                if (_effectGuid != null && File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(_assetFolder + "/Effect.asset")))
                {
                    string path = _assetFolder + "/Effect.asset";
                    bool owned = AssetDatabase.AssetPathToGUID(path) == _effectGuid &&
                        Matches(path, _effectBefore) && Matches(path + ".meta", _effectMetaBefore);
                    if (!owned)
                    {
                        errors.Add("Refusing to delete changed EffectData fixture: " + path);
                    }
                    else if (!AssetDatabase.DeleteAsset(path))
                    {
                        errors.Add("Could not delete the owned EffectData fixture: " + path);
                    }
                }

                if (_assetFolderGuid != null && Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(_assetFolder)))
                {
                    bool owned = AssetDatabase.AssetPathToGUID(_assetFolder) == _assetFolderGuid &&
                        Directory.GetFileSystemEntries(global::YLFramework.Testing.PackageTestPaths.Physical(_assetFolder)).Length == 0;
                    if (!owned)
                    {
                        errors.Add("Refusing to delete a changed fixture folder: " + _assetFolder);
                    }
                    else if (!AssetDatabase.DeleteAsset(_assetFolder))
                    {
                        errors.Add("Could not delete the owned fixture folder: " + _assetFolder);
                    }
                }

                foreach (string directory in _missingDirectories)
                {
                    if (Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(directory)) && Directory.GetFileSystemEntries(global::YLFramework.Testing.PackageTestPaths.Physical(directory)).Length == 0)
                    {
                        Directory.Delete(global::YLFramework.Testing.PackageTestPaths.Physical(directory), false);
                    }
                }

                if (!SettingsEntries().OrderBy(path => path, StringComparer.Ordinal)
                    .SequenceEqual(_initialEntries.OrderBy(path => path, StringComparer.Ordinal)))
                {
                    errors.Add("Unexpected outputs are left intact rather than claimed by cleanup.");
                }
                if (File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(LegacyPath + ".meta")) || File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(CurrentPath + ".meta")))
                {
                    errors.Add("Unexpected settings metadata is left intact.");
                }
                if (_foreignBefore.HasValue ? !Matches(ForeignPath, _foreignBefore.Value) : File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(ForeignPath)))
                {
                    errors.Add("Foreign settings must remain byte-for-byte unchanged; never restore or delete them.");
                }

                Assert.That(errors, Is.Empty, string.Join(Environment.NewLine, errors));
            }
            finally
            {
                SettingsInstance.SetValue(null, _previousSettings);
                ProviderInstance.SetValue(null, _previousProvider);
                _ownsScope = false;
            }
        }

        [Test]
        public void CurrentSettingsHaveNoOneTimeMigrationSurface()
        {
            Assert.That(typeof(BuffSettings).Assembly.GetType("YLFramework.Editor.Buff.BuffSettingsMigration"), Is.Null);
            Assert.That(typeof(ProjectSettingsScriptableObject<BuffSettings>).Assembly.GetType("YLFramework.Editor.Common.PreviousProjectSettingsNameAttribute"), Is.Null);
            Assert.That(typeof(ProjectSettingsScriptableObject<BuffSettings>).GetProperty("PreviousFilePath", PrivateStatic), Is.Null);
        }

        [Test]
        public void CurrentSettingsDoNotReadOrMoveAnUnrelatedFormerNameFile()
        {
            WriteSettings(LegacyPath, CreatePayload());
            var before = Snapshot(LegacyPath);
            ObserveWrites(BuffSettings.LoadOrCreate);
            Assert.That(BuffSettings.Instance, Is.Not.Null);
            Assert.That(Matches(LegacyPath, before), Is.True);
            AssertFiles(LegacyPath, CurrentPath);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void CurrentLoadAndSaveRejectIncompatibleOrMultipleObjectsWithoutRewriting(bool multipleObjects)
        {
            if (multipleObjects)
            {
                WriteSettings(CurrentPath, CreatePayload(), CreatePayload("-extra"));
            }
            else
            {
                var wrongType = ScriptableObject.CreateInstance<EffectData>();
                wrongType.name = _owner;
                _ownedObjects.Add(wrongType);
                WriteSettings(CurrentPath, wrongType);
            }
            var before = Snapshot(CurrentPath);
            ObserveWrites(() => Assert.Throws<IOException>(BuffSettings.LoadOrCreate));
            Assert.That(SettingsInstance.GetValue(null), Is.Null);
            var cached = CreatePayload("-cached");
            SettingsInstance.SetValue(null, cached);
            ObserveWrites(() => Assert.Throws<IOException>(() => BuffSettings.Save()));
            Assert.That(SettingsInstance.GetValue(null), Is.SameAs(cached));
            Assert.That(Matches(CurrentPath, before), Is.True);
            AssertFiles(CurrentPath);
        }

        [Test]
        public void CurrentSaveAndLoadRoundTripAllFieldsAndPreserveAssetReferences()
        {
            SettingsInstance.SetValue(null, CreatePayload());
            ObserveWrites(() => BuffSettings.Save());
            var before = Snapshot(CurrentPath);
            SettingsInstance.SetValue(null, null);
            BuffSettings loaded = null;
            ObserveWrites(() => loaded = BuffSettings.Instance);
            AssertPayload(loaded);
            ObserveWrites(() => BuffSettings.LoadOrCreate());
            Assert.That(BuffSettings.Instance, Is.Not.SameAs(loaded), "Verify disk reload, not just the cached payload.");
            AssertPayload(BuffSettings.Instance);
            Assert.That(Matches(CurrentPath, before), Is.True);
            AssertFiles(CurrentPath);
            Assert.That(Matches(_assetFolder + "/Effect.asset", _effectBefore), Is.True);
            Assert.That(Matches(_assetFolder + "/Effect.asset.meta", _effectMetaBefore), Is.True);
        }

        [Test]
        public void ProviderReloadDropsAllSerializedBindingsWithoutLoadingOrWritingSettings()
        {
            var cached = CreatePayload();
            SettingsInstance.SetValue(null, cached);
            var provider = CacheProviderBindings(cached);
            var reload = typeof(BuffSettingsProvider).GetMethod("Reload", PrivateInstance, null, Type.EmptyTypes, null);
            Assert.That(reload, Is.Not.Null, "Missing API: internal void BuffSettingsProvider.Reload().");
            Assert.That(reload.IsAssembly, Is.True);
            Assert.That(reload.ReturnType, Is.EqualTo(typeof(void)));

            ObserveWrites(() => reload.Invoke(provider, null));

            AssertProviderCachesCleared(provider);
            Assert.That(BuffSettingsProvider.Instance, Is.SameAs(provider));
            Assert.That(SettingsInstance.GetValue(null), Is.SameAs(cached));
            AssertFiles();
        }

        private BuffSettings CreatePayload(string suffix = "")
        {
            if (!_effect)
            {
                _assetFolder = "Assets/" + _owner;
                Assert.That(File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(_assetFolder)) || Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(_assetFolder)) ||
                    File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(_assetFolder + ".meta")), Is.False);
                _assetFolderGuid = AssetDatabase.CreateFolder("Assets", _owner);
                Assert.That(_assetFolderGuid, Is.Not.Empty);
                _effect = ScriptableObject.CreateInstance<EffectData>();
                _ownedObjects.Add(_effect);
                _effect.id = 701;
                _effect.type = 37;
                _effect.effectName = _owner + "-effect";
                AssetDatabase.CreateAsset(_effect, _assetFolder + "/Effect.asset");
                _effectGuid = AssetDatabase.AssetPathToGUID(_assetFolder + "/Effect.asset");
                Assert.That(_effectGuid, Is.Not.Empty);
                _effectBefore = Snapshot(_assetFolder + "/Effect.asset");
                _effectMetaBefore = Snapshot(_assetFolder + "/Effect.asset.meta");
            }

            var settings = ScriptableObject.CreateInstance<BuffSettings>();
            _ownedObjects.Add(settings);
            settings.name = _owner + suffix;
            Assert.That(AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(MonoScript.FromScriptableObject(settings))),
                Is.EqualTo(ScriptGuid), "Current Buff settings retain their MonoScript identity.");
            typeof(BuffSettings).GetField("allEffectTypes", PrivateInstance).SetValue(settings,
                new List<EffectType>
                {
                    new EffectType { type = 37, name = _owner },
                    new EffectType { type = 0, name = "secondary" }
                });
            typeof(BuffSettings).GetField("allEffectDatas", PrivateInstance).SetValue(settings,
                new List<EffectData> { _effect });
            typeof(BuffSettings).GetField("allEffectDataWithType", PrivateInstance).SetValue(settings,
                new List<GroupEffectData> { new GroupEffectData { effectDatas = new List<EffectData> { _effect } } });
            return settings;
        }

        private void AssertPayload(BuffSettings settings)
        {
            Assert.That(settings, Is.Not.Null);
            Assert.That(settings.name, Is.EqualTo(_owner));
            using var serialized = new SerializedObject(settings);
            var types = serialized.FindProperty("allEffectTypes");
            Assert.That(types.arraySize, Is.EqualTo(2));
            Assert.That(types.GetArrayElementAtIndex(0).FindPropertyRelative("type").intValue, Is.EqualTo(37));
            Assert.That(types.GetArrayElementAtIndex(0).FindPropertyRelative("name").stringValue, Is.EqualTo(_owner));
            Assert.That(types.GetArrayElementAtIndex(1).FindPropertyRelative("type").intValue, Is.Zero);
            Assert.That(types.GetArrayElementAtIndex(1).FindPropertyRelative("name").stringValue, Is.EqualTo("secondary"));
            var effects = serialized.FindProperty("allEffectDatas");
            var groups = serialized.FindProperty("allEffectDataWithType");
            Assert.That(effects.arraySize, Is.EqualTo(1));
            Assert.That(groups.arraySize, Is.EqualTo(1));
            var groupedEffects = groups.GetArrayElementAtIndex(0).FindPropertyRelative("effectDatas");
            Assert.That(groupedEffects.arraySize, Is.EqualTo(1));
            Assert.That(effects.GetArrayElementAtIndex(0).objectReferenceValue, Is.EqualTo(_effect));
            Assert.That(groupedEffects.GetArrayElementAtIndex(0).objectReferenceValue, Is.EqualTo(_effect));
            Assert.That(AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(
                effects.GetArrayElementAtIndex(0).objectReferenceValue)), Is.EqualTo(_effectGuid));
            Assert.That(_effect.id, Is.EqualTo(701));
            Assert.That(_effect.type, Is.EqualTo(37));
            Assert.That(_effect.effectName, Is.EqualTo(_owner + "-effect"));
            Assert.That(File.ReadAllText(global::YLFramework.Testing.PackageTestPaths.Physical(CurrentPath)), Does.Contain(_effectGuid));
        }

        private static FieldInfo ProviderField(string name)
        {
            var field = typeof(BuffSettingsProvider).GetField(name, PrivateInstance);
            Assert.That(field, Is.Not.Null, name);
            return field;
        }

        private BuffSettingsProvider CacheProviderBindings(BuffSettings settings)
        {
            var provider = BuffSettingsProvider.Instance;
            var serialized = new SerializedObject(settings);
            _ownedSerializedObjects.Add(serialized);
            var property = serialized.FindProperty("allEffectTypes");
            ProviderField("_serializedObject").SetValue(provider, serialized);
            ProviderField("_serializedProperties").SetValue(provider, new List<SerializedProperty> { property });
            ProviderField("_reorderableListType").SetValue(provider, new ReorderableList(serialized, property));
            foreach (string name in ProviderCaches)
            {
                Assert.That(ProviderField(name).GetValue(provider), Is.Not.Null, name);
            }
            return provider;
        }

        private static void AssertProviderCachesCleared(BuffSettingsProvider provider)
        {
            foreach (string name in ProviderCaches)
            {
                Assert.That(ProviderField(name).GetValue(provider), Is.Null, name);
            }
        }

        private void WriteSettings(string path, params Object[] objects)
        {
            Assert.That(path == LegacyPath || path == CurrentPath, Is.True);
            Assert.That(File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(path)) || Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(path)), Is.False);
            Directory.CreateDirectory(global::YLFramework.Testing.PackageTestPaths.Physical(SettingsDirectory));
            string temporary = Path.Combine(SettingsDirectory, _owner + "-serialized-" + Guid.NewGuid().ToString("N") + ".asset");
            Assert.That(File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(temporary)) || Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(temporary)), Is.False);
            InternalEditorUtility.SaveToSerializedFileAndForget(objects, temporary, true);
            var serialized = Snapshot(temporary);
            _ownedFiles.Add(Path.GetFullPath(global::YLFramework.Testing.PackageTestPaths.Physical(temporary)), serialized);
            try
            {
                ObserveWrites(() =>
                {
                    using var output = new FileStream(global::YLFramework.Testing.PackageTestPaths.Physical(path), FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    _ownedFiles.Add(Path.GetFullPath(global::YLFramework.Testing.PackageTestPaths.Physical(path)), (Array.Empty<byte>(), File.GetLastWriteTimeUtc(path)));
                    output.Write(serialized.Bytes, 0, serialized.Bytes.Length);
                });
            }
            finally
            {
                bool unchanged = Matches(temporary, serialized);
                if (unchanged)
                {
                    File.Delete(global::YLFramework.Testing.PackageTestPaths.Physical(temporary));
                }
                Assert.That(unchanged, Is.True, "Refusing to delete a changed serialization fixture: " + temporary);
            }
        }

        private void ObserveWrites(Action action)
        {
            var before = SettingsEntries();
            TrackCachedSettings();
            try
            {
                action();
            }
            finally
            {
                TrackCachedSettings();
                // Only these two absent-at-SetUp paths can be outputs of the synchronous invocation.
                foreach (string path in new[] { LegacyPath, CurrentPath }.Select(Path.GetFullPath))
                {
                    if (File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(path)) && (_ownedFiles.ContainsKey(path) || !before.Contains(path)))
                    {
                        _ownedFiles[path] = Snapshot(path);
                    }
                }

            }
        }

        private void TrackCachedSettings()
        {
            var instance = (BuffSettings)SettingsInstance.GetValue(null);
            if (instance && instance != _previousSettings && !_ownedObjects.Contains(instance))
            {
                _ownedObjects.Add(instance);
            }
        }

        private void AssertFiles(params string[] paths)
        {
            Assert.That(SettingsEntries(), Is.EquivalentTo(_initialEntries.Concat(paths.Select(Path.GetFullPath))));
        }

        private static string[] SettingsEntries()
        {
            return Directory.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(SettingsDirectory))
                ? Directory.GetFileSystemEntries(global::YLFramework.Testing.PackageTestPaths.Physical(SettingsDirectory)).Select(Path.GetFullPath).ToArray()
                : Array.Empty<string>();
        }

        private static (byte[] Bytes, DateTime Written) Snapshot(string path)
        {
            return (File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(path)), File.GetLastWriteTimeUtc(path));
        }

        private static bool Matches(string path, (byte[] Bytes, DateTime Written) snapshot)
        {
            return File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(path)) && File.ReadAllBytes(global::YLFramework.Testing.PackageTestPaths.Physical(path)).SequenceEqual(snapshot.Bytes) &&
                File.GetLastWriteTimeUtc(path) == snapshot.Written;
        }
    }
}
