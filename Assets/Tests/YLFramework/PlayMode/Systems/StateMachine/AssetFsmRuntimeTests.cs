using System;
using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Scripting;
using UnityEngine.TestTools;
using YLFramework.FSM;
using YLFramework.StateMachine;

namespace YLFramework.Tests.Extensions.Behavior
{
    public class AssetFsmRuntimeTests
    {
        private readonly List<GameObject> _objects = new List<GameObject>();
        private readonly List<RuntimeFsmController> _assets = new List<RuntimeFsmController>();

        public sealed class Trace
        {
            public readonly List<string> Events = new List<string>();
            public Action<ProbeState> Enter;
            public Action<ProbeState> Exit;
            public RuntimeFsmControllerInstance FailedInstance;
        }

        [Preserve]
        public sealed class ProbeState : FsmState
        {
            private Trace Context => UserData as Trace;
            public override void OnCreate() => Context?.Events.Add(CurrentStateInfo.Data.name + ".create");
            public override void OnEnter()
            {
                Context?.Events.Add(CurrentStateInfo.Data.name + ".enter");
                Context?.Enter?.Invoke(this);
            }
            public override void OnExit()
            {
                Context?.Events.Add(CurrentStateInfo.Data.name + ".exit");
                Context?.Exit?.Invoke(this);
            }
        }

        [Preserve]
        public sealed class ReentrantFirstScript : FsmState
        {
            public override void OnCreate() => ((Trace)UserData).Events.Add("first.create");
            public override void OnEnter()
            {
                var trace = (Trace)UserData;
                trace.Events.Add("first.enter");
                if (!trace.Events.Contains("left.once"))
                {
                    trace.Events.Add("left.once");
                    Controller.SetBool("Go", false);
                }
            }
        }

        [Preserve]
        public sealed class ReentrantSecondScript : FsmState
        {
            public override void OnCreate() => ((Trace)UserData).Events.Add("second.create");
            public override void OnEnter() => ((Trace)UserData).Events.Add("second.enter");
        }

        [Preserve]
        public sealed class ThrowOnFirstCreate : FsmState
        {
            public override void OnCreate()
            {
                ((Trace)UserData).FailedInstance = CurrentStateInfo.Instance;
                throw new InvalidOperationException("Expected startup failure.");
            }
        }

        private FsmStateNodeData Node(string name, bool isDefault = false, bool sub = false, params string[] parents)
        {
            var node = new FsmStateNodeData { name = name, isDefaultState = isDefault, isSubStateMachine = sub, parents = new List<string>(parents) };
            node.stateScripts.Add(new FsmStateScriptInfo { className = typeof(ProbeState).FullName });
            return node;
        }

        private RuntimeFsmController Asset(string name)
        {
            var asset = ScriptableObject.CreateInstance<RuntimeFsmController>();
            asset.name = "YL.Behavior." + name;
            _assets.Add(asset);
            asset.states.Add(Node("Idle", true));
            asset.states.Add(Node("Run"));
            asset.parameters.Add(new FsmParameterData { name = "Go", parameterType = ParameterType.Bool });
            asset.parameters.Add(new FsmParameterData { name = "Fire", parameterType = ParameterType.Trigger });
            asset.transitions.Add(Transition("Idle", "Run", "Go", 1));
            asset.transitions.Add(Transition("Run", "Idle", "Go", 0));
            return asset;
        }

        private static FsmTransitionData Transition(string from, string to, string parameter, float value)
        {
            var transition = new FsmTransitionData { fromStateName = from, toStateName = to };
            transition.conditions.Add(new FsmConditionData { parameterName = parameter, compareType = CompareType.Equal, targetValue = value });
            return transition;
        }

        private FsmController Host(RuntimeFsmController asset, string layer, Trace trace = null)
        {
            var obj = new GameObject("YL.Behavior.FSMHost");
            _objects.Add(obj);
            obj.SetActive(false);
            var controller = obj.AddComponent<FsmController>();
            controller.UserDataValue = trace;
            controller.AddRuntimeFsmController(asset, layer);
            obj.SetActive(true);
            return controller;
        }

