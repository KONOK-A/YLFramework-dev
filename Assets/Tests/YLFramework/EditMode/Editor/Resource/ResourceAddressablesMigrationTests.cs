#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;
using UnityEngine;
using YLFramework.Testing;
using YLFramework.Tests.Fixtures;

namespace YLFramework.Tests.Editor.Resource
{
    [Category("YL.Resource.Addressables.Migration")]
    public sealed class ResourceAddressablesMigrationTests
    {
        private string _root;

        [SetUp]
        public void SetUp()
        {
            _root = "Assets/ResourceMigrationTests_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", Path.GetFileName(_root));
        }

        [TearDown]
        public void TearDown() => AssetDatabase.DeleteAsset(_root);

        [Test]
        public void Candidate_PreservesForeignEntriesAndUsesCanonicalKeysInsteadOfAliasLabels()
        {
            var settings = AddressableAssetSettings.Create(_root + "/Settings", "Settings", true, true);
            var foreign = new TextAsset("foreign localization value");
            AssetDatabase.CreateAsset(foreign, _root + "/Foreign.asset");
            string foreignGuid = AssetDatabase.AssetPathToGUID(_root + "/Foreign.asset");
            var external = settings.CreateOrMoveEntry(foreignGuid, settings.DefaultGroup);
            external.address = "localization/foreign";
            external.SetLabel("external-label", true, true);
            var input = new TextAsset("migration value");
            AssetDatabase.CreateAsset(input, _root + "/Input.asset");
            var preview = Preview(settings, input);

            JObject result = ResourceAddressablesMigration.Convert(preview, false);

            Assert.That((string)result["status"], Is.EqualTo("CandidatePrepared"));
            Assert.That((bool)result["runtimeVerified"], Is.False);
            Assert.That(settings.DisableCatalogUpdateOnStartup, Is.True);
            Assert.That(settings.FindAssetEntry(foreignGuid), Is.SameAs(external));
            Assert.That(external.address, Is.EqualTo("localization/foreign"));
            Assert.That(external.labels, Contains.Item("external-label"));
            var migrated = settings.FindAssetEntry(AssetDatabase.AssetPathToGUID(_root + "/Input.asset"));
            Assert.That(migrated.address, Is.EqualTo("project/data/input"));
            Assert.That(migrated.labels, Contains.Item("project/main"));
            Assert.That(migrated.labels, Does.Not.Contain("project/old-alias"));
        }

        [Test]
        public void Candidate_InvalidLocalIdStopsBeforeCreatingAnyGroupsOrChangingCatalogPolicy()
        {
            var settings = AddressableAssetSettings.Create(_root + "/Settings", "Settings", true, true);
            var input = new TextAsset("migration value");
            AssetDatabase.CreateAsset(input, _root + "/Input.asset");
            var preview = Preview(settings, input);
            preview["entries"][0]["localId"] = 123456789;
            int count = settings.groups.Count;
            bool policy = settings.DisableCatalogUpdateOnStartup;

            Assert.Throws<InvalidOperationException>(() => ResourceAddressablesMigration.Convert(preview, false));

            Assert.That(settings.groups.Count, Is.EqualTo(count));
            Assert.That(settings.DisableCatalogUpdateOnStartup, Is.EqualTo(policy));
            Assert.That(settings.FindAssetEntry(AssetDatabase.AssetPathToGUID(_root + "/Input.asset")), Is.Null);
        }

        [Test]
        public void Candidate_AddressOrExternalLabelCollisionDoesNotMoveExistingEntry()
        {
            var settings = AddressableAssetSettings.Create(_root + "/Settings", "Settings", true, true);
            var foreign = new TextAsset("foreign");
            AssetDatabase.CreateAsset(foreign, _root + "/Foreign.asset");
            var entry = settings.CreateOrMoveEntry(AssetDatabase.AssetPathToGUID(_root + "/Foreign.asset"), settings.DefaultGroup);
            entry.address = "foreign/key";
            entry.SetLabel("project/data/input", true, true);
            var input = new TextAsset("migration value");
            AssetDatabase.CreateAsset(input, _root + "/Input.asset");

            Assert.Throws<InvalidOperationException>(() => ResourceAddressablesMigration.Convert(Preview(settings, input), false));

            Assert.That(entry.address, Is.EqualTo("foreign/key"));
            Assert.That(entry.labels, Contains.Item("project/data/input"));
        }

