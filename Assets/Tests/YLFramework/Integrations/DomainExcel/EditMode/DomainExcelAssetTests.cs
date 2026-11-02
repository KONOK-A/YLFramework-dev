#if UNITY_EDITOR && YL_DOMAIN_EXCEL_PACKAGE
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using YLFramework.Buff;
using YLFramework.Mission;
using YLFramework.Skill;
using YLFramework.Integrations.DomainExcel.Editor;
using YLFramework.Integrations.Excel.Editor;

namespace YLFramework.Domain.Excel.Tests
{
    public class DomainExcelAssetTests
    {
        public sealed class SkillForExcel : SkillBase
        {
        }
        private string _folder, _path;
        [SetUp]
        public void Setup()
        {
            _folder = "Assets/DomainExcelFixture-" + Guid.NewGuid().ToString("N");
            // 同一组回归可由不同验收 owner 运行，只登记本用例新建的独占目录。
            var equipmentOwner = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("YLFramework.Tests.EquipmentAcceptance.EquipmentSettings")).FirstOrDefault(t => t != null);
            equipmentOwner?.GetMethod("OwnPath").Invoke(null, new object[] { _folder });
            if (SessionState.GetBool("YLFramework.Domain.MainAcceptance.Protection", false))
                Type.GetType("YLFramework.Tests.DomainMain.DomainMainAcceptance, Assembly-CSharp-Editor", true)
                    .GetMethod("OwnPath").Invoke(null, new object[] { _folder });
            AssetDatabase.CreateFolder("Assets", Path.GetFileName(_folder));
            _path = Path.Combine(Path.GetTempPath(), "domain-excel-native-" + Guid.NewGuid().ToString("N") + ".xlsx");
        }
        [TearDown]
        public void Cleanup()
        {
            if (AssetDatabase.IsValidFolder(_folder))
                AssetDatabase.DeleteAsset(_folder);
            if (File.Exists(global::YLFramework.Testing.PackageTestPaths.Physical(_path)))
                File.Delete(global::YLFramework.Testing.PackageTestPaths.Physical(_path));
        }

        [Test]
        public void NewSkillUsesImportedClassAfterInitializeAndCancelDoesNotCreateAssets()
        {
            var root = ScriptableObject.CreateInstance<SkillsConfig>();
            AssetDatabase.CreateAsset(root, _folder + "/skills.asset");
            var data = ScriptableObject.CreateInstance<SkillConfigData>();
            try
            {
                data.id = 100;
                data.skillName = "xlsx skill";
                data.skillClassFullName = typeof(SkillForExcel).FullName;
                ExcelAuthoring.ScriptableObjectToExcel(new[] { data }, _path, "skills");
                using (var cancel = DomainExcelCommands.PreviewImport(root, _path))
                {
                    Assert.That(cancel.Errors, Is.Empty);
                    Assert.That(cancel.Commit(false), Is.EqualTo(ExcelCommitResult.Cancelled));
                    Assert.That(root.skills, Is.Empty);
                }
                using var apply = DomainExcelCommands.PreviewImport(root, _path);
                Assert.That(apply.Errors, Is.Empty);
                Assert.That(apply.Commit(true), Is.EqualTo(ExcelCommitResult.Applied));
                Assert.That(root.skills[0].skillClassFullName, Is.EqualTo(data.skillClassFullName));
                Assert.That(root.skills[0].parent, Is.SameAs(root));
                Assert.That(AssetDatabase.IsSubAsset(root.skills[0]), Is.True);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(data);
            }
        }