        [TearDown]
        public void Cleanup()
        {
            foreach (var obj in _objects)
            {
                if (obj)
                {
                    UnityEngine.Object.DestroyImmediate(obj);
                }
            }

            foreach (var asset in _assets)
            {
                if (asset)
                {
                    UnityEngine.Object.DestroyImmediate(asset);
                }
            }

            _objects.Clear();
            _assets.Clear();
        }

        [UnityTest]
        public IEnumerator TwoClonesAreIsolatedAndLayerRemovalDisposesOnlyOwnedClone()
        {
            var source = Asset("Shared");
            var first = Host(source, "A");
            var second = Host(source, "B");
            yield return null;
            var instance = first.GetCurrentStateInfo(0).Instance;
            var clone = instance.RuntimeFsmController;
            Assert.That(clone, Is.Not.SameAs(source));
            Assert.That(clone, Is.Not.SameAs(second.RuntimeFsmController[0]));
            first.SetBool("Go", true);
            Assert.That(first.GetCurrentStateInfo(0)?.Data.name, Is.EqualTo("Run"));
            Assert.That(second.GetCurrentStateInfo(0)?.Data.name, Is.EqualTo("Idle"));
            Assert.That(source.parameters[0].Value, Is.Zero);
            Assert.That(source.parameters[0].ValueChanged, Is.Null);
            first.RemoveRuntimeFsmController("A");
            Assert.That(instance.IsDisposed, Is.True);
            instance.Dispose();
            yield return null;
            Assert.That(!clone, Is.True);
            Assert.That(source && second.RuntimeFsmController[0], Is.True);
        }

        [UnityTest]
        public IEnumerator InterruptedFirstEntryCreatesEveryScriptOnceBeforeItsOwnEnter()
        {
            var source = Asset("PerScriptCreate");
            source.states[1].stateScripts.Clear();
            source.states[1].stateScripts.Add(new FsmStateScriptInfo { className = typeof(ReentrantFirstScript).FullName });
            source.states[1].stateScripts.Add(new FsmStateScriptInfo { className = typeof(ReentrantSecondScript).FullName });
            var trace = new Trace();
            var controller = Host(source, "A", trace);
            yield return null;
            controller.SetBool("Go", true);
            Assert.That(controller.GetCurrentStateInfo(0)?.Data.name, Is.EqualTo("Idle"));
            Assert.That(trace.Events.Contains("second.create"), Is.False);
            controller.SetBool("Go", true);
            Assert.That(controller.GetCurrentStateInfo(0)?.Data.name, Is.EqualTo("Run"));
            Assert.That(trace.Events.FindAll(item => item == "first.create").Count, Is.EqualTo(1));
            Assert.That(trace.Events.FindAll(item => item == "second.create").Count, Is.EqualTo(1));
            Assert.That(trace.Events.IndexOf("second.create"), Is.LessThan(trace.Events.IndexOf("second.enter")));
        }

