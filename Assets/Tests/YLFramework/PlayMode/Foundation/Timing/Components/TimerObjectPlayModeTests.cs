using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.TestTools;
using YLFramework.Timing;
using Object = UnityEngine.Object;
using Timer = YLFramework.Timing.Timer;

namespace YLFramework.Core.Tests
{
    public class TimerObjectPlayModeTests
    {
        private const BindingFlags PrivateStatic = BindingFlags.NonPublic | BindingFlags.Static;
        private const BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;
        private readonly List<Component> _hosts = new();
        private readonly List<TimerObject> _probes = new();
        private bool _isIsolated;
        private float _previousTimeScale;
        private static Type DriverType => typeof(Timer).Assembly.GetType("YLFramework.Timing.Internal.TimerDriver", true);
        private static Type SessionType => typeof(Timer).Assembly.GetType("YLFramework.Singletons.SingletonSessionState", true);
        private static Component[] FindHosts() => Object.FindObjectsByType(DriverType,
            FindObjectsInactive.Include, FindObjectsSortMode.None).Cast<Component>().ToArray();
        private static TimerHandle Current(TimerObject probe) =>
            (TimerHandle)typeof(TimerObject).GetField("_current", PrivateInstance).GetValue(probe);

        [SetUp]
        public void SetUp()
        {
            _isIsolated = false;
            Assert.That(FindHosts(), Is.Empty, "Do not take over an existing TimerDriver, including inactive hosts.");
            Assert.That(DriverType.GetField("_instance", PrivateStatic).GetValue(null), Is.Null,
                "Do not take over an unreleased static owner.");
            Assert.That(SessionType.GetProperty("CanRunExecution", PrivateStatic).GetValue(null), Is.True,
                "Do not reopen the execution gate.");
            _previousTimeScale = Time.timeScale;
            _isIsolated = true;
            Time.timeScale = 1;
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (!_isIsolated)
            {
                yield break;
            }

            try
            {
                foreach (var probe in _probes)
                {
                    if (!probe)
                    {
                        continue;
                    }

                    probe.onCompleted.RemoveAllListeners();
                    probe.onAborted.RemoveAllListeners();
                    Object.Destroy(probe.gameObject);
                }
                _probes.Clear();
                yield return null;
            }
            finally
            {
                Time.timeScale = _previousTimeScale;
                foreach (var host in _hosts)
                {
                    if (host)
                    {
                        Object.Destroy(host.gameObject);
                    }
                }

                _hosts.Clear();
            }
            yield return null;
        }

        private TimerObject Create(float delay = 0, bool ignoreTimeScale = true)
        {
            var obj = new GameObject("YLCoreTimer.TestProbe");
            obj.SetActive(false);
            var probe = obj.AddComponent<TimerObject>();
            _probes.Add(probe);
            probe.delay = delay;
            probe.ignoreTimeScale = ignoreTimeScale;
            probe.onCompleted = new UnityEvent();
            probe.onAborted = new UnityEvent();
            return probe;
        }

        private void Activate(TimerObject probe)
        {
            var before = FindHosts();
            Assert.That(before.All(_hosts.Contains), Is.True, "Do not adopt a foreign host created during the test.");
            try
            {
                probe.gameObject.SetActive(true);
            }
            finally
            {
                foreach (var host in FindHosts())
                {
                    if (!before.Contains(host) && !_hosts.Contains(host))
                    {
                        _hosts.Add(host);
                    }
                }
            }
        }

        private static IEnumerator Until(Func<bool> condition)
        {
            var deadline = Time.realtimeSinceStartupAsDouble + 3;
            while (!condition() && Time.realtimeSinceStartupAsDouble < deadline)
            {
                yield return null;
            }

            Assert.That(condition(), Is.True, "Real Unity frames did not complete the expected operation before the deadline.");
        }