        [Test]
        public void Candidate_RewritesOnlyTheCertifiedAliasFieldAndPreservesUnknownValues()
        {
            var settings = AddressableAssetSettings.Create(_root + "/Settings", "Settings", true, true);
            var input = new TextAsset("migration value");
            AssetDatabase.CreateAsset(input, _root + "/Input.asset");
            var consumer = ScriptableObject.CreateInstance<ResourceMigrationProbe>();
            consumer.entries = new[]
            {
                new ResourceMigrationProbe.Entry { address = "project/old-alias", aliases = new[] { "untouched-text" }, reference = input }
            };
            consumer.offset = new Vector3(9, 8, 7);
            string path = _root + "/Consumer.asset";
            AssetDatabase.CreateAsset(consumer, path);
            AssetDatabase.SaveAssetIfDirty(consumer);
            Assert.That(AssetDatabase.TryGetGUIDAndLocalFileIdentifier(consumer, out string guid, out long id), Is.True);
            var preview = Preview(settings, input);
            const string field = "entries.Array.data[0].address";
            using var serialized = new SerializedObject(consumer);
            using var property = serialized.FindProperty(field);
            preview["consumers"] = new JArray(new JObject
            {
                ["assetPath"] = path, ["guid"] = guid, ["localId"] = id, ["type"] = consumer.GetType().AssemblyQualifiedName,
                ["properties"] = new JObject
                {
                    [field] = new JObject { ["propertyType"] = "String", ["type"] = property.type, ["value"] = "project/old-alias" }
                }
            });
            preview["rewrites"] = new JArray(new JObject
            {
                ["path"] = path, ["guid"] = guid, ["localId"] = id, ["propertyPath"] = field,
                ["oldValue"] = "project/old-alias", ["newValue"] = "project/data/input"
            });

            ResourceAddressablesMigration.Convert(preview, false);

            Assert.That(consumer.entries[0].address, Is.EqualTo("project/data/input"));
            Assert.That(consumer.entries[0].aliases, Is.EqualTo(new[] { "untouched-text" }));
            Assert.That(consumer.entries[0].reference, Is.SameAs(input));
            Assert.That(consumer.offset, Is.EqualTo(new Vector3(9, 8, 7)));
            Assert.That(consumer.mode, Is.EqualTo(ResourceMigrationProbe.SourceMode.Resource));
        }

        [Test]
        public void Candidate_RefusesUnknownGroupFilesBeforeAnyOfficialWrite()
        {
            var settings = AddressableAssetSettings.Create(_root + "/Settings", "Settings", true, true);
            var input = new TextAsset("migration value");
            AssetDatabase.CreateAsset(input, _root + "/Input.asset");
            string occupied = settings.GroupFolder + "/YLResource_project__main.asset";
            AssetDatabase.CreateAsset(new TextAsset("unknown owner"), occupied);
            string original = File.ReadAllText(occupied);
            int count = settings.groups.Count;
            bool policy = settings.DisableCatalogUpdateOnStartup;

            Assert.Throws<InvalidOperationException>(() => ResourceAddressablesMigration.Convert(Preview(settings, input), false));

            Assert.That(File.ReadAllText(occupied), Is.EqualTo(original));
            Assert.That(settings.groups.Count, Is.EqualTo(count));
            Assert.That(settings.DisableCatalogUpdateOnStartup, Is.EqualTo(policy));
        }

        [Test]
        public void Candidate_RefusesNewSettingsInAnOccupiedConfigurationNamespace()
        {
            var settings = AddressableAssetSettings.Create(_root + "/Settings", "Settings", true, true);
            var input = new TextAsset("migration value");
            AssetDatabase.CreateAsset(input, _root + "/Input.asset");
            var preview = Preview(settings, input);
            string target = settings.ConfigFolder + "/OtherSettings.asset";
            preview["settingsPath"] = target;
            preview["settingsExpectedAbsent"] = true;

            Assert.Throws<InvalidOperationException>(() => ResourceAddressablesMigration.Convert(preview, false));

            Assert.That(File.Exists(target), Is.False);
            Assert.That(AssetDatabase.LoadAssetAtPath<AddressableAssetSettings>(AssetDatabase.GetAssetPath(settings)), Is.SameAs(settings));
        }

        [Test]
        public void ExistingSettingsProof_AllowsOnlyCertifiedAdditionsAndPreservesForeignLocalization()
        {
            var settings = ExistingSettings(out var input);
            var preview = Preview(settings, input);
            var before = ResourceAddressablesCandidateValidation.SettingsFootprint(settings);
            var frozen = before.DeepClone();

            ResourceAddressablesMigration.Convert(preview, false);
            var after = ResourceAddressablesCandidateValidation.SettingsFootprint(settings);
            after["groups"] = new JArray(((JArray)after["groups"]).Reverse().Select(value => value.DeepClone()));
            after["settings"]["fields"]["m_GroupAssets"] = new JArray(((JArray)after["settings"]["fields"]["m_GroupAssets"]).Reverse().Select(value => value.DeepClone()));
            var evidence = ResourceAddressablesSettingsProof.Verify(before, after, preview);

            Assert.That((bool)evidence["existingSettingsVerified"], Is.True);
            Assert.That((int)evidence["baselineForeignEntries"], Is.EqualTo(1));
            Assert.That(JToken.DeepEquals(before, frozen), Is.True, "Comparison must not mutate a shared groups/labels/profile snapshot.");
            Assert.That(settings.FindAssetEntry(AssetDatabase.AssetPathToGUID(_root + "/Foreign.asset")).address, Is.EqualTo("localization/foreign"));
        }