        [UnityTest]
        public IEnumerator FailedInitialCallbackDisposesItsLayerAndCompletesOtherInitialization()
        {
            var bad = Asset("BadStartup");
            bad.states[0].stateScripts[0].className = typeof(ThrowOnFirstCreate).FullName;
            var trace = new Trace();
            var controller = Host(bad, "Bad", trace);
            controller.AddRuntimeFsmController(Asset("GoodStartup"), "Good");
            controller.SetBool("Go", true);
            var init = typeof(FsmController).GetMethod("Init", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var exception = Assert.Throws<System.Reflection.TargetInvocationException>(() => init.Invoke(controller, null));
            Assert.That(exception.InnerException, Is.TypeOf<InvalidOperationException>());
            Assert.That(controller.IsInitialized, Is.True);
            Assert.That(typeof(FsmController).GetField("_isInitializing", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(controller), Is.EqualTo(false));
            Assert.That(trace.FailedInstance.IsDisposed, Is.True);
            Assert.That(controller.GetCurrentStateInfo("Bad"), Is.Null);
            Assert.That(controller.GetCurrentStateInfo("Good")?.Data.name, Is.EqualTo("Run"));
            yield return null;
            controller.SetBool("Go", false);
            Assert.That(controller.GetCurrentStateInfo("Good")?.Data.name, Is.EqualTo("Idle"));
        }

        [UnityTest]
        public IEnumerator PendingLayerCanBeCancelledDuringEarlierEnterAndBeforeStart()
        {
            var pending = Asset("Pending");
            pending.states.Clear();
            pending.transitions.Clear();
            pending.states.Add(Node("Never", true));
            var trace = new Trace
            {
                Enter = state =>
                {
                    if (state.CurrentStateInfo.Instance.LayerName == "A")
                    {
                        state.Controller.RemoveRuntimeFsmController("B");
                    }
                }
            };
            var first = Host(Asset("Earlier"), "A", trace);
            first.AddRuntimeFsmController(pending, "B");
            var second = Host(Asset("BeforeStart"), "A");
            second.AddRuntimeFsmController(pending, "B");
            second.RemoveRuntimeFsmController("B");
            yield return null;
            Assert.That(first.RuntimeFsmControllerLayers.Count, Is.EqualTo(1));
            Assert.That(second.RuntimeFsmControllerLayers.Count, Is.EqualTo(1));
            Assert.That(trace.Events.Exists(item => item.StartsWith("Never.")), Is.False);
        }

        [UnityTest]
        public IEnumerator DisableCloseClearsActiveAndQueuedTriggersAndCanRestartSameClone()
        {
            var controller = Host(Asset("Trigger"), "A");
            yield return null;
            var instance = controller.GetCurrentStateInfo(0).Instance;
            var clone = instance.RuntimeFsmController;
            controller.SetTrigger("Fire");
            controller.SetTrigger("Fire");
            Assert.That(instance.GetTriggerCount(FsmController.StringToHash("Fire")), Is.EqualTo(1));
            controller.enabled = false;
            Assert.That(instance.HasStarted, Is.False);
            Assert.That(controller.GetTrigger("Fire"), Is.False);
            controller.enabled = true;
            yield return null;
            Assert.That(instance.HasStarted, Is.True);
            Assert.That(instance.RuntimeFsmController, Is.SameAs(clone));
            Assert.That(controller.GetCurrentStateInfo(0)?.Data.name, Is.EqualTo("Idle"));
            Assert.That(controller.GetTrigger("Fire"), Is.False);
        }

        [UnityTest]
        public IEnumerator ConditionGroupsRetainAndWithinOrAcrossGroupsAndConsumeTrigger()
        {
            var source = Asset("ConditionGroups");
            source.transitions.Clear();
            source.parameters.Add(new FsmParameterData { name = "Count", parameterType = ParameterType.Int });
            source.parameters.Add(new FsmParameterData { name = "Speed", parameterType = ParameterType.Float });
            var transition = Transition("Idle", "Run", "Go", 1);
            transition.conditions.Add(new FsmConditionData { parameterName = "Speed", compareType = CompareType.Greater, targetValue = 3 });
            transition.groupConditions.Add(new GroupCondition
            {
                conditions = new List<FsmConditionData>
            {
                new FsmConditionData { parameterName = "Count", compareType = CompareType.Greater, targetValue = 1 },
                new FsmConditionData { parameterName = "Fire", compareType = CompareType.Equal, targetValue = 1 }
            }
            });
            source.transitions.Add(transition);
            var controller = Host(source, "Groups");
            yield return null;
            controller.SetInt("Count", 2);
            Assert.That(controller.GetCurrentStateInfo(0)?.Data.name, Is.EqualTo("Idle"));
            controller.SetTrigger("Fire");
            Assert.That(controller.GetCurrentStateInfo(0)?.Data.name, Is.EqualTo("Run"));
            Assert.That(controller.GetTrigger("Fire"), Is.False);
            Assert.That(controller.GetInt("Count"), Is.EqualTo(2));
            Assert.That(controller.GetFloat("Speed"), Is.Zero);
        }

        [UnityTest]
        public IEnumerator BuiltInEntryDisplaysDefaultAndAnyStateTransitionsRemainAvailable()
        {
            var source = Asset("EntryAny");
            source.transitions.Clear();
            source.states[0].isDefaultState = false;
            source.states[1].isDefaultState = true;
            source.states.Add(new FsmStateNodeData { name = FsmConst.EntryState, isBuiltInState = true, builtInStateName = FsmConst.EntryState });
            source.states.Add(new FsmStateNodeData { name = FsmConst.AnyState, isBuiltInState = true, builtInStateName = FsmConst.AnyState });
            source.transitions.Add(Transition(FsmConst.AnyState, "Idle", "Fire", 1));
            var controller = Host(source, "EntryAny");
            yield return null;
            Assert.That(controller.GetCurrentStateInfo(0)?.Data.name, Is.EqualTo("Run"));
            controller.SetTrigger("Fire");
            Assert.That(controller.GetCurrentStateInfo(0)?.Data.name, Is.EqualTo("Idle"));
            Assert.That(controller.GetTrigger("Fire"), Is.False);
        }

        [UnityTest]
        public IEnumerator NestedDefaultTransitionsAndExitCallbacksRemainAvailable()
        {
            var source = Asset("Hierarchy");
            source.states.Clear();
            source.transitions.Clear();
            source.states.Add(Node("Root", true, true));
            source.states.Add(Node("ChildIdle", true, false, "Root"));
            source.states.Add(Node("ChildRun", false, false, "Root"));
            source.transitions.Add(Transition("ChildIdle", "ChildRun", "Go", 1));
            var trace = new Trace();
            var controller = Host(source, "Hierarchy", trace);
            yield return null;
            Assert.That(controller.GetCurrentStateInfo(0)?.Data.name, Is.EqualTo("Root"));
            Assert.That(controller.GetCurrentStateInfo(0, "Root")?.Data.name, Is.EqualTo("ChildIdle"));
            controller.SetBool("Go", true);
            Assert.That(controller.GetCurrentStateInfo(0, "Root")?.Data.name, Is.EqualTo("ChildRun"));
            trace.Events.Clear();
            controller.enabled = false;
            CollectionAssert.AreEqual(new[] { "Root.exit", "ChildRun.exit" }, trace.Events);
        }

        [UnityTest]
        public IEnumerator EnterCallbackCanAddLayerAndRemoveItselfWithoutInvalidatingIteration()
        {
            var source = Asset("Mutating");
            var added = Asset("Added");
            var trace = new Trace();
            RuntimeFsmControllerInstance removed = null;
            trace.Enter = state =>
            {
                if (state.CurrentStateInfo.Instance.LayerName != "A")
                {
                    return;
                }

                removed = state.CurrentStateInfo.Instance;
                state.Controller.AddRuntimeFsmController(added, "B");
                state.Controller.RemoveRuntimeFsmController("A");
            };
            var controller = Host(source, "A", trace);
            yield return null;
            Assert.That(removed, Is.Not.Null);
            Assert.That(removed.IsDisposed, Is.True);
            Assert.That(controller.RuntimeFsmControllerLayers.Count, Is.EqualTo(1));
            Assert.That(controller.RuntimeFsmControllerLayers[0].name, Is.EqualTo("B"));
            Assert.That(controller.GetCurrentStateInfo(0)?.Data.name, Is.EqualTo("Idle"));
        }

        [UnityTest]
        public IEnumerator ExitExceptionStillDisposesRemovedCloneAndPreservesOtherLayer()
        {
            var source = Asset("ThrowingExit");
            var other = Asset("OtherLayer");
            var trace = new Trace
            {
                Exit = state =>
                {
                    if (state.CurrentStateInfo.Instance.LayerName == "A")
                    {
                        throw new InvalidOperationException("Expected exit failure.");
                    }
                }
            };
            var controller = Host(source, "A", trace);
            controller.AddRuntimeFsmController(other, "B");
            yield return null;
            var instance = controller.GetCurrentStateInfo("A").Instance;
            var clone = instance.RuntimeFsmController;
            Assert.Throws<InvalidOperationException>(() => controller.RemoveRuntimeFsmController("A"));
            Assert.That(instance.IsDisposed, Is.True);
            Assert.That(controller.GetCurrentStateInfo("B")?.Data.name, Is.EqualTo("Idle"));
            yield return null;
            Assert.That(!clone, Is.True);
        }

        [UnityTest]
        public IEnumerator InvalidTypeReportsAssetLayerStateAndDoesNotPreventValidController()
        {
            var invalid = Asset("Invalid");
            invalid.states[0].stateScripts[0].className = "Missing.State.Type";
            LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("Asset 'YL.Behavior.Invalid', layer 'Bad'.*state 'Idle'.*Missing.State.Type"));
            var badHost = Host(invalid, "Bad");
            var good = Host(Asset("Valid"), "Good");
            yield return null;
            Assert.That(badHost.RuntimeFsmController, Is.Empty);
            Assert.That(good.GetCurrentStateInfo(0)?.Data.name, Is.EqualTo("Idle"));
        }

        [UnityTest]
        public IEnumerator ImmediateTransitionCycleLogsOnceWithoutThrowingOrHangingAtZeroTimeScale()
        {
            var source = Asset("Cycle");
            source.transitions.Clear();
            source.transitions.Add(new FsmTransitionData { fromStateName = "Idle", toStateName = "Run", autoSwitch = true });
            source.transitions.Add(new FsmTransitionData { fromStateName = "Run", toStateName = "Idle", autoSwitch = true });
            float oldScale = Time.timeScale;
            try
            {
                Time.timeScale = 0;
                LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("transition cycle budget exceeded"));
                var controller = Host(source, "Cycle");
                yield return null;
                Assert.That(controller.IsInitialized, Is.True);
                Assert.That(controller.GetCurrentStateInfo(0)?.Data.name, Is.Not.Empty);
            }
            finally
            {
                Time.timeScale = oldScale;
            }
        }

