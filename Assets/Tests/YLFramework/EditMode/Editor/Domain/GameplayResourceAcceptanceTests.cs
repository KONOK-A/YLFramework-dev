using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;
using UnityEngine;
using UnityEngine.SceneManagement;
using YLFramework.Buff;
using YLFramework.Editor.Resource;
using YLFramework.Equipment;
using YLFramework.Mission;
using YLFramework.Resource;
using YLFramework.Skill;
using YLFramework.Testing.EditorFixtures;

namespace YLFramework.Tests.DomainAcceptance
{
    [Category("YL.Module.Domain")]
    [Category("YL.Special.GameplayResource")]
    [Category("YL.Special.GameplaySession")]
    [Category("YL.Package.GameplayStandalone")]
    public sealed class GameplayResourceAcceptanceTests
    {
        private const string ProbeAssembly = "YLFramework.Tests.Domain.Probes";
        private const string ReceiverNamespace = "YLFramework.Tests.DomainAcceptance.";
        private static readonly string[] _frameworkAssemblies =
        {
            "YLFramework.Runtime", "YLFramework.Skill", "YLFramework.Buff", "YLFramework.Mission", "YLFramework.Equipment",
            ProbeAssembly
        };
        private static readonly Type[] _assetTypes =
        {
            typeof(SkillsConfig), typeof(SkillConfigData), typeof(BuffsConfig), typeof(BuffConfigData), typeof(EffectData),
            typeof(MissionsConfig), typeof(MissionConfigData), typeof(EquipmentsConfig), typeof(EquipmentConfigData)
        };
        private string _folder;
        private string _folderGuid;
        private string _settingsGuid;
        private string _groupPath;
        private string _groupGuid;
        private string[] _schemaGuids;
        private Dictionary<string, string> _entries;

        [Serializable]
        private sealed class HostOwner
        {
            public int schemaVersion;
            public string purpose;
            public string kind;
            public string path;
        }

        [Test]
        public void NewFourDomainPublicationRejectsOldAssemblyTuplesAndRunsInTwoOwnedMonoPlayers()
        {
            Assert.That(Application.isBatchMode && !EditorApplication.isPlayingOrWillChangePlaymode, Is.True);
            Assert.That(EditorApplication.isCompiling || EditorApplication.isUpdating, Is.False);
            string output = global::YLFramework.Testing.Editor.YLTestEntry.RequireOwnedRunOutput();
            Assert.That(Directory.Exists(output), Is.True, "Never create or adopt an unowned result directory.");
            string projectPath = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            var hostOwner = JsonUtility.FromJson<HostOwner>(File.ReadAllText(Path.Combine(projectPath, ".yl-owner.json")));
            Assert.That(hostOwner, Is.Not.Null);
            Assert.That(hostOwner.schemaVersion, Is.EqualTo(1));
            Assert.That(hostOwner.purpose, Is.EqualTo("YLTesting.Directory"));
            Assert.That(hostOwner.kind, Is.EqualTo("Host"));
            Assert.That(Path.GetFullPath(hostOwner.path), Is.EqualTo(projectPath).IgnoreCase);
            for (var directory = new DirectoryInfo(projectPath); directory != null; directory = directory.Parent)
                Assert.That((directory.Attributes & FileAttributes.ReparsePoint) == 0, Is.True);
            var settings = AddressableAssetSettingsDefaultObject.GetSettings(false);
            Assert.That(settings, Is.Not.Null, "Prepare existing official default settings; never replace unknown authoring settings here.");
            Assert.That(settings.BuildAddressablesWithPlayerBuild, Is.EqualTo(AddressableAssetSettings.PlayerBuildOption.DoNotBuildWithPlayer));
            ResourceEditorOperations.ValidateBuild(settings);
            _settingsGuid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(settings));
            Assert.That(_settingsGuid, Is.Not.Empty);
            Assert.That(ResourceSystem.Current, Is.Null, "Do not take over another Resource authority.");
            Assert.That(SkillManager.ConfigCount + BuffManager.ConfigCount + MissionManager.ConfigCount, Is.Zero);
            Assert.That(SkillManager.HostCount + BuffManager.HostCount + BuffManager.EffectOwnerCount, Is.Zero);
            Assert.That(EquipmentManager.GetConfig("Equipment"), Is.Null);
            CollectionAssert.AreEquivalent(new[] { "com.kano.ylframework", "com.kano.ylframework.gameplay" },
                UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages()
                    .Where(package => package.name.StartsWith("com.kano.ylframework", StringComparison.Ordinal)).Select(package => package.name));
            Assert.That(EditorUserBuildSettings.activeBuildTarget, Is.EqualTo(BuildTarget.StandaloneWindows64));
            Assert.That(BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.Standalone, BuildTarget.StandaloneWindows64), Is.True);
            for (int index = 0; index < SceneManager.sceneCount; index++)
            {
                var scene = SceneManager.GetSceneAt(index);
                Assert.That(scene.isDirty, Is.False, "Never save or replace an unknown dirty scene.");
                Assert.That(scene.rootCount, Is.Zero);
                Assert.That(string.IsNullOrEmpty(scene.path) || scene.path == "Assets/YLTestingProfile/Empty.unity", Is.True);
            }
            foreach (string name in new[] { "standalone-player-location.txt", "StandalonePlayer-1.json", "StandalonePlayer-2.json" })
            {
                Assert.That(File.Exists(Path.Combine(output, name)) || Directory.Exists(Path.Combine(output, name)), Is.False);
            }

