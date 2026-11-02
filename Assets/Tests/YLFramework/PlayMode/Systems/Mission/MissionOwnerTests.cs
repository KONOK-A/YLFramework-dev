using NUnit.Framework;
using UnityEngine;
using YLFramework.Mission;

namespace YLFramework.Tests.Extensions.Domain
{
    public class MissionOwnerTests
    {
        [SetUp] public void Isolated() => Assert.That(MissionManager.ConfigCount, Is.Zero, "只在无既有领域配置的验收场景运行。");
        [Test]
        public void RuntimeConditionsRewardsResetAndReentrantRemovalDoNotModifyAssets()
        {
            var root = ScriptableObject.CreateInstance<MissionsConfig>();
            var data = ScriptableObject.CreateInstance<MissionConfigData>();
            var parameter = new MissionParameter { name = "amount", type = MissionParamType.Float };
            var condition = new MissionCondition { paramName = "amount", target = 3, compareType = CompareType.Equals };
            try
            {
                root.name = "Domain.Mission.Config";
                root.parameters.Add(parameter);
                data.id = 100;
                data.missionTypeClassFullName = typeof(MissionBase).FullName;
                data.conditions.Add(condition);
                data.rewards.Add(new RewardInfo { id = 1, count = 5 });
                root.missions.Add(data);
                MissionManager.AddConfig(root);
                var mission = MissionManager.GetMission(root.name, 100);
                Assert.That(mission.MissionConfig, Is.Not.SameAs(data));
                Assert.That(mission.MissionConfig.conditions[0], Is.Not.SameAs(condition));
                mission.MissionConfig.rewards[0].count = 99;
                Assert.That(data.rewards[0].count, Is.EqualTo(5));
                mission.State = MissionState.InProgress;
                MissionManager.SetFloat(root.name, "amount", 2);
                Assert.That(mission.Progress, Is.EqualTo(0), "Equals条件只有0或1进度。");
                Assert.That(parameter.Value, Is.Zero);
                MissionManager.Reset(root.name);
                Assert.That(mission.State, Is.EqualTo(MissionState.NotStarted));
                Assert.That(MissionManager.GetFloat(root.name, "amount"), Is.Zero);
                var runtimeParameter = MissionManager.GetAllParameters(root.name)[0];
                var callbacks = 0;
                mission.State = MissionState.InProgress;
                mission.StatusChanged = current =>
                {
                    callbacks++;
                    MissionManager.RemoveConfig(root.name);
                };
                MissionManager.SetFloat(root.name, "amount", 3);
                Assert.That(callbacks, Is.EqualTo(1));
                Assert.That(MissionManager.GetConfig(root.name), Is.Null);
                Assert.That(mission.MissionConfig, Is.Null);
                runtimeParameter.Value = 10;
                Assert.That(callbacks, Is.EqualTo(1));
                Assert.That(parameter.Value, Is.Zero);
                Assert.That(condition.Param, Is.Null);
            }
            finally
            {
                MissionManager.Shutdown();
                Object.DestroyImmediate(root);
                Object.DestroyImmediate(data);
            }
        }

        [Test]
        public void IntFloatBoolAndOnlyInProgressConstraintsUseRuntimeCopies()
        {
            var root = ScriptableObject.CreateInstance<MissionsConfig>();
            var entries = new System.Collections.Generic.List<MissionConfigData>();
            try
            {
                root.name = "Domain.Typed.Parameters";
                root.parameters.Add(new MissionParameter { name = "integer", type = MissionParamType.Int, onlySetInProgress = true, onlySetLarger = true });
                root.parameters.Add(new MissionParameter { name = "float", type = MissionParamType.Float });
                root.parameters.Add(new MissionParameter { name = "boolean", type = MissionParamType.Bool });
                for (var i = 0; i < root.parameters.Count; i++)
                {
                    var data = ScriptableObject.CreateInstance<MissionConfigData>();
                    entries.Add(data);
                    data.id = 100 + i;
                    data.missionTypeClassFullName = typeof(MissionBase).FullName;
                    data.resetParamOnCompleted = false;
                    data.conditions.Add(new MissionCondition { paramName = root.parameters[i].name, target = i == 0 ? 3 : i == 1 ? 2 : 1, compareType = CompareType.Equals });
                    root.missions.Add(data);
                }
                MissionManager.AddConfig(root);
                MissionManager.SetInt(root.name, "integer", 2);
                Assert.That(MissionManager.GetInt(root.name, "integer"), Is.Zero);
                foreach (var mission in MissionManager.GetAllMissions(root.name))
                {
                    mission.State = MissionState.InProgress;
                }

                MissionManager.SetInt(root.name, "integer", 2);
                MissionManager.SetInt(root.name, "integer", 1);
                Assert.That(MissionManager.GetInt(root.name, "integer"), Is.EqualTo(2));
                Assert.That(() => MissionManager.SetFloat(root.name, "integer", 3), Throws.InvalidOperationException);
                MissionManager.AddInt(root.name, "integer", 1);
                MissionManager.SetFloat(root.name, "float", 2);
                MissionManager.SetBool(root.name, "boolean", true);
                foreach (var mission in MissionManager.GetAllMissions(root.name))
                {
                    Assert.That(mission.State, Is.EqualTo(MissionState.Completed));
                }

                foreach (var parameter in root.parameters)
                {
                    Assert.That(parameter.Value, Is.Zero);
                }

                MissionManager.Reset(root.name);
                foreach (var mission in MissionManager.GetAllMissions(root.name))
                {
                    Assert.That(mission.State, Is.EqualTo(MissionState.NotStarted));
                }

                Assert.That(MissionManager.GetBool(root.name, "boolean"), Is.False);
            }
            finally
            {
                MissionManager.Shutdown();
                foreach (var data in entries)
                {
                    Object.DestroyImmediate(data);
                }
                Object.DestroyImmediate(root);
            }
        }
    }
}
