using System;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;
using YLFramework.Integrations.HybridCLR;
using YLFramework.Integrations.HybridCLR.Editor;
using YLFramework.Resource;

namespace YLFramework.Tests.Integrations.HybridCLR
{
    [Category("YL.Resource.Addressables")]
    public sealed class HybridCLRAddressablesTests
    {
        private string _root;
        private HybridCLRBuildProfile _profile;
        private AddressableAssetSettings _settings;

        [SetUp]
        public void SetUp()
        {
            var folder = "YLHybridCLRAddressables-" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", folder);
            _root = "Assets/" + folder;
            _settings = AddressableAssetSettings.Create(_root, "Settings", true, true);
            _settings.DisableCatalogUpdateOnStartup = true;
            _settings.BuildRemoteCatalog = true;
            var schema = _settings.DefaultGroup.GetSchema<BundledAssetGroupSchema>();
            schema.BuildPath.SetVariableByName(_settings, AddressableAssetSettings.kRemoteBuildPath);
            schema.LoadPath.SetVariableByName(_settings, AddressableAssetSettings.kRemoteLoadPath);
            _profile = UnityEngine.ScriptableObject.CreateInstance<HybridCLRBuildProfile>();
            _profile.schemaVersion = 2;
            _profile.addressableSettings = _settings;
            _profile.codeGroup = _settings.DefaultGroup;
            _profile.projectId = "hot-demo";
            _profile.appContentCompatibilityId = "aa-base-v2";
            _profile.generatedAssetDirectory = _root + "/Generated";
            _profile.baselineDirectory = "Build/HybridCLR/AddressablesTest-" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateAsset(_profile, _root + "/Profile.asset");
            AssetDatabase.SaveAssetIfDirty(_profile);
        }

        [TearDown]
        public void TearDown() => AssetDatabase.DeleteAsset(_root);

        [Test]
        public void LegacyProfileIsRejectedWithoutReadingItsRecipeOrChangingData()
        {
            _profile.schemaVersion = 1;
            var before = EditorJsonUtility.ToJson(_profile);
            Assert.Throws<InvalidDataException>(() => HybridCLRBuildPipeline.ValidateResourceInputs(_profile));
            Assert.That(EditorJsonUtility.ToJson(_profile), Is.EqualTo(before));
        }

        [Test]
        public void ExecutionInputsRejectConcurrentChangesToOfficialAuthoringFacts()
        {
            foreach (var group in _settings.groups)
            {
                if (!group)
                {
                    continue;
                }
                foreach (var schema in group.Schemas)
                {
                    AssetDatabase.SaveAssetIfDirty(schema);
                }
                AssetDatabase.SaveAssetIfDirty(group);
            }
            AssetDatabase.SaveAssetIfDirty(_settings);
            var sdk = global::HybridCLR.Settings.HybridCLRSettings.instance;
            var definitions = sdk.hotUpdateAssemblyDefinitions;
            var names = sdk.hotUpdateAssemblies;
            var preserved = sdk.preserveHotUpdateAssemblies;
            try
            {
                sdk.hotUpdateAssemblyDefinitions = Array.Empty<UnityEditorInternal.AssemblyDefinitionAsset>();
                sdk.hotUpdateAssemblies = new[] { "YLResourceAuthoringFixtureHot" };
                sdk.preserveHotUpdateAssemblies = Array.Empty<string>();
                var inputs = HybridCLRBuildInputs.Capture(_profile);
                _settings.AddLabel("concurrent-authoring-edit");
                AssetDatabase.SaveAssetIfDirty(_settings);
                Assert.Throws<InvalidDataException>(() => inputs.Reload());
            }
            finally
            {
                sdk.hotUpdateAssemblyDefinitions = definitions;
                sdk.hotUpdateAssemblies = names;
                sdk.preserveHotUpdateAssemblies = preserved;
            }
        }