        [TestCase("schema")]
        [TestCase("profile")]
        [TestCase("default-config")]
        [TestCase("unrelated-settings")]
        public void ExistingSettingsProof_RejectsUnrelatedChanges(string changed)
        {
            var settings = ExistingSettings(out var input);
            var preview = Preview(settings, input);
            var before = ResourceAddressablesCandidateValidation.SettingsFootprint(settings);
            ResourceAddressablesMigration.Convert(preview, false);
            switch (changed)
            {
                case "schema":
                    var schema = settings.DefaultGroup.GetSchema<BundledAssetGroupSchema>();
                    schema.Compression = schema.Compression == BundledAssetGroupSchema.BundleCompressionMode.Uncompressed ? BundledAssetGroupSchema.BundleCompressionMode.LZ4 : BundledAssetGroupSchema.BundleCompressionMode.Uncompressed;
                    EditorUtility.SetDirty(schema);
                    AssetDatabase.SaveAssetIfDirty(schema);
                    break;
                case "profile":
                    settings.profileSettings.SetValue(settings.activeProfileId, AddressableAssetSettings.kLocalLoadPath, "altered-foreign-profile");
                    EditorUtility.SetDirty(settings);
                    AssetDatabase.SaveAssetIfDirty(settings);
                    break;
                case "unrelated-settings":
                    settings.BuildRemoteCatalog = !settings.BuildRemoteCatalog;
                    EditorUtility.SetDirty(settings);
                    AssetDatabase.SaveAssetIfDirty(settings);
                    break;
            }
            var after = ResourceAddressablesCandidateValidation.SettingsFootprint(settings);
            if (changed == "default-config")
            {
                // Tamper with an actual captured identity, never the source registration or YAML.
                after["registration"]["defaultSettings"] = after["settings"]["identity"].DeepClone();
                if (JToken.DeepEquals(before["registration"]["defaultSettings"], after["registration"]["defaultSettings"]))
                    after["registration"]["defaultSettings"] = JValue.CreateNull();
            }
            Assert.Throws<InvalidOperationException>(() => ResourceAddressablesSettingsProof.Verify(before, after, preview));
        }

        private AddressableAssetSettings ExistingSettings(out TextAsset input)
        {
            var settings = AddressableAssetSettings.Create(_root + "/Settings", "Settings", true, true);
            var foreign = new TextAsset("foreign localization value");
            AssetDatabase.CreateAsset(foreign, _root + "/Foreign.asset");
            var entry = settings.CreateOrMoveEntry(AssetDatabase.AssetPathToGUID(_root + "/Foreign.asset"), settings.DefaultGroup);
            entry.SetAddress("localization/foreign");
            entry.SetLabel("localization/ja", true, true);
            settings.profileSettings.AddProfile("Foreign localization profile", settings.activeProfileId);
            input = new TextAsset("migration value");
            AssetDatabase.CreateAsset(input, _root + "/Input.asset");
            foreach (var group in settings.groups.Where(group => group))
            {
                foreach (var schema in group.Schemas) AssetDatabase.SaveAssetIfDirty(schema);
                AssetDatabase.SaveAssetIfDirty(group);
            }
            AssetDatabase.SaveAssetIfDirty(settings);
            return settings;
        }

        private JObject Preview(AddressableAssetSettings settings, UnityEngine.Object input)
        {
            Assert.That(AssetDatabase.TryGetGUIDAndLocalFileIdentifier(input, out string guid, out long id), Is.True);
            return new JObject
            {
                ["schemaVersion"] = 1, ["purpose"] = "YLResource.AddressablesMigrationPreview",
                ["status"] = "ReadyForCandidate", ["previewId"] = Guid.NewGuid().ToString("N"),
                ["settingsPath"] = AssetDatabase.GetAssetPath(settings), ["settingsExpectedAbsent"] = false,
                ["settingsRoot"] = settings.ConfigFolder.Replace('\\', '/'), ["useAsDefault"] = false,
                ["inputs"] = new JArray(), ["blockers"] = new JArray(), ["projects"] = new JArray(), ["consumers"] = new JArray(), ["rewrites"] = new JArray(),
                ["entries"] = new JArray(new JObject
                {
                    ["path"] = AssetDatabase.GetAssetPath(input), ["guid"] = guid, ["localId"] = id,
                    ["type"] = input.GetType().AssemblyQualifiedName, ["address"] = "project/data/input",
                    ["group"] = "YLResource_project__main", ["label"] = "project/main", ["packing"] = "Together", ["compression"] = "Lz4",
                    ["selectionPath"] = AssetDatabase.GetAssetPath(input), ["selectionGuid"] = guid, ["subAssets"] = new JArray()
                }),
                ["aliases"] = new JArray(new JObject { ["legacy"] = "project/old-alias", ["canonical"] = "project/data/input", ["guid"] = guid })
            };
        }
    }
}
#endif
