using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using YLFramework.Events;
using Object = UnityEngine.Object;
using YLFramework.Update;

namespace YLFramework.Foundation.Tests
{
    public class UpdateManagerPlayModeTests
    {
        private readonly List<Component> _hosts = new();
        private readonly List<IUnregister> _tokens = new();
        private bool _isIsolated;
        private static Type UpdateType => typeof(UpdateManager);
        private static Component[] FindHosts() => Object.FindObjectsByType(UpdateType,
            FindObjectsInactive.Include, FindObjectsSortMode.None).Cast<Component>().ToArray();

        [SetUp]
        public void SetUp()
        {
            _isIsolated = false;
            Assert.That(FindHosts(), Is.Empty, "不接管已有公共宿主。");
            _isIsolated = true;
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (!_isIsolated)
            {
                yield break;
            }

            foreach (var token in _tokens)
            {
                token?.Unregister();
            }

            _tokens.Clear();
            foreach (var host in _hosts)
            {
                if (host)
                {
                    Object.Destroy(host.gameObject);
                }
            }

            _hosts.Clear();
            yield return null;
        }

        private object Call(string method, Action callback)
        {
            try
            {
                return UpdateType.GetMethod(method).Invoke(null, new object[] { callback });
            }
            finally
            {
                foreach (var host in FindHosts())
                {
                    if (!_hosts.Contains(host))
                    {
                        _hosts.Add(host);
                    }
                }
            }
        }
        private IUnregister Register(string phase, Action callback)
        {
            var token = Call("Add" + phase + "Event", callback) as IUnregister;
            Assert.That(token, Is.Not.Null, "每次注册必须返回精确身份。");
            _tokens.Add(token);
            return token;
        }

        [UnityTest]
        public IEnumerator NullRegistrationAndUnsubscribeDoNotCreateHost()
        {
            Action callback = () => { };
            foreach (var phase in new[] { "Update", "FixedUpdate", "LateUpdate" })
            {
                Call("Remove" + phase + "Event", callback);
                Call("Add" + phase + "Event", null);
            }
            Assert.That(FindHosts(), Is.Empty);
            yield break;
        }

        [UnityTest]
        public IEnumerator UnityDrivesThreePhasesAndKeepsIdleHostAcrossSceneUnload()
        {
            var update = 0;
            var fixedUpdate = 0;
            var late = 0;
            var wrongPhase = false;
            var scene = UnityEngine.SceneManagement.SceneManager.CreateScene("YLUpdate.Phases");
            var previous = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            try
            {
                UnityEngine.SceneManagement.SceneManager.SetActiveScene(scene);
                var u = Register("Update", () =>
                {
                    update++;
                    wrongPhase |= Time.inFixedTimeStep;
                });
                var f = Register("FixedUpdate", () =>
                {
                    fixedUpdate++;
                    wrongPhase |= !Time.inFixedTimeStep;
                });
                var l = Register("LateUpdate", () =>
                {
                    late++;
                    wrongPhase |= Time.inFixedTimeStep;
                });
                var host = FindHosts().Single();
                Assert.That(host.name, Is.EqualTo("YLFramework.Foundation.UpdateManager"));
                UnityEngine.SceneManagement.SceneManager.SetActiveScene(previous);
                yield return UnityEngine.SceneManagement.SceneManager.UnloadSceneAsync(scene);
                var deadline = Time.realtimeSinceStartup + 3;
                while ((update == 0 || fixedUpdate == 0 || late == 0) && Time.realtimeSinceStartup < deadline)
                {
                    yield return null;
                }

                Assert.That(update > 0 && fixedUpdate > 0 && late > 0, Is.True);
                Assert.That(wrongPhase, Is.False);
                u.Unregister();
                f.Unregister();
                l.Unregister();
                var counts = new[] { update, fixedUpdate, late };
                yield return null;
                yield return null;
                CollectionAssert.AreEqual(counts, new[] { update, fixedUpdate, late });
                Assert.That(FindHosts().Single(), Is.SameAs(host), "空闲宿主保留到会话结束。");
            }
            finally
            {
                UnityEngine.SceneManagement.SceneManager.SetActiveScene(previous);
                if (scene.IsValid() && scene.isLoaded)
                {
                    UnityEngine.SceneManagement.SceneManager.UnloadSceneAsync(scene);
                }
            }
        }

        [UnityTest]
        public IEnumerator ExactDuplicatesMulticastAndSnapshotFollowSharedEventsContract()
        {
            var calls = new List<string>();
            Action shared = () => calls.Add("shared");
            var first = Register("Update", shared);
            var second = Register("Update", shared);
            first.Unregister();
            first.Unregister();
            Action tail = () => calls.Add("tail");
            var multi = Register("Update", shared + tail);
            Call("RemoveUpdateEvent", shared);
            FindHosts().Single().SendMessage("Update");
            CollectionAssert.AreEqual(new[] { "shared", "tail" }, calls);
            second.Unregister();
            multi.Unregister();
            calls.Clear();
            IUnregister removed = null;
            IUnregister added = null;
            var head = Register("Update", () =>
            {
                calls.Add("head");
                removed.Unregister();
                added ??= Register("Update", () => calls.Add("added"));
            });
            removed = Register("Update", () => calls.Add("removed"));
            FindHosts().Single().SendMessage("Update");
            CollectionAssert.AreEqual(new[] { "head", "removed" }, calls);
            calls.Clear();
            FindHosts().Single().SendMessage("Update");
            CollectionAssert.AreEqual(new[] { "head", "added" }, calls);
            head.Unregister();
            added.Unregister();
            yield break;
        }