            string token = Guid.NewGuid().ToString("N");
            string folder = "Assets/__YLGameplay_" + token;
            Assert.That(Directory.Exists(folder) || File.Exists(folder) || File.Exists(folder + ".meta") || AssetDatabase.IsValidFolder(folder), Is.False);
            string folderGuid = AssetDatabase.CreateFolder("Assets", folder.Substring("Assets/".Length));
            Assert.That(folderGuid, Is.Not.Null.And.Not.Empty);
            _folder = folder;
            _folderGuid = folderGuid;
            Assert.That(AssetDatabase.CreateFolder(folder, "Content"), Is.Not.Empty);
            CreateContent(token);
            string projectId = "gp-" + token.Substring(0, 8);
            RegisterEntries(projectId);
            var built = ResourceEditorOperations.BuildContent(Settings(), Path.Combine(output, "gameplay-content-state.bin"));
            string contentHash = StandaloneFixturePlayer.ContentHash(built);
            var roots = new[] { ("Skills", typeof(SkillsConfig)), ("Buffs", typeof(BuffsConfig)), ("Missions", typeof(MissionsConfig)), ("Equipment", typeof(EquipmentsConfig)) };
            foreach (var root in roots)
            {
                var asset = AssetDatabase.LoadMainAssetAtPath(folder + "/Content/" + root.Item1 + ".asset");
                Assert.That(asset, Is.Not.Null, "Reacquire the actual saved root after the official build.");
                Assert.That(asset.GetType(), Is.EqualTo(root.Item2));
            }
            var objects = roots.SelectMany(root => AssetDatabase.LoadAllAssetsAtPath(folder + "/Content/" + root.Item1 + ".asset").OfType<ScriptableObject>()).ToArray();
            Assert.That(objects, Has.Length.EqualTo(11));
            Assert.That(objects.Count(AssetDatabase.IsMainAsset), Is.EqualTo(4));
            foreach (var expected in new[] { ("YLFramework.Skill", 2), ("YLFramework.Buff", 4), ("YLFramework.Mission", 3), ("YLFramework.Equipment", 2) })
            {
                Assert.That(objects.Count(asset => asset.GetType().Assembly.GetName().Name == expected.Item1), Is.EqualTo(expected.Item2));
            }
            var identities = new HashSet<string>(StringComparer.Ordinal);
            foreach (var asset in objects)
            {
                Assert.That(_assetTypes, Does.Contain(asset.GetType()));
                Assert.That(MonoScript.FromScriptableObject(asset).GetClass(), Is.EqualTo(asset.GetType()), "Serialized script identity must resolve to the actual new Runtime type.");
                Assert.That(AssetDatabase.TryGetGUIDAndLocalFileIdentifier(asset, out string guid, out long localId), Is.True);
                Assert.That(guid, Is.Not.Empty);
                Assert.That(localId, Is.Not.Zero);
                Assert.That(identities.Add(guid + "/" + localId), Is.True, "Every saved root/row/effect needs a distinct real asset identity.");
            }
            // Official catalogs have no private tuple descriptor; old Runtime type names must remain unresolvable.
            foreach (var type in _assetTypes)
                Assert.That(Type.GetType(type.FullName + ", YLFramework.Runtime", false), Is.Null, "Do not restore an old Runtime tuple through a compatibility type or forwarder.");
            var definitions = Directory.GetFiles(folder + "/Content", "*.asset")
                .SelectMany(path => new[] { path, path + ".meta" }).ToDictionary(path => path, File.ReadAllBytes);