        [Test]
        public void CoreIdentityAndSerializedEventLayoutRemainValid()
        {
            var type = typeof(TimerObject);
            Assert.That(type.FullName, Is.EqualTo("YLFramework.Timing.TimerObject"));
            Assert.That(type.Assembly.GetName().Name, Is.EqualTo("YLFramework.Runtime"));
            Assert.That(type.BaseType, Is.EqualTo(typeof(MonoBehaviour)));
            foreach (var expected in new[]
            {
                ("delay", typeof(float)), ("ignoreTimeScale", typeof(bool)),
                ("onCompleted", typeof(UnityEvent)), ("onAborted", typeof(UnityEvent))
            })
            {
                var field = type.GetField(expected.Item1, BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                Assert.That(field, Is.Not.Null, expected.Item1);
                Assert.That(field.FieldType, Is.EqualTo(expected.Item2), expected.Item1);
                Assert.That(field.IsNotSerialized || field.IsInitOnly, Is.False, expected.Item1);
            }
        }

        [UnityTest]
        public IEnumerator ComponentDisableAndGameObjectDisableCancelOnceAndReenableStartsNewRun()
        {
            var probe = Create(60);
            var actions = 0;
            var aborts = 0;
            probe.onCompleted.AddListener(() => actions++);
            probe.onAborted.AddListener(() => aborts++);
            Activate(probe);
            var first = Current(probe);

            probe.enabled = false;
            Assert.That(probe.gameObject.activeInHierarchy, Is.True);
            Assert.That(first.Status, Is.EqualTo(TimerState.Cancelled));
            Assert.That(aborts, Is.EqualTo(1));
            probe.enabled = false;
            yield return null;
            Assert.That(actions, Is.Zero);
            Assert.That(aborts, Is.EqualTo(1));

            probe.enabled = true;
            var second = Current(probe);
            Assert.That(first.Status, Is.EqualTo(TimerState.Invalidated));
            Assert.That(second.IsActive, Is.True);
            probe.gameObject.SetActive(false);
            Assert.That(second.Status, Is.EqualTo(TimerState.Cancelled));
            Assert.That(aborts, Is.EqualTo(2), "Component and GO listeners must not notify twice for one run.");
            probe.gameObject.SetActive(false);
            yield return null;
            Assert.That(actions, Is.Zero);
            Assert.That(aborts, Is.EqualTo(2));

            probe.delay = 0;
            Activate(probe);
            var third = Current(probe);
            Assert.That(second.Status, Is.EqualTo(TimerState.Invalidated));
            Assert.That(third.IsActive, Is.True);
            yield return Until(() => third.IsFinished);
            Assert.That(third.Status, Is.EqualTo(TimerState.Completed));
            Assert.That(actions, Is.EqualTo(1));
            probe.gameObject.SetActive(false);
            Assert.That(aborts, Is.EqualTo(2));

            var slot = (ReusableTimer)typeof(TimerObject).GetField("_timer", PrivateInstance).GetValue(probe);
            Object.Destroy(probe.gameObject);
            yield return null;
            Assert.That(slot.IsDisposed, Is.True);
            Assert.That(aborts, Is.EqualTo(2));
        }

        [UnityTest]
        public IEnumerator SuccessfulCallbackCanDisableAndRestartWithoutAbortOrOldCompletionCancellingNewRun()
        {
            var probe = Create();
            var actions = 0;
            var aborts = 0;
            TimerHandle restarted = default;
            probe.onAborted.AddListener(() => aborts++);
            probe.onCompleted.AddListener(() =>
            {
                actions++;
                probe.gameObject.SetActive(false);
                if (actions != 2)
                {
                    return;
                }

                probe.gameObject.SetActive(true);
                restarted = Current(probe);
            });
            Activate(probe);
            var first = Current(probe);
            yield return Until(() => first.IsFinished);
            Assert.That(first.Status, Is.EqualTo(TimerState.Completed));
            Assert.That(probe.gameObject.activeSelf, Is.False);
            Assert.That(actions, Is.EqualTo(1));
            Assert.That(aborts, Is.Zero, "Self-disable during a consumed success callback is not cancellation.");

            Activate(probe);
            var second = Current(probe);
            yield return Until(() => actions == 3);
            Assert.That(second.Status, Is.EqualTo(TimerState.Invalidated));
            Assert.That(second.Cancel(), Is.False);
            Assert.That(restarted.Status, Is.EqualTo(TimerState.Completed));
            Assert.That(restarted.ExecutionCount, Is.EqualTo(1));
            Assert.That(probe.gameObject.activeSelf, Is.False);
            Assert.That(aborts, Is.Zero);
            yield return null;
            Assert.That(actions, Is.EqualTo(3));
        }

        [UnityTest]
        public IEnumerator AbortCallbackCanReenableComponentWithoutOldCleanupCancellingNewRun()
        {
            var probe = Create(60);
            var actions = 0;
            var aborts = 0;
            TimerHandle restarted = default;
            probe.onCompleted.AddListener(() => actions++);
            probe.onAborted.AddListener(() =>
            {
                aborts++;
                probe.delay = 0;
                probe.enabled = true;
                restarted = Current(probe);
            });
            Activate(probe);
            var first = Current(probe);
            probe.enabled = false;
            Assert.That(aborts, Is.EqualTo(1));
            Assert.That(probe.isActiveAndEnabled, Is.True);
            Assert.That(first.Status, Is.EqualTo(TimerState.Invalidated));
            Assert.That(first.Cancel(), Is.False);
            Assert.That(restarted.IsActive, Is.True);

            yield return Until(() => restarted.IsFinished);
            Assert.That(restarted.Status, Is.EqualTo(TimerState.Completed));
            Assert.That(actions, Is.EqualTo(1));
            probe.enabled = false;
            probe.gameObject.SetActive(false);
            yield return null;
            Assert.That(aborts, Is.EqualTo(1));
            Assert.That(actions, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator CallbackFailureFaultsWithoutAbortAndReenableCanStartAgain()
        {
            var probe = Create();
            var failures = 0;
            var aborts = 0;
            probe.onAborted.AddListener(() => aborts++);
            probe.onCompleted.AddListener(() =>
            {
                failures++;
                throw new InvalidOperationException("YLCoreTimer.ExpectedFailure");
            });
            LogAssert.Expect(LogType.Exception, new Regex("InvalidOperationException: YLCoreTimer.ExpectedFailure"));
            Activate(probe);
            var failed = Current(probe);
            yield return Until(() => failed.IsFinished);
            Assert.That(failed.Status, Is.EqualTo(TimerState.Faulted));
            Assert.That(failed.ExecutionCount, Is.EqualTo(1));
            Assert.That(failures, Is.EqualTo(1));
            probe.gameObject.SetActive(false);
            Assert.That(aborts, Is.Zero, "Fault and subsequent disable must not invoke Abort.");

            var actions = 0;
            probe.onCompleted.RemoveAllListeners();
            probe.onCompleted.AddListener(() => actions++);
            Activate(probe);
            var next = Current(probe);
            yield return Until(() => next.IsFinished);
            Assert.That(next.Status, Is.EqualTo(TimerState.Completed));
            Assert.That(actions, Is.EqualTo(1));
            Assert.That(failures, Is.EqualTo(1));
            Assert.That(aborts, Is.Zero);
        }

        [UnityTest]
        public IEnumerator IgnoreTimeScaleSelectsUnscaledWhileScaledWaitsForTimeToResume()
        {
            Time.timeScale = 0;
            var scaled = Create(0.02f, false);
            var unscaled = Create(0.02f, true);
            var scaledActions = 0;
            var unscaledActions = 0;
            var aborts = 0;
            scaled.onCompleted.AddListener(() => scaledActions++);
            unscaled.onCompleted.AddListener(() => unscaledActions++);
            scaled.onAborted.AddListener(() => aborts++);
            unscaled.onAborted.AddListener(() => aborts++);
            Activate(scaled);
            Activate(unscaled);
            var scaledRun = Current(scaled);
            var unscaledRun = Current(unscaled);
            yield return Until(() => unscaledRun.IsFinished);
            Assert.That(unscaledRun.Status, Is.EqualTo(TimerState.Completed));
            Assert.That(unscaledActions, Is.EqualTo(1));
            Assert.That(scaledActions, Is.Zero);
            Assert.That(scaledRun.IsActive, Is.True);

            Time.timeScale = 1;
            yield return Until(() => scaledRun.IsFinished);
            Assert.That(scaledRun.Status, Is.EqualTo(TimerState.Completed));
            Assert.That(scaledActions, Is.EqualTo(1));
            Assert.That(aborts, Is.Zero);
        }
    }
}