        [Test]
        public void BuffEffectIdsUseRealAssetsAndUnknownIdsCannotCommit()
        {
            var root = ScriptableObject.CreateInstance<BuffsConfig>();
            AssetDatabase.CreateAsset(root, _folder + "/buffs.asset");
            var occupied = new HashSet<int>();
            foreach (var guid in AssetDatabase.FindAssets("t:EffectData"))
            {
                var asset = AssetDatabase.LoadAssetAtPath<EffectData>(AssetDatabase.GUIDToAssetPath(guid));
                if (asset)
                    occupied.Add(asset.id);
            }
            var id = int.MinValue;
            while (occupied.Contains(id))
                id++;
            var effect = ScriptableObject.CreateInstance<EffectData>();
            effect.id = id;
            AssetDatabase.CreateAsset(effect, _folder + "/effect.asset");
            var data = ScriptableObject.CreateInstance<BuffConfigData>();
            var unknown = ScriptableObject.CreateInstance<EffectData>();
            try
            {
                data.id = 100;
                data.buffClassFullName = typeof(BuffBase).FullName;
                data.effects.Add(effect);
                ExcelAuthoring.ScriptableObjectToExcel(new[] { data }, _path, "buffs");
                using (var apply = DomainExcelCommands.PreviewImport(root, _path))
                {
                    Assert.That(apply.Errors, Is.Empty);
                    Assert.That(apply.Commit(true), Is.EqualTo(ExcelCommitResult.Applied));
                }
                Assert.That(root.buffs[0].effects[0], Is.SameAs(effect));
                do
                {
                    id++;
                } while (occupied.Contains(id));
                unknown.id = id;
                data.effects.Clear();
                data.effects.Add(unknown);
                ExcelAuthoring.ScriptableObjectToExcel(new[] { data }, _path, "buffs");
                using var invalid = DomainExcelCommands.PreviewImport(root, _path);
                Assert.That(invalid.Errors, Is.Not.Empty);
                Assert.That(invalid.Commit(true), Is.EqualTo(ExcelCommitResult.Failed));
                Assert.That(root.buffs[0].effects[0], Is.SameAs(effect));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(data);
                UnityEngine.Object.DestroyImmediate(unknown);
            }
        }

        [Test]
        public void MissionImportReadsActualConditionArrayAndRejectsUnknownParameterWithoutChangingRoot()
        {
            var root = ScriptableObject.CreateInstance<MissionsConfig>();
            root.parameters.Add(new MissionParameter { name = "amount", type = MissionParamType.Int });
            AssetDatabase.CreateAsset(root, _folder + "/missions.asset");
            var data = ScriptableObject.CreateInstance<MissionConfigData>();
            try
            {
                data.id = 100;
                data.missionName = "xlsx mission";
                data.missionTypeClassFullName = typeof(MissionBase).FullName;
                data.conditions.Add(new MissionCondition { paramName = "amount", compareType = CompareType.GreaterEqual, target = 2 });
                var jsonProperty = typeof(MissionCondition).GetField(nameof(MissionCondition.paramName)).GetCustomAttributes(false)
                    .Single(attribute => attribute.GetType().FullName == "Newtonsoft.Json.JsonPropertyAttribute");
                Assert.That(jsonProperty.GetType().GetProperty("PropertyName").GetValue(jsonProperty), Is.EqualTo("param_name"),
                    "Nested snapshots retain the shipped Mission JSON key.");
                ExcelAuthoring.ScriptableObjectToExcel(new[] { data }, _path, "missions");
                using (var cancel = DomainExcelCommands.PreviewImport(root, _path))
                {
                    Assert.That(cancel.Errors, Is.Empty);
                    Assert.That(cancel.Commit(false), Is.EqualTo(ExcelCommitResult.Cancelled));
                    Assert.That(root.missions, Is.Empty);
                }
                using (var apply = DomainExcelCommands.PreviewImport(root, _path))
                {
                    Assert.That(apply.Errors, Is.Empty);
                    Assert.That(apply.Commit(true), Is.EqualTo(ExcelCommitResult.Applied));
                }
                Assert.That(root.missions[0].conditions[0].paramName, Is.EqualTo("amount"));
                Assert.That(root.missions[0].conditions[0].target, Is.EqualTo(2));
                Assert.That(root.missions[0].parent, Is.SameAs(root));
                data.conditions[0].paramName = "missing";
                ExcelAuthoring.ScriptableObjectToExcel(new[] { data }, _path, "missions");
                Assert.That(() => DomainExcelCommands.PreviewImport(root, _path), Throws.InvalidOperationException.With.Message.Contains("missing"));
                Assert.That(root.missions[0].conditions[0].paramName, Is.EqualTo("amount"));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(data);
            }
        }
    }
}
#endif