        [Test]
        public void GeneratedCodeUsesOfficialEntriesWithCanonicalProjectPathKeys()
        {
            var manifest = CreatePayloads();
            HybridCLRBuildPipeline.RegisterResourceEntries(_profile, manifest);

            var path = _profile.generatedAssetDirectory + "/" + HybridCLRManifest.ManifestPath;
            var entry = _settings.FindAssetEntry(AssetDatabase.AssetPathToGUID(path));
            Assert.That(entry.address, Is.EqualTo("hot-demo/hybridclr/manifest.json"));
            Assert.That(entry.parentGroup, Is.SameAs(_profile.codeGroup));
            var code = manifest.HotUpdateAssemblies[0];
            var codeEntry = _settings.FindAssetEntry(AssetDatabase.AssetPathToGUID(_profile.generatedAssetDirectory + "/" + code.Address));
            Assert.That(codeEntry.address, Is.EqualTo("hot-demo/hybridclr/assemblies/Example.Entry.dll.bytes"));
            Assert.That(codeEntry.AssetPath, Does.StartWith(_profile.generatedAssetDirectory + "/"));
        }

        [Test]
        public void ConflictingOfficialAddressStopsTheWholeRegistrationBeforeAddingEntries()
        {
            var manifest = CreatePayloads();
            var path = _root + "/external.txt";
            File.WriteAllText(path, "external owner");
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            var external = _settings.CreateOrMoveEntry(AssetDatabase.AssetPathToGUID(path), _settings.DefaultGroup);
            external.SetAddress("hot-demo/hybridclr/manifest.json");

            Assert.Throws<InvalidDataException>(() => HybridCLRBuildPipeline.RegisterResourceEntries(_profile, manifest));

            Assert.That(external.AssetPath, Is.EqualTo(path));
            Assert.That(external.address, Is.EqualTo("hot-demo/hybridclr/manifest.json"));
            var codePath = _profile.generatedAssetDirectory + "/" + manifest.HotUpdateAssemblies[0].Address;
            Assert.That(_settings.FindAssetEntry(AssetDatabase.AssetPathToGUID(codePath)), Is.Null);
        }

        [Test]
        public void AddressUsedAsAnOfficialLabelCannotAliasTheCodeManifest()
        {
            var manifest = CreatePayloads();
            var path = _root + "/label-owner.txt";
            File.WriteAllText(path, "label owner");
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            _settings.AddLabel("hot-demo/hybridclr/manifest.json");
            var other = _settings.CreateOrMoveEntry(AssetDatabase.AssetPathToGUID(path), _settings.DefaultGroup);
            other.SetAddress("other/text");
            other.SetLabel("hot-demo/hybridclr/manifest.json", true);

            Assert.Throws<InvalidDataException>(() => HybridCLRBuildPipeline.RegisterResourceEntries(_profile, manifest));

            Assert.That(other.labels, Does.Contain("hot-demo/hybridclr/manifest.json"));
        }

        [Test]
        public void RemovingOwnedGeneratedCodeRetiresItsOfficialEntryWithoutTouchingOtherEntries()
        {
            var first = HybridCLRAssemblyAnalysis.Read(typeof(HybridCLRManifest).Assembly.Location);
            var retired = HybridCLRAssemblyAnalysis.Read(typeof(ResourceAddress).Assembly.Location);
            var full = new HybridCLRAssemblySet { HotUpdate = new[] { first, retired }, Aot = Array.Empty<HybridCLRAssemblyRecord>() };
            var manifest = Describe(first, retired);
            HybridCLRBuildPipeline.CommitPayloads(_profile, manifest, full);
            HybridCLRBuildPipeline.RegisterResourceEntries(_profile, manifest);
            var retiredPath = _profile.generatedAssetDirectory + "/" + HybridCLRManifest.PayloadAddress(retired.Name, false);
            var retiredGuid = AssetDatabase.AssetPathToGUID(retiredPath);
            var externalPath = _root + "/unrelated.txt";
            File.WriteAllText(externalPath, "unrelated");
            AssetDatabase.ImportAsset(externalPath, ImportAssetOptions.ForceSynchronousImport);
            var unrelated = _settings.CreateOrMoveEntry(AssetDatabase.AssetPathToGUID(externalPath), _settings.DefaultGroup);
            unrelated.SetAddress("unrelated/text");

            var reduced = new HybridCLRAssemblySet { HotUpdate = new[] { first }, Aot = Array.Empty<HybridCLRAssemblyRecord>() };
            var patch = Describe(first);
            HybridCLRBuildPipeline.CommitPayloads(_profile, patch, reduced);
            HybridCLRBuildPipeline.RegisterResourceEntries(_profile, patch);

            Assert.That(_settings.FindAssetEntry(retiredGuid), Is.Null);
            Assert.That(File.Exists(retiredPath), Is.False);
            Assert.That(unrelated.address, Is.EqualTo("unrelated/text"));
            Assert.That(File.Exists(externalPath), Is.True);
        }