            // Keep only addresses and value tokens across BuildAndRun's scene switches, never native asset handles.
            StandaloneFixturePlayer.BuildAndRun(output, folder, "YLFramework.Skill", built, scene =>
            {
                var type = Type.GetType(ReceiverNamespace + "GameplayResourcePlayerProbe, " + ProbeAssembly, true);
                var host = new GameObject("GameplayResourceStandaloneProbe");
                host.SetActive(false);
                SceneManager.MoveGameObjectToScene(host, scene);
                using (var serialized = new SerializedObject(host.AddComponent(type)))
                {
                    serialized.FindProperty("projectId").stringValue = projectId;
                    serialized.FindProperty("contentHash").stringValue = contentHash;
                    serialized.FindProperty("contentToken").stringValue = token;
                    serialized.ApplyModifiedPropertiesWithoutUndo();
                }
                host.SetActive(true);
            }, new[] { "YL_DOMAIN_ACCEPTANCE" });
            string player = File.ReadAllText(Path.Combine(output, "standalone-player-location.txt"));
            CollectionAssert.AreEquivalent(_frameworkAssemblies.Select(name => name + ".dll"),
                Directory.GetFiles(Path.Combine(player, "P_Data/Managed"), "YLFramework*.dll").Select(Path.GetFileName), "No other feature or Editor assembly may enter the real Player.");
            for (int run = 1; run <= 2; run++)
            {
                var report = JObject.Parse(File.ReadAllText(Path.Combine(output, "StandalonePlayer-" + run + ".json")));
                foreach (var expected in new[] { ("skillAssembly", "YLFramework.Skill"), ("buffAssembly", "YLFramework.Buff"),
                    ("missionAssembly", "YLFramework.Mission"), ("equipmentAssembly", "YLFramework.Equipment"), ("probeAssembly", ProbeAssembly) })
                {
                    Assert.That((string)report[expected.Item1], Is.EqualTo(expected.Item2));
                }
                CollectionAssert.AreEquivalent(_frameworkAssemblies, report["frameworkAssemblies"].ToObject<string[]>());
                Assert.That((string)report["contentToken"], Is.EqualTo(token));
                Assert.That((int)report["schemaVersion"], Is.EqualTo(2));
                Assert.That((string)report["contentHash"], Is.EqualTo(contentHash));
                Assert.That((int)report["definitionAssetCount"], Is.EqualTo(11));
                Assert.That((bool)report["ledgerMeasured"], Is.True);
                Assert.That((string)report["resourceState"], Is.EqualTo("Closed"));
                foreach (string count in new[] { "leaseCount", "scopeCount", "requestCount", "pendingWorkCount", "drainingHandleCount" })
                    Assert.That((int)report[count], Is.Zero, count + ": " + report);
                foreach (string flag in new[] { "fourRootsLoaded", "skillLifecycle", "buffLifecycle", "missionLifecycle", "rewardDefinitions",
                    "equipmentLifecycle", "borrowedOwnership", "configsUnchanged", "independentRootSurvived", "managersClosed", "resourceReturned" })
                {
                    Assert.That((bool)report[flag], Is.True, flag + ": " + report);
                }
                Assert.That((int)report["skillCompletions"], Is.EqualTo(1));
                Assert.That((int)report["skillInterruptions"], Is.EqualTo(1));
                Assert.That((int)report["buffAdds"], Is.EqualTo(5));
                Assert.That((int)report["buffRemoves"], Is.EqualTo(5));
                Assert.That((int)report["missionCompletions"], Is.EqualTo(4));
                CollectionAssert.AreEqual(new[] { "loaded", "equip", "unequip", "equip", "unequip", "unloaded" }, report["equipmentCallbacks"].ToObject<string[]>());
            }
            foreach (var definition in definitions)
            {
                CollectionAssert.AreEqual(definition.Value, File.ReadAllBytes(definition.Key), "The acceptance must not dirty its authoring definitions.");
            }
            Assert.That(ResourceSystem.Current, Is.Null);
        }

        private void CreateContent(string token)
        {
            var skills = ScriptableObject.CreateInstance<SkillsConfig>();
            skills.name = "Skills";
            var skill = ScriptableObject.CreateInstance<SkillConfigData>();
            skill.name = "DurationSkill";
            skill.id = 101;
            skill.parent = skills;
            skill.description = token;
            skill.duration = 0.05f;
            skill.cooldownSeconds = 0.05f;
            skill.allowCancellation = true;
            skill.skillClassFullName = ReceiverNamespace + "ReceiverSkill";
            skills.skills.Add(skill);
            SaveRoot(skills, skill);

            var buffs = ScriptableObject.CreateInstance<BuffsConfig>();
            buffs.name = "Buffs";
            var effect = ScriptableObject.CreateInstance<EffectData>();
            effect.name = "PublishedEffect";
            effect.id = 301;
            effect.description = token;
            foreach (var item in new[] { (201, BuffMode.Single), (202, BuffMode.Additive) })
            {
                var buff = ScriptableObject.CreateInstance<BuffConfigData>();
                buff.name = "Buff" + item.Item1;
                buff.id = item.Item1;
                buff.parent = buffs;
                buff.description = token;
                buff.duration = 0.05f;
                buff.mode = item.Item2;
                buff.buffClassFullName = ReceiverNamespace + "ReceiverBuff";
                buff.effects.Add(effect);
                buffs.buffs.Add(buff);
            }
            SaveRoot(buffs, buffs.buffs.Cast<ScriptableObject>().Concat(new[] { effect }).ToArray());

            var missions = ScriptableObject.CreateInstance<MissionsConfig>();
            missions.name = "Missions";
            missions.parameters.Add(new MissionParameter { name = "amount", type = MissionParamType.Float, onlySetLarger = true, description = token });
            for (int index = 1; index <= 2; index++)
            {
                var mission = ScriptableObject.CreateInstance<MissionConfigData>();
                mission.name = "Mission" + index;
                mission.id = 500 + index;
                mission.parent = missions;
                mission.description = token;
                mission.missionTypeClassFullName = ReceiverNamespace + "ReceiverMission";
                mission.conditions.Add(new MissionCondition { paramName = "amount", compareType = CompareType.GreaterEqual, target = index });
                mission.rewards.Add(new RewardInfo { id = 7, count = 2 });
                missions.missions.Add(mission);
            }
            SaveRoot(missions, missions.missions.Cast<ScriptableObject>().ToArray());

            var equipment = ScriptableObject.CreateInstance<EquipmentsConfig>();
            equipment.name = "Equipment";
            var itemData = ScriptableObject.CreateInstance<EquipmentConfigData>();
            itemData.name = "ScriptEquipment";
            itemData.id = 401;
            itemData.parent = equipment;
            itemData.description = token;
            itemData.equipmentType = EquipmentType.Script;
            itemData.equipmentClassFullName = ReceiverNamespace + "GameplayResourceEquipment";
            equipment.Equipments.Add(itemData);
            SaveRoot(equipment, itemData);

        }

        private AddressableAssetSettings Settings()
        {
            var settings = AddressableAssetSettingsDefaultObject.GetSettings(false);
            Assert.That(settings, Is.Not.Null);
            Assert.That(AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(settings)), Is.EqualTo(_settingsGuid), "Do not switch or replace the prepared official settings.");
            return settings;
        }

        private void RegisterEntries(string projectId)
        {
            var settings = Settings();
            var existing = new List<AddressableAssetEntry>();
            settings.GetAllAssets(existing, false);
            _entries = new[] { ("Skills", "skills"), ("Buffs", "buffs"), ("Missions", "missions"), ("Equipment", "equipment") }
                .ToDictionary(root => AssetDatabase.AssetPathToGUID(_folder + "/Content/" + root.Item1 + ".asset"), root => projectId + "/" + root.Item2, StringComparer.Ordinal);
            Assert.That(_entries.Keys, Is.All.Not.Empty);
            foreach (var entry in _entries)
                Assert.That(existing.Any(value => value.guid == entry.Key || value.address == entry.Value || value.labels.Contains(entry.Value)), Is.False, "Never move or overwrite another owner's entry.");
            Assert.That(settings.groups.Any(group => group && group.Name == projectId), Is.False);
            _groupPath = settings.GroupFolder.Replace('\\', '/') + "/" + projectId + ".asset";
            var schemas = settings.GroupSchemaFolder.Replace('\\', '/');
            foreach (string path in new[] { _groupPath, schemas + "/" + projectId + "_BundledAssetGroupSchema.asset", schemas + "/" + projectId + "_ContentUpdateGroupSchema.asset" })
            {
                RequireLocalAssetPath(path);
                Assert.That(File.Exists(path) || File.Exists(path + ".meta") || Directory.Exists(path), Is.False, "Never overwrite an occupied official group/schema output.");
            }
            var group = settings.CreateGroup(projectId, false, false, false, null, typeof(BundledAssetGroupSchema), typeof(ContentUpdateGroupSchema));
            Assert.That(AssetDatabase.GetAssetPath(group), Is.EqualTo(_groupPath));
            _groupGuid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(group));
            var bundled = group.GetSchema<BundledAssetGroupSchema>();
            bundled.BuildPath.SetVariableByName(settings, AddressableAssetSettings.kLocalBuildPath);
            bundled.LoadPath.SetVariableByName(settings, AddressableAssetSettings.kLocalLoadPath);
            bundled.BundleMode = BundledAssetGroupSchema.BundlePackingMode.PackTogether;
            bundled.IncludeInBuild = bundled.IncludeAddressInCatalog = bundled.IncludeGUIDInCatalog = true;
            group.GetSchema<ContentUpdateGroupSchema>().StaticContent = false;
            foreach (var entry in _entries)
                settings.CreateOrMoveEntry(entry.Key, group).SetAddress(entry.Value);
            foreach (var schema in group.Schemas)
            {
                EditorUtility.SetDirty(schema);
                AssetDatabase.SaveAssetIfDirty(schema);
            }
            _schemaGuids = group.Schemas.Select(schema => AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(schema))).ToArray();
            AssetDatabase.SaveAssetIfDirty(group);
            AssetDatabase.SaveAssetIfDirty(settings);
        }

        private static void RequireLocalAssetPath(string path)
        {
            Assert.That(path.StartsWith("Assets/", StringComparison.Ordinal), Is.True, "Only local host Assets may be authored or removed.");
            for (string cursor = Path.GetFullPath(path); cursor != null; cursor = Path.GetDirectoryName(cursor))
                if (File.Exists(cursor) || Directory.Exists(cursor))
                    Assert.That((File.GetAttributes(cursor) & FileAttributes.ReparsePoint) == 0, Is.True, "Do not author or delete through linked assets.");
        }

        private void SaveRoot(ScriptableObject root, params ScriptableObject[] children)
        {
            string path = _folder + "/Content/" + root.name + ".asset";
            Assert.That(File.Exists(path) || Directory.Exists(path) || File.Exists(path + ".meta"), Is.False);
            AssetDatabase.CreateAsset(root, path);
            foreach (var child in children)
            {
                AssetDatabase.AddObjectToAsset(child, root);
                EditorUtility.SetDirty(child);
            }
            EditorUtility.SetDirty(root);
            AssetDatabase.SaveAssetIfDirty(root);
        }

        [TearDown]
        public void CleanupOnlyThisSuccessfulGeneratedRoot()
        {
            if (_folder != null && TestContext.CurrentContext.Result.Outcome.Status == NUnit.Framework.Interfaces.TestStatus.Passed)
            {
                if (_groupGuid != null)
                {
                    Assert.That(AssetDatabase.AssetPathToGUID(_groupPath), Is.EqualTo(_groupGuid));
                    var group = AssetDatabase.LoadAssetAtPath<AddressableAssetGroup>(_groupPath);
                    Assert.That(group.entries.Count, Is.EqualTo(_entries.Count));
                    Assert.That(group.entries.All(entry => _entries.TryGetValue(entry.guid, out var key) && key == entry.address), Is.True);
                    CollectionAssert.AreEquivalent(_schemaGuids, group.Schemas.Select(schema => AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(schema))));
                    foreach (string path in new[] { _groupPath }.Concat(group.Schemas.Select(AssetDatabase.GetAssetPath)))
                        RequireLocalAssetPath(path);
                    var settings = Settings();
                    settings.RemoveGroup(group);
                    AssetDatabase.SaveAssetIfDirty(settings);
                }
                Assert.That(AssetDatabase.AssetPathToGUID(_folder), Is.EqualTo(_folderGuid));
                Assert.That((File.GetAttributes(_folder) & FileAttributes.ReparsePoint) == 0, Is.True);
                Assert.That(AssetDatabase.DeleteAsset(_folder), Is.True);
            }
            _folder = null;
            _folderGuid = null;
            _groupPath = _groupGuid = _settingsGuid = null;
            _schemaGuids = null;
            _entries = null;
        }
    }
}
