using System;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
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

        [Test]
        public void NewFourDomainPublicationRejectsOldAssemblyTuplesAndRunsInTwoOwnedMonoPlayers()
        {
            Assert.That(Application.isBatchMode && !EditorApplication.isPlayingOrWillChangePlaymode, Is.True);
            Assert.That(EditorApplication.isCompiling || EditorApplication.isUpdating, Is.False);
            string output = global::YLFramework.Testing.Editor.YLTestEntry.RequireOwnedRunOutput();
            Assert.That(Directory.Exists(output), Is.True, "Never create or adopt an unowned result directory.");
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
            var project = CreateContent(token);
            var release = ResourcePublisher.Publish(ResourceBundleBuilder.Build(project, BuildTarget.StandaloneWindows64), "gameplay-resource-v1");
            project = AssetDatabase.LoadAssetAtPath<ResourceProject>(folder + "/Project.asset");
            Assert.That((bool)project, Is.True, "Bundle building can reload assets; reacquire the owned project by its saved path.");
            Assert.That(release.IsRepeat, Is.False);
            Assert.That(release.Catalog.Assets, Has.Count.EqualTo(11));
            Assert.That(release.Catalog.Assets.Count(asset => asset.Kind == "Asset"), Is.EqualTo(4));
            foreach (var expected in new[] { ("YLFramework.Skill", 2), ("YLFramework.Buff", 4), ("YLFramework.Mission", 3), ("YLFramework.Equipment", 2) })
            {
                Assert.That(release.Catalog.Assets.Count(asset => asset.AssetAssembly == expected.Item1), Is.EqualTo(expected.Item2));
            }
            foreach (var asset in release.Catalog.Assets)
            {
                Assert.That(_assetTypes.Any(type => type.FullName == asset.AssetType && type.Assembly.GetName().Name == asset.AssetAssembly), Is.True);
            }
            var snapshot = ResourceLocalSnapshot.Open(Path.GetFullPath(release.DirectoryPath), release.Catalog.ProjectId,
                release.Catalog.CatalogDigest, release.Catalog.Platform, release.Catalog.AppContentCompatibilityId);
            Assert.That(snapshot.CreateCatalog(_assetTypes).Entries, Has.Count.EqualTo(11));
            var positive = new ResourceEditorSimulation(new[] { project }, new[] { release.Catalog });
            foreach (var root in new[] { ("skills", typeof(SkillsConfig)), ("buffs", typeof(BuffsConfig)), ("missions", typeof(MissionsConfig)), ("equipment", typeof(EquipmentsConfig)) })
            {
                Assert.That(positive.Load(new ResourceAddress(project.projectId, root.Item1), root.Item2).GetType(), Is.EqualTo(root.Item2));
            }

            // Change only a tuple in a copy of this new publication. Frozen catalogs and Bundles remain untouched.
            byte[] publishedBytes = ResourceCatalogCodec.Write(release.Catalog);
            foreach (string assembly in new[] { "YLFramework.Skill", "YLFramework.Buff", "YLFramework.Mission", "YLFramework.Equipment" })
            {
                var payload = JObject.Parse(Encoding.UTF8.GetString(publishedBytes));
                payload.Remove("BuildId");
                foreach (var asset in ((JArray)payload["Assets"]).OfType<JObject>().Where(asset => (string)asset["AssetAssembly"] == assembly))
                {
                    asset["AssetAssembly"] = "YLFramework.Runtime";
                }
                var bytes = ResourceCatalogCodec.Seal(Encoding.UTF8.GetBytes(payload.ToString(Formatting.None)));
                var oldTuple = ResourceCatalogCodec.Read(bytes, project.projectId, release.Catalog.Platform,
                    release.Catalog.UnityCompatibility, release.Catalog.AppContentCompatibilityId);
                Assert.That(oldTuple.CatalogDigest, Is.Not.EqualTo(release.Catalog.CatalogDigest));
                var rejected = new ResourceEditorSimulation(new[] { project }, new[] { oldTuple });
                var error = Assert.Throws<InvalidOperationException>(() => rejected.Resolve(new ResourceAddress(project.projectId, "skills")));
                Assert.That(error.Message, Does.StartWith("Published Catalog descriptor is stale:"), "Reject the assembly tuple, not a schema or compatibility error.");
            }
            CollectionAssert.AreEqual(publishedBytes, File.ReadAllBytes(Path.Combine(release.DirectoryPath, "resource-catalog.json")));
            var definitions = Directory.GetFiles(folder + "/Content", "*.asset").ToDictionary(path => path, File.ReadAllBytes);

            // Keep only addresses and value tokens across BuildAndRun's scene switches, never native asset handles.
            StandaloneFixturePlayer.BuildAndRun(output, folder, "YLFramework.Skill", release, scene =>
            {
                var type = Type.GetType(ReceiverNamespace + "GameplayResourcePlayerProbe, " + ProbeAssembly, true);
                var host = new GameObject("GameplayResourceStandaloneProbe");
                host.SetActive(false);
                SceneManager.MoveGameObjectToScene(host, scene);
                using (var serialized = new SerializedObject(host.AddComponent(type)))
                {
                    serialized.FindProperty("projectId").stringValue = release.Catalog.ProjectId;
                    serialized.FindProperty("catalogDigest").stringValue = release.Catalog.CatalogDigest;
                    serialized.FindProperty("compatibility").stringValue = release.Catalog.AppContentCompatibilityId;
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
                Assert.That((int)report["catalogAssetCount"], Is.EqualTo(11));
                Assert.That((int)report["bundleRecordCount"], Is.Zero);
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

        private ResourceProject CreateContent(string token)
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

            var project = ScriptableObject.CreateInstance<ResourceProject>();
            project.projectId = "gp-" + token.Substring(0, 8);
            project.sourceDirectories.Add(new ResourceSourceDirectory { guid = AssetDatabase.AssetPathToGUID(_folder + "/Content") });
            var group = new ResourceGroup { id = "Gameplay", packing = ResourcePacking.Together };
            foreach (var root in new[] { ("Skills", "skills"), ("Buffs", "buffs"), ("Missions", "missions"), ("Equipment", "equipment") })
            {
                group.selections.Add(new ResourceSelection { guid = AssetDatabase.AssetPathToGUID(_folder + "/Content/" + root.Item1 + ".asset"), address = root.Item2 });
            }
            project.groups.Add(group);
            string path = _folder + "/Project.asset";
            Assert.That(File.Exists(path) || File.Exists(path + ".meta"), Is.False);
            AssetDatabase.CreateAsset(project, path);
            EditorUtility.SetDirty(project);
            AssetDatabase.SaveAssetIfDirty(project);
            return project;
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
                Assert.That(AssetDatabase.AssetPathToGUID(_folder), Is.EqualTo(_folderGuid));
                Assert.That((File.GetAttributes(_folder) & FileAttributes.ReparsePoint) == 0, Is.True);
                Assert.That(AssetDatabase.DeleteAsset(_folder), Is.True);
            }
            _folder = null;
            _folderGuid = null;
        }
    }
}