        [UnityTest]
        public IEnumerator BothFsmImplementationsRunAndExitWithoutSharingExecutionState()
        {
            var asset = Host(Asset("Coexist"), "Asset");
            var lightweight = Fsm<int>.Allocate().Init(null);
            try
            {
                lightweight.State(1).OnUpdate(() => lightweight.ChangeState(2));
                lightweight.State(2);
                lightweight.SetUp(1);
                yield return null;
                // Kept in the Unity gate: JIT of the real method resolves Time.deltaTime even on its switch path.
                lightweight.Update();
                Assert.That(lightweight.CurrentStateId, Is.EqualTo(2));
                Assert.That(lightweight.SecondsOfCurrentState, Is.Zero);
                Assert.That(lightweight.FrameCountOfCurrentState, Is.EqualTo(1));
                Assert.That(asset.GetCurrentStateInfo(0)?.Data.name, Is.EqualTo("Idle"));
                asset.SetBool("Go", true);
                Assert.That(asset.GetCurrentStateInfo(0)?.Data.name, Is.EqualTo("Run"));
                Assert.That(lightweight.CurrentStateId, Is.EqualTo(2));
                asset.enabled = false;
                Assert.That(lightweight.IsRunning, Is.True);
            }
            finally
            {
                lightweight.RecycleToCache();
            }
        }
    }
}
