using System;
using System.Collections.Generic;
using NUnit.Framework;
using YLFramework.FSM;
using YLFramework.StateMachine;

namespace YLFramework.Tests.Extensions.Behavior
{
    [Category("YL.Module.Behavior")]
    public class DualFsmLegacyTests
    {
        [Test]
        public void ExistingLightweightLifecycleAndPreviousStateAreUnchanged()
        {
            var events = new List<string>();
            var fsm = Fsm<string>.Allocate().Init(null);
            try
            {
                fsm.State("A").OnEnter(() => events.Add("A.enter")).OnExit(() => events.Add("A.exit"));
                fsm.State("B").OnEnter(() => events.Add("B.enter")).OnExit(() => events.Add("B.exit"));
                fsm.OnStateChanged = (from, to) => events.Add(from + ">" + to);
                Assert.That(fsm.SetUp("A"), Is.True);
                Assert.That(fsm.ChangeState("B"), Is.True);
                Assert.That(fsm.PreviousStateId, Is.EqualTo("A"));
                Assert.That(fsm.Pause(), Is.True);
                Assert.That(fsm.MachineState, Is.EqualTo(MachineState.Stop));
                Assert.That(fsm.Continue(), Is.True);
                CollectionAssert.AreEqual(new[] { "A.enter", "A.exit", "A>B", "B.enter", "B.exit", "B.enter" }, events);
            }
            finally
            {
                fsm.RecycleToCache();
            }
        }

        [Test]
        public void ExistingLightweightCallbackReentryAndRecycleGuardsAreUnchanged()
        {
            var fsm = Fsm<string>.Allocate().Init(null);
            try
            {
                fsm.State("A").OnExit(() =>
                {
                    Assert.That(fsm.ChangeState("A"), Is.False);
                    Assert.That(fsm.Pause(), Is.False);
                    Assert.Throws<InvalidOperationException>(() => fsm.RecycleToCache());
                });
                fsm.State("B");
                fsm.SetUp("A");
                Assert.That(fsm.ChangeState("B"), Is.True);
            }
            finally
            {
                fsm.RecycleToCache();
            }
        }

        [Test]
        public void ExistingLightweightConditionExceptionDoesNotCommitTransition()
        {
            var fsm = Fsm<int>.Allocate().Init(null);
            try
            {
                fsm.State(1);
                fsm.State(2).OnCondition(() => throw new InvalidOperationException("Expected condition failure."));
                fsm.SetUp(1);
                Assert.Throws<InvalidOperationException>(() => fsm.ChangeState(2));
                Assert.That(fsm.CurrentStateId, Is.EqualTo(1));
                fsm.State(2).OnCondition(() => true);
                Assert.That(fsm.ChangeState(2), Is.True);
            }
            finally
            {
                fsm.RecycleToCache();
            }
        }

        [Test]
        public void ExistingFixedLateAndGuiDriveCallbacksRemainIndependentOfNativeTime()
        {
            var fsm = Fsm<int>.Allocate().Init(null);
            int fixedTicks = 0, lateTicks = 0, guiTicks = 0;
            try
            {
                fsm.State(1);
                fsm.State(2).OnFixedUpdate(() => fixedTicks++).OnLateUpdate(() => lateTicks++).OnGUI(() => guiTicks++);
                fsm.SetUp(1);
                fsm.ChangeState(2);
                fsm.FixedUpdate();
                fsm.LateUpdate();
                fsm.OnGUI();
                Assert.That(fsm.CurrentStateId, Is.EqualTo(2));
                Assert.That(fsm.SecondsOfCurrentState, Is.Zero);
                Assert.That(fsm.FrameCountOfCurrentState, Is.EqualTo(1));
                Assert.That(fixedTicks + lateTicks + guiTicks, Is.EqualTo(3));
            }
            finally
            {
                fsm.RecycleToCache();
            }
        }

        [Test]
        public void AssetDataAndLightweightMachineRemainIndependentlyUsable()
        {
            var fsm = Fsm<int>.Allocate().Init(null);
            var parameter = new FsmParameterData { name = "Go", parameterType = ParameterType.Bool };
            try
            {
                fsm.State(1);
                fsm.State(2);
                fsm.SetUp(1);
                parameter.Value = 1;
                Assert.That(fsm.CurrentStateId, Is.EqualTo(1));
                fsm.ChangeState(2);
                Assert.That(parameter.Value, Is.EqualTo(1));
                parameter.Value = 0;
                Assert.That(fsm.CurrentStateId, Is.EqualTo(2));
            }
            finally
            {
                fsm.RecycleToCache();
            }
        }
    }
}