        [UnityTest]
        public IEnumerator CallbackErrorsContinueAndFailedSubscriptionsRemain()
        {
            var observed = 0;
            Register("Update", () => throw new InvalidOperationException("YLUpdate.FirstFailure"));
            Register("Update", () => observed++);
            Register("Update", () => throw new ArgumentException("YLUpdate.SecondFailure"));
            var previousHandler = Debug.unityLogger.logHandler;
            var captured = new ExceptionCapture(previousHandler);
            Debug.unityLogger.logHandler = captured;
            try
            {
                for (var round = 0; round < 2; round++)
                {
                    // Unity 将聚合异常渲染为首个 inner 的消息；原始报告对象仍须保留两个错误。
                    LogAssert.Expect(LogType.Exception, new Regex("InvalidOperationException: YLUpdate.FirstFailure"));
                    FindHosts().Single().SendMessage("Update");
                    Assert.That(captured.Error, Is.TypeOf<AggregateException>());
                    var errors = ((AggregateException)captured.Error).InnerExceptions;
                    Assert.That(errors.Count, Is.EqualTo(2));
                    Assert.That(errors[0].Message, Is.EqualTo("YLUpdate.FirstFailure"));
                    Assert.That(errors[1].Message, Is.EqualTo("YLUpdate.SecondFailure"));
                }
            }
            finally
            {
                Debug.unityLogger.logHandler = previousHandler;
            }
            Assert.That(observed, Is.EqualTo(2));
            foreach (var token in _tokens)
            {
                token.Unregister();
            }

            yield break;
        }

        private sealed class ExceptionCapture : ILogHandler
        {
            private readonly ILogHandler _previous;
            internal Exception Error;
            internal ExceptionCapture(ILogHandler previous) => this._previous = previous;
            public void LogException(Exception exception, Object context)
            {
                Error = exception;
                _previous.LogException(exception, context);
            }
            public void LogFormat(LogType type, Object context, string format, params object[] args)
                => _previous.LogFormat(type, context, format, args);
        }

        [UnityTest]
        public IEnumerator DestroyedOwnerCutsSnapshotAndOldTokenCannotCancelNewOwner()
        {
            var oldCalls = 0;
            var newCalls = 0;
            Register("Update", () => Object.DestroyImmediate(FindHosts().Single().gameObject));
            var oldToken = Register("Update", () => oldCalls++);
            FindHosts().Single().SendMessage("Update");
            Assert.That(oldCalls, Is.Zero, "owner 销毁截断旧快照。");
            Assert.That(FindHosts(), Is.Empty, "OnDestroy 不自行重建。");
            Register("Update", () => newCalls++);
            oldToken.Unregister();
            FindHosts().Single().SendMessage("Update");
            Assert.That(newCalls, Is.EqualTo(1));
            yield break;
        }

        [UnityTest]
        public IEnumerator SessionResetRevokesOldHostAndQuitRejectsNewRegistration()
        {
            var oldCalls = 0;
            var newCalls = 0;
            var oldToken = Register("Update", () => oldCalls++);
            var oldHost = FindHosts().Single();
            var state = typeof(ApplicationTool).Assembly.GetType("YLFramework.Singletons.SingletonSessionState", true);
            state.GetMethod("ResetForNewSession", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, null);
            Assert.That(((Behaviour)oldHost).enabled, Is.False);
            oldHost.SendMessage("Update");
            var current = Register("Update", () => newCalls++);
            var newHost = FindHosts().Single(h => ((Behaviour)h).enabled);
            Assert.That(newHost, Is.Not.SameAs(oldHost));
            oldHost.SendMessage("OnApplicationQuit");
            oldHost.SendMessage("OnDestroy");
            oldToken.Unregister();
            newHost.SendMessage("Update");
            Assert.That(oldCalls, Is.Zero);
            Assert.That(newCalls, Is.EqualTo(1));
            newHost.SendMessage("OnApplicationQuit");
            current.Unregister();
            Call("RemoveUpdateEvent", () => { });
            var refused = Call("AddUpdateEvent", (Action)(() => newCalls++)) as IUnregister;
            Assert.That(refused, Is.Not.Null);
            Assert.That(FindHosts().Length, Is.EqualTo(2));
            newHost.SendMessage("Update");
            Assert.That(newCalls, Is.EqualTo(1));
            // 独立测试场景中恢复门禁，连续真实 Play 验收归 C 批次。
            state.GetMethod("ResetForNewSession", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, null);
            yield return null;
            Assert.That(FindHosts(), Is.Empty);
        }
    }
}
