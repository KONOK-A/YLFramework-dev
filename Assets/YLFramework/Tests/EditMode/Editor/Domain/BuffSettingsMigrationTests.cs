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
    public class BuffSettingsMigrationTests
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
            Assert.That(Path.GetFullPath("Assets"), Is.EqualTo(Path.GetFullPath(Application.dataPath)).IgnoreCase);
            Assert.That(Directory.Exists("ProjectSettings"), Is.True);
            foreach (string path in new[] { LegacyPath, CurrentPath })
            {
                Assert.That(File.Exists(path) || Directory.Exists(path), Is.False,
                    "Pre-existing Buff settings are not owned by this test: " + path);
                Assert.That(File.Exists(path + ".meta") || Directory.Exists(path + ".meta"), Is.False,
                    "Pre-existing metadata is not owned by this test: " + path);
            }

            for (string path = Path.GetFullPath(SettingsDirectory); path != null; path = Path.GetDirectoryName(path))
            {
                if (Directory.Exists(path) || File.Exists(path))
                {
                    Assert.That((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0, Is.False,
                        "Refusing linked settings paths: " + path);
                }
            }

            Assert.That(SettingsInstance, Is.Not.Null);
            Assert.That(ProviderInstance, Is.Not.Null);
            _ownedFiles.Clear();
            _ownedObjects.Clear();
            _ownedSerializedObjects.Clear();
            _owner = "BuffSettingsMigration-" + Guid.NewGuid().ToString("N");
            _initialEntries = SettingsEntries();
            _missingDirectories = new[] { SettingsDirectory, "ProjectSettings/YLFramework" }
                .Where(path => !Directory.Exists(path)).ToArray();
            _foreignBefore = File.Exists(ForeignPath) ? Snapshot(ForeignPath) : null;
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
                    if (!File.Exists(file.Key))
                    {
                        continue;
                    }

                    if (!Matches(file.Key, file.Value))
                    {
                        errors.Add("Refusing to delete changed test output: " + file.Key);
                        continue;
                    }

                    File.Delete(file.Key);
                }

                if (_effectGuid != null && File.Exists(_assetFolder + "/Effect.asset"))
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

                if (_assetFolderGuid != null && Directory.Exists(_assetFolder))
                {
                    bool owned = AssetDatabase.AssetPathToGUID(_assetFolder) == _assetFolderGuid &&
                        Directory.GetFileSystemEntries(_assetFolder).Length == 0;
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
                    if (Directory.Exists(directory) && Directory.GetFileSystemEntries(directory).Length == 0)
                    {
                        Directory.Delete(directory, false);
                    }
                }

                if (!SettingsEntries().OrderBy(path => path, StringComparer.Ordinal)
                    .SequenceEqual(_initialEntries.OrderBy(path => path, StringComparer.Ordinal)))
                {
                    errors.Add("Unexpected outputs are left intact rather than claimed by cleanup.");
                }
                if (File.Exists(LegacyPath + ".meta") || File.Exists(CurrentPath + ".meta"))
                {
                    errors.Add("Unexpected settings metadata is left intact.");
                }
                if (_foreignBefore.HasValue ? !Matches(ForeignPath, _foreignBefore.Value) : File.Exists(ForeignPath))
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

        [TestCase(false)]
        [TestCase(true)]
        public void LegacyOnlyLoadRefusesToCreateDefaultSettings(bool explicitLoad)
        {
            WriteSettings(LegacyPath, CreatePayload());
            var before = Snapshot(LegacyPath);

            ObserveWrites(() => Assert.Throws<IOException>(() =>
            {
                if (explicitLoad)
                {
                    BuffSettings.LoadOrCreate();
                }
                else
                {
                    _ = BuffSettings.Instance;
                }
            }));

            Assert.That(SettingsInstance.GetValue(null), Is.Null);
            Assert.That(Matches(LegacyPath, before), Is.True);
            AssertFiles(LegacyPath);
        }

        [Test]
        public void LegacyOnlySaveRefusesToCreateNewSettingsFromACachedInstance()
        {
            WriteSettings(LegacyPath, CreatePayload());
            var before = Snapshot(LegacyPath);
            var cached = CreatePayload("-cached");
            SettingsInstance.SetValue(null, cached);

            ObserveWrites(() => Assert.Throws<IOException>(() => BuffSettings.Save()));

            Assert.That(SettingsInstance.GetValue(null), Is.SameAs(cached));
            Assert.That(Matches(LegacyPath, before), Is.True);
            AssertFiles(LegacyPath);
        }

        [Test]
        public void MigrationRejectsDualFileConflictWithoutChangingEitherFileOrWritingABackup()
        {
            WriteSettings(LegacyPath, CreatePayload());
            WriteSettings(CurrentPath, CreatePayload("-current"));
            var legacy = Snapshot(LegacyPath);
            var current = Snapshot(CurrentPath);

            var error = Assert.Throws<TargetInvocationException>(() => Migrate());

            Assert.That(error.InnerException, Is.TypeOf<IOException>());
            Assert.That(Matches(LegacyPath, legacy), Is.True);
            Assert.That(Matches(CurrentPath, current), Is.True);
            Assert.That(SettingsInstance.GetValue(null), Is.Null);
            AssertFiles(LegacyPath, CurrentPath);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void MigrationRejectsIncompatibleLegacyObjectsWithoutWritingABackup(bool multipleObjects)
        {
            if (multipleObjects)
            {
                WriteSettings(LegacyPath, CreatePayload(), CreatePayload("-extra"));
            }
            else
            {
                var wrongType = ScriptableObject.CreateInstance<EffectData>();
                wrongType.name = _owner;
                _ownedObjects.Add(wrongType);
                WriteSettings(LegacyPath, wrongType);
            }
            var before = Snapshot(LegacyPath);

            var error = Assert.Throws<TargetInvocationException>(() => Migrate());

            Assert.That(error.InnerException, Is.TypeOf<IOException>());
            Assert.That(Matches(LegacyPath, before), Is.True);
            Assert.That(SettingsInstance.GetValue(null), Is.Null);
            AssertFiles(LegacyPath);
        }

        [Test]
        public void MigrationBacksUpAndMovesExactBuffBytesThenReloadsAllFieldsAndAssetReferences()
        {
            WriteSettings(LegacyPath, CreatePayload());
            var legacy = Snapshot(LegacyPath);

            string backup = Migrate();

            AssertMigrationFiles(backup, legacy.Bytes);
            BuffSettings loaded = null;
            ObserveWrites(() => loaded = BuffSettings.Instance);
            AssertPayload(loaded);
            ObserveWrites(() => BuffSettings.LoadOrCreate());
            Assert.That(BuffSettings.Instance, Is.Not.SameAs(loaded), "Verify disk reload, not just the cached payload.");
            AssertPayload(BuffSettings.Instance);
            AssertMigrationFiles(backup, legacy.Bytes);
            Assert.That(Matches(_assetFolder + "/Effect.asset", _effectBefore), Is.True);
            Assert.That(Matches(_assetFolder + "/Effect.asset.meta", _effectMetaBefore), Is.True);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void MigrationWithoutLegacyFileIsANoOpAndDoesNotCreateDefaultsOrBackups(bool currentExists)
        {
            (byte[] Bytes, DateTime Written)? current = null;
            if (currentExists)
            {
                var cached = CreatePayload();
                WriteSettings(CurrentPath, cached);
                SettingsInstance.SetValue(null, cached);
                current = Snapshot(CurrentPath);
            }
            var settingsBefore = SettingsInstance.GetValue(null);
            var providerBefore = ProviderInstance.GetValue(null);

            Assert.That(Migrate(), Is.Null);

            Assert.That(SettingsInstance.GetValue(null), Is.SameAs(settingsBefore));
            Assert.That(ProviderInstance.GetValue(null), Is.SameAs(providerBefore));
            if (current.HasValue)
            {
                Assert.That(Matches(CurrentPath, current.Value), Is.True);
            }
            AssertFiles(currentExists ? new[] { CurrentPath } : Array.Empty<string>());
        }

        [Test]
        public void SecondMigrationIsIdempotentWithoutAnAdditionalBackupOrReload()
        {
            WriteSettings(LegacyPath, CreatePayload());
            byte[] original = File.ReadAllBytes(LegacyPath);
            string backup = Migrate();
            AssertMigrationFiles(backup, original);
            var backupBefore = Snapshot(backup);
            var currentBefore = Snapshot(CurrentPath);
            var cached = SettingsInstance.GetValue(null);

            Assert.That(Migrate(), Is.Null);

            Assert.That(Matches(backup, backupBefore), Is.True);
            Assert.That(Matches(CurrentPath, currentBefore), Is.True);
            Assert.That(SettingsInstance.GetValue(null), Is.SameAs(cached));
            AssertFiles(CurrentPath, backup);
        }

        [Test]
        public void MigrationReplacesCachedSettingsAndInvalidatesTheExistingProviderBindings()
        {
            WriteSettings(LegacyPath, CreatePayload());
            byte[] original = File.ReadAllBytes(LegacyPath);
            var stale = CreatePayload("-stale");
            SettingsInstance.SetValue(null, stale);
            var provider = CacheProviderBindings(stale);

            string backup = Migrate();

            Assert.That(BuffSettingsProvider.Instance, Is.SameAs(provider),
                "Clear the registered provider's bindings, not just its singleton reference.");
            AssertProviderCachesCleared(provider);
            Assert.That(SettingsInstance.GetValue(null), Is.Not.Null,
                "A successful migration must reload the migrated settings.");
            Assert.That(SettingsInstance.GetValue(null), Is.Not.SameAs(stale));
            AssertPayload(BuffSettings.Instance);
            AssertMigrationFiles(backup, original);
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

        private string Migrate()
        {
            var type = typeof(BuffSettings).Assembly.GetType("YLFramework.Editor.Buff.BuffSettingsMigration");
            Assert.That(type, Is.Not.Null, "Missing API: internal YLFramework.Editor.Buff.BuffSettingsMigration.");
            Assert.That(type.IsVisible, Is.False);
            var migrate = type.GetMethod("Migrate", PrivateStatic, null, Type.EmptyTypes, null);
            Assert.That(migrate, Is.Not.Null, "Missing API: internal static string BuffSettingsMigration.Migrate().");
            Assert.That(migrate.IsAssembly, Is.True);
            Assert.That(migrate.ReturnType, Is.EqualTo(typeof(string)));
            string backup = null;
            ObserveWrites(() => backup = (string)migrate.Invoke(null, null));
            return backup;
        }

        private BuffSettings CreatePayload(string suffix = "")
        {
            if (!_effect)
            {
                _assetFolder = "Assets/" + _owner;
                Assert.That(File.Exists(_assetFolder) || Directory.Exists(_assetFolder) ||
                    File.Exists(_assetFolder + ".meta"), Is.False);
                DomainTestAssets.OwnPath(_assetFolder);
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
                Is.EqualTo(ScriptGuid), "The legacy fixture relies on the unchanged Buff settings MonoScript GUID.");
            // The old payload uses the same GUID and serialized fields; only its exact file name changed.
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
            Assert.That(File.ReadAllText(CurrentPath), Does.Contain(_effectGuid));
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
            Assert.That(File.Exists(path) || Directory.Exists(path), Is.False);
            Directory.CreateDirectory(SettingsDirectory);
            string temporary = Path.Combine(SettingsDirectory, _owner + "-serialized-" + Guid.NewGuid().ToString("N") + ".asset");
            Assert.That(File.Exists(temporary) || Directory.Exists(temporary), Is.False);
            InternalEditorUtility.SaveToSerializedFileAndForget(objects, temporary, true);
            var serialized = Snapshot(temporary);
            _ownedFiles.Add(Path.GetFullPath(temporary), serialized);
            try
            {
                ObserveWrites(() =>
                {
                    using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    _ownedFiles.Add(Path.GetFullPath(path), (Array.Empty<byte>(), File.GetLastWriteTimeUtc(path)));
                    output.Write(serialized.Bytes, 0, serialized.Bytes.Length);
                });
            }
            finally
            {
                bool unchanged = Matches(temporary, serialized);
                if (unchanged)
                {
                    File.Delete(temporary);
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
                    if (File.Exists(path) && (_ownedFiles.ContainsKey(path) || !before.Contains(path)))
                    {
                        _ownedFiles[path] = Snapshot(path);
                    }
                }

                // A new adjacent copy of our unique payload is owned even if migration throws before returning it.
                if (_ownedFiles.TryGetValue(Path.GetFullPath(LegacyPath), out var legacy))
                {
                    foreach (string path in SettingsEntries().Except(before))
                    {
                        if (File.Exists(path) && !_ownedFiles.ContainsKey(path) &&
                            File.ReadAllBytes(path).SequenceEqual(legacy.Bytes))
                        {
                            _ownedFiles.Add(path, Snapshot(path));
                        }
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

        private void AssertMigrationFiles(string backup, byte[] original)
        {
            Assert.That(backup, Is.Not.Null.And.Not.Empty);
            string fullPath = Path.GetFullPath(backup);
            Assert.That(Path.GetDirectoryName(fullPath), Is.EqualTo(Path.GetFullPath(SettingsDirectory)).IgnoreCase,
                "Keep the backup adjacent to the exact owned legacy settings, not in an arbitrary project path.");
            Assert.That(fullPath, Is.Not.EqualTo(Path.GetFullPath(LegacyPath)).IgnoreCase);
            Assert.That(fullPath, Is.Not.EqualTo(Path.GetFullPath(CurrentPath)).IgnoreCase);
            Assert.That(_initialEntries, Does.Not.Contain(fullPath), "Never reuse a pre-existing backup.");
            Assert.That(File.Exists(LegacyPath), Is.False);
            Assert.That(File.ReadAllBytes(CurrentPath), Is.EqualTo(original));
            Assert.That(File.ReadAllBytes(fullPath), Is.EqualTo(original));
            AssertFiles(CurrentPath, fullPath);
        }

        private void AssertFiles(params string[] paths)
        {
            Assert.That(SettingsEntries(), Is.EquivalentTo(_initialEntries.Concat(paths.Select(Path.GetFullPath))));
        }

        private static string[] SettingsEntries()
        {
            return Directory.Exists(SettingsDirectory)
                ? Directory.GetFileSystemEntries(SettingsDirectory).Select(Path.GetFullPath).ToArray()
                : Array.Empty<string>();
        }

        private static (byte[] Bytes, DateTime Written) Snapshot(string path)
        {
            return (File.ReadAllBytes(path), File.GetLastWriteTimeUtc(path));
        }

        private static bool Matches(string path, (byte[] Bytes, DateTime Written) snapshot)
        {
            return File.Exists(path) && File.ReadAllBytes(path).SequenceEqual(snapshot.Bytes) &&
                File.GetLastWriteTimeUtc(path) == snapshot.Written;
        }
    }
}
