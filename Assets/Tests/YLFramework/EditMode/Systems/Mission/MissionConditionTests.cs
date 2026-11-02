using System;
using System.Reflection;
using NUnit.Framework;
using YLFramework.Events;
using YLFramework.Mission;

namespace YLFramework.Tests.Extensions.Domain
{
    [Category("YL.Module.Domain")]
    public class MissionConditionTests
    {
        private Type Find(string name)
        {
            var type = typeof(EventChannel).Assembly.GetType("YLFramework.Mission." + name);
            Assert.That(type, Is.Not.Null, "任务领域类型缺失: " + name);
            return type;
        }

        [TestCase("Equals", 3, 3, true)]
        [TestCase("NotEquals", 2, 3, true)]
        [TestCase("NotEquals", 3, 3, false)]
        [TestCase("Greater", 4, 3, true)]
        [TestCase("LessEqual", 3, 3, true)]
        public void FloatComparisonUsesEveryDeclaredOperator(string comparison, float value, float target, bool expected)
        {
            var parameterType = Find("MissionParameter");
            var parameter = Activator.CreateInstance(parameterType);
            parameterType.GetProperty("Value").SetValue(parameter, value);
            var type = Find("MissionCondition");
            var condition = Activator.CreateInstance(type);
            type.GetField("target").SetValue(condition, target);
            type.GetField("compareType").SetValue(condition, Enum.Parse(Find("CompareType"), comparison));
            type.GetMethod("UpdateState", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(condition, new[] { parameter });
            Assert.That(type.GetProperty("IsMet").GetValue(condition), Is.EqualTo(expected));
        }

        [Test]
        public void ResetDoesNotNotifyOldParameterSubscribers()
        {
            var type = Find("MissionParameter");
            var parameter = Activator.CreateInstance(type);
            type.GetProperty("Value").SetValue(parameter, 2f);
            var calls = 0;
            type.GetField("ValueChanged").SetValue(parameter, (Action<object>)(_ => calls++));
            type.GetMethod("Reset", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(parameter, null);
            Assert.That(calls, Is.Zero);
            Assert.That(type.GetProperty("Value").GetValue(parameter), Is.EqualTo(0f));
        }

        [Test]
        public void ProgressSubscriberSeesNewMeetStateNotPreviousState()
        {
            var parameter = new MissionParameter { type = MissionParamType.Float };
            parameter.Value = 2;
            var condition = new MissionCondition { target = 2, compareType = CompareType.GreaterEqual };
            var seen = false;
            condition.ProgressValueChanged = value => seen = value.IsMet;
            typeof(MissionCondition).GetMethod("UpdateState", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(condition, new object[] { parameter });
            Assert.That(seen, Is.True);
        }

        [Test]
        public void RebindAndDisposeReturnExactParameterSubscription()
        {
            var parameter = new MissionParameter { name = "amount", type = MissionParamType.Float };
            var condition = new MissionCondition { paramName = "amount", target = 2, compareType = CompareType.Equals };
            var bind = typeof(MissionCondition).GetMethod("Bind", BindingFlags.Instance | BindingFlags.NonPublic);
            var dispose = typeof(MissionCondition).GetMethod("Dispose", BindingFlags.Instance | BindingFlags.NonPublic);
            bind.Invoke(condition, new object[] { parameter });
            bind.Invoke(condition, new object[] { parameter });
            var meet = 0;
            condition.Met = () => meet++;
            parameter.Value = 2;
            Assert.That(meet, Is.EqualTo(1));
            dispose.Invoke(condition, null);
            parameter.Value = 3;
            parameter.Value = 2;
            Assert.That(meet, Is.EqualTo(1));
            Assert.That(condition.Param, Is.Null);
        }

        [Test]
        public void ReentrantDisposeStopsCompletionForOldCondition()
        {
            var parameter = new MissionParameter { name = "amount" };
            var condition = new MissionCondition { paramName = "amount", target = 2, compareType = CompareType.Equals };
            typeof(MissionCondition).GetMethod("Bind", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(condition, new object[] { parameter });
            var calls = 0;
            condition.Met = () => calls++;
            condition.ProgressValueChanged = _ => typeof(MissionCondition).GetMethod("Dispose", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(condition, null);
            parameter.Value = 2;
            Assert.That(calls, Is.Zero);
            Assert.That(condition.Param, Is.Null);
        }

        [Test]
        public void ParameterCloneDoesNotShareValuesOrCustomData()
        {
            var source = new MissionParameter { name = "amount" };
            source.Value = 9;
            var clone = source.CloneConfiguration();
            clone.Value = 2;
            Assert.That(source.Value, Is.EqualTo(9));
            var custom = typeof(MissionParameter).GetField("customData", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(custom.GetValue(clone), Is.Not.SameAs(custom.GetValue(source)));
        }

        [Test]
        public void KnownHashCollisionIsRejectedRatherThanSharingAuthority()
        {
            Assert.That(MissionManager.StringToHash("costarring"), Is.EqualTo(MissionManager.StringToHash("liquid")));
            var remember = typeof(MissionManager).GetMethod("RememberKey", BindingFlags.Static | BindingFlags.NonPublic);
            try
            {
                remember.Invoke(null, new object[] { "costarring" });
                var error = Assert.Throws<TargetInvocationException>(() => remember.Invoke(null, new object[] { "liquid" }));
                Assert.That(error.InnerException, Is.InstanceOf<InvalidOperationException>());
                Assert.That(error.InnerException.Message, Does.Contain("costarring").And.Contain("liquid"));
            }
            finally
            {
                MissionManager.Shutdown();
            }
        }
    }
}