        [Test]
        public void ChangedGeneratedPayloadFailsBeforeRegisteringAnyOfficialEntry()
        {
            var manifest = CreatePayloads();
            File.WriteAllBytes(_profile.generatedAssetDirectory + "/" + manifest.HotUpdateAssemblies[0].Address,
                new byte[] { 3, 2, 1 });

            Assert.Throws<InvalidDataException>(() => HybridCLRBuildPipeline.RegisterResourceEntries(_profile, manifest));

            var manifestPath = _profile.generatedAssetDirectory + "/" + HybridCLRManifest.ManifestPath;
            Assert.That(_settings.FindAssetEntry(AssetDatabase.AssetPathToGUID(manifestPath)), Is.Null);
        }

        [Test]
        public void LegacyBaselineIsRejectedAndNotUpgradedInPlace()
        {
            var root = Path.Combine(Path.GetTempPath(), "kilo", "YLLegacyBase-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var bytes = Encoding.UTF8.GetBytes("{\"SchemaVersion\":1}");
            File.WriteAllBytes(root + "/baseline.json", bytes);
            File.WriteAllText(root + "/complete.json", JsonConvert.SerializeObject(new
            {
                schemaVersion = 1,
                baselineSha256 = HybridCLRManifest.Hash(bytes)
            }));
            try
            {
                Assert.Throws<InvalidDataException>(() => HybridCLRBuildBaseline.Read(root));
                CollectionAssert.AreEqual(bytes, File.ReadAllBytes(root + "/baseline.json"));
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        [Test]
        public void LegacyManifestCannotBeResealedAsAnAddressablesCodePackage()
        {
            var manifest = CreatePayloads();
            manifest.SchemaVersion = 1;
            Assert.Throws<InvalidDataException>(() => manifest.Seal());
        }

        private HybridCLRManifest CreatePayloads()
        {
            var bytes = new byte[] { 1, 2, 3 };
            var payload = new HybridCLRPayload
            {
                Name = "Example.Entry",
                Identity = "Example.Entry, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null",
                Mvid = "f594503a-2d03-41ee-b578-68526adc31c4",
                Address = "hybridclr/assemblies/Example.Entry.dll.bytes",
                Length = bytes.Length,
                Sha256 = HybridCLRManifest.Hash(bytes)
            };
            var manifest = new HybridCLRManifest
            {
                ProjectId = "hot-demo",
                AppContentCompatibilityId = "aa-base-v2",
                UnityVersion = UnityEngine.Application.unityVersion,
                HotUpdateAssemblies = new[] { payload }
            };
            manifest.Seal();
            Directory.CreateDirectory(_profile.generatedAssetDirectory + "/hybridclr/assemblies");
            File.WriteAllBytes(_profile.generatedAssetDirectory + "/" + payload.Address, bytes);
            File.WriteAllText(_profile.generatedAssetDirectory + "/" + HybridCLRManifest.ManifestPath, manifest.ToJson());
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            return manifest;
        }

        private HybridCLRManifest Describe(params HybridCLRAssemblyRecord[] assemblies)
        {
            var manifest = new HybridCLRManifest
            {
                ProjectId = _profile.projectId,
                AppContentCompatibilityId = _profile.appContentCompatibilityId,
                UnityVersion = UnityEngine.Application.unityVersion,
                Development = _profile.development,
                HotUpdateAssemblies = assemblies.Select(assembly => new HybridCLRPayload
                {
                    Name = assembly.Name,
                    Identity = assembly.Identity,
                    Mvid = assembly.Mvid,
                    Sha256 = assembly.Sha256,
                    Length = assembly.Length,
                    Address = HybridCLRManifest.PayloadAddress(assembly.Name, false)
                }).ToArray()
            };
            manifest.Seal();
            return manifest;
        }
    }
}
