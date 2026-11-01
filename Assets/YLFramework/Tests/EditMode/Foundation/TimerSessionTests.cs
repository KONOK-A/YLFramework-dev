using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using YLFramework.Timing;
using Object = UnityEngine.Object;
using Timer = YLFramework.Timing.Timer;
using YLFramework.Async;
using YLFramework.Update;

namespace YLFramework.Foundation.Tests
{
    // 使用真实 Enter/ExitPlayMode；不写 session/gate，也不调用模拟 reset。
    [Category("YL.Special.Session")]
    public class TimerSessionTests
    {
        private const BindingFlags StaticPrivate = BindingFlags.Static | BindingFlags.NonPublic;
        private const BindingFlags InstancePrivate = BindingFlags.Instance | BindingFlags.NonPublic;
        private bool _isConfigured;
        private bool _wereOptionsEnabled;
        private EnterPlayModeOptions _previousOptions;
        private string _previousEnabledLine;
        private string _previousOptionsLine;
        private static string SettingsPath => Path.Combine(Path.GetDirectoryName(Application.dataPath), "ProjectSettings/EditorSettings.asset");
        private const string EnabledPattern = @"(?m)^  m_EnterPlayModeOptionsEnabled: [^\r\n]*";
        private const string OptionsPattern = @"(?m)^  m_EnterPlayModeOptions: [^\r\n]*";
        private ILogHandler _previousLogHandler;
        private ILogHandler _editorLogHandler;
        private SceneSetup[] _previousScenes;
        private string _scenePath;
        private TimerScope _firstScope;
        private ReusableTimer _firstSlot;
        private TimerScope _secondScope;
        private ReusableTimer _secondSlot;
        private int _exitNotifications;
        private int _exitRejections;
        private int _callbacks;
        private static Type Driver => typeof(Timer).Assembly.GetType("YLFramework.Timing.Internal.TimerDriver", true);
        private static Type Session => typeof(Timer).Assembly.GetType("YLFramework.Singletons.SingletonSessionState", true);
        private static object SessionIdentity => Session.GetProperty("ExecutionSession", StaticPrivate).GetValue(null);
        private static Component[] Hosts(Type type) => Object.FindObjectsByType(type,
            FindObjectsInactive.Include, FindObjectsSortMode.None).Cast<Component>().ToArray();

        [SetUp]
        public void SetUp()
        {
            Assert.That(EditorApplication.isPlayingOrWillChangePlaymode, Is.False);
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                Assert.That(SceneManager.GetSceneAt(i).isDirty, Is.False, "不保存或覆盖未保存的用户场景。");
            }

            AssertNoHosts();
            Assert.That(Driver.GetField("_instance", StaticPrivate).GetValue(null), Is.Null,
                "不接管既有默认 owner。");
            _wereOptionsEnabled = EditorSettings.enterPlayModeOptionsEnabled;
            _previousOptions = EditorSettings.enterPlayModeOptions;
            var settingsText = File.ReadAllText(SettingsPath);
            _previousEnabledLine = ReadSettingLine(settingsText, EnabledPattern);
            _previousOptionsLine = ReadSettingLine(settingsText, OptionsPattern);
            _previousLogHandler = Debug.unityLogger.logHandler;
            _editorLogHandler = _previousLogHandler;
            // Test Runner 自己的空场景不属于用户场景；原已保存场景在 finally 中恢复。
            _previousScenes = EditorSceneManager.GetSceneManagerSetup().Where(s => !string.IsNullOrEmpty(s.path)).ToArray();
            _scenePath = "Temp/YLTimer.Session." + Guid.NewGuid().ToString("N") + ".unity";
            _isConfigured = true;
            Debug.unityLogger.logHandler = _editorLogHandler;
            EditorSettings.enterPlayModeOptions = EnterPlayModeOptions.DisableDomainReload;
            EditorSettings.enterPlayModeOptionsEnabled = true;
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            Assert.That(EditorSceneManager.SaveScene(scene, _scenePath), Is.True, "只保存本测试的临时空场景。");
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (!_isConfigured)
            {
                yield break;
            }
            // 即使断言失败，也先完成真实退出；不以 Shutdown 补偿退出缺陷。
            if (EditorApplication.isPlaying)
            {
                yield return new ExitPlayMode();
            }

            try
            {
                _firstScope?.Dispose();
                _firstSlot?.Dispose();
                _secondScope?.Dispose();
                _secondSlot?.Dispose();
            }
            finally
            {
                EditorSettings.enterPlayModeOptions = _previousOptions;
                EditorSettings.enterPlayModeOptionsEnabled = _wereOptionsEnabled;
                RestoreSettingsFile();
                Debug.unityLogger.logHandler = _previousLogHandler;
                if (_previousScenes.Length > 0)
                {
                    EditorSceneManager.RestoreSceneManagerSetup(_previousScenes);
                }
                else
                {
                    EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                }

                System.IO.File.Delete(_scenePath);
                _isConfigured = false;
            }
            Assert.That(EditorSettings.enterPlayModeOptions, Is.EqualTo(_previousOptions));
            Assert.That(EditorSettings.enterPlayModeOptionsEnabled, Is.EqualTo(_wereOptionsEnabled));
            AssertNoHosts();
        }

        private static void AssertNoHosts()
        {
            foreach (var type in new[] { Driver, typeof(UpdateManager), typeof(CoroutineStarter) })
            {
                Assert.That(Hosts(type), Is.Empty, "独立测试场景，不接管或清除他人宿主：" + type.FullName);
            }
        }

        private static string ReadSettingLine(string text, string pattern)
        {
            var matches = Regex.Matches(text, pattern);
            Assert.That(matches.Count, Is.EqualTo(1), "只接受当前 EditorSettings 已确认的字段形状。");
            return matches[0].Value;
        }

        private void RestoreSettingsFile()
        {
            // Unity 2022.3 的单对象保存 API 未持久化这两项。只还原本 fixture 调整的两行，
            // 不全局 SaveAssets、不覆盖其余设置，不保存用户场景。保持原文件换行。
            var text = File.ReadAllText(SettingsPath);
            ReadSettingLine(text, EnabledPattern);
            ReadSettingLine(text, OptionsPattern);
            var restored = Regex.Replace(text, EnabledPattern, _ => _previousEnabledLine);
            restored = Regex.Replace(restored, OptionsPattern, _ => _previousOptionsLine);
            if (restored != text)
            {
                File.WriteAllText(SettingsPath, restored, new UTF8Encoding(false));
            }

            var disk = File.ReadAllText(SettingsPath);
            Assert.That(ReadSettingLine(disk, EnabledPattern), Is.EqualTo(_previousEnabledLine));
            Assert.That(ReadSettingLine(disk, OptionsPattern), Is.EqualTo(_previousOptionsLine));
        }

        private void OnRealExit(TimerCancellationReason reason)
        {
            _exitNotifications++;
            Assert.That(reason, Is.EqualTo(TimerCancellationReason.SchedulerDisposed));
            var before = Hosts(Driver);
            Assert.That(Session.GetProperty("CanRunExecution", StaticPrivate).GetValue(null), Is.False);
            Assert.Throws<InvalidOperationException>(() => Timer.Delay(0, () => _callbacks++));
            // 尝试新注册必须仍被既有公共 gate 拒绝，不创建其他执行领域的宿主。
            UpdateManager.AddUpdateEvent(() => _callbacks++).Unregister();
            Assert.That(CoroutineStarter.Start(Empty()), Is.Null);
            CollectionAssert.AreEquivalent(before, Hosts(Driver), "真实退出通知不能重建 TimerDriver。");
            Assert.That(Hosts(typeof(UpdateManager)), Is.Empty);
            Assert.That(Hosts(typeof(CoroutineStarter)), Is.Empty);
            _exitRejections++;
        }

        private static IEnumerator Empty()
        {
            yield break;
        }

        [UnityTest]
        public IEnumerator TwoRealPlaysIsolateOldRunsScopesSlotsAndLateMessagesWithoutExitRebuild()
        {
            yield return new EnterPlayMode(false);
            Assert.That(Application.isPlaying, Is.True);
            Assert.That(EditorSettings.enterPlayModeOptionsEnabled, Is.True);
            Assert.That(EditorSettings.enterPlayModeOptions, Is.EqualTo(EnterPlayModeOptions.DisableDomainReload),
                "Domain Reload 关闭，Scene Reload 正常。");
            Assert.That(SceneManager.GetActiveScene().path, Is.EqualTo(_scenePath));
            AssertNoHosts();
            var firstSession = SessionIdentity;
            _firstScope = new TimerScope();
            _firstSlot = new ReusableTimer();
            var pending = Timer.Delay(60, () => _callbacks++, onCancelled: OnRealExit);
            var paused = Timer.DelayFrames(60, () => _callbacks++, OnRealExit);
            Assert.That(paused.Pause(), Is.True);
            var scoped = _firstScope.PostNamed("same", 60, () => _callbacks++, onCancelled: OnRealExit);
            var stored = _firstSlot.Start(60, () => _callbacks++, onCancelled: OnRealExit);
            var oldHost = Hosts(Driver).Single();
            yield return null;
            yield return null;
            Assert.That(pending.IsActive && scoped.IsActive && stored.IsActive, Is.True);
            Assert.That(paused.Status, Is.EqualTo(TimerState.Paused));

            yield return new ExitPlayMode();
            Assert.That(_exitNotifications, Is.EqualTo(4));
            Assert.That(_exitRejections, Is.EqualTo(4));
            Assert.That(_callbacks, Is.Zero);
            foreach (var run in new[] { pending, paused, scoped, stored })
            {
                Assert.That(run.Status, Is.EqualTo(TimerState.Invalidated));
                Assert.That(run.Cancel() || run.Pause() || run.Resume(), Is.False);
            }
            Assert.That(_firstScope.Contains("same"), Is.False);
            AssertNoHosts();

            Assert.That(Debug.unityLogger.logHandler, Is.SameAs(_editorLogHandler),
                "Timer 退出不能替换应用既有的日志 handler。");
            yield return new EnterPlayMode(false);
            Assert.That(SessionIdentity, Is.Not.SameAs(firstSession), "实际 SubsystemRegistration 更换会话身份。");
            Assert.That(SceneManager.GetActiveScene().path, Is.EqualTo(_scenePath));
            AssertNoHosts();
            Assert.Throws<InvalidOperationException>(() => _firstScope.Delay(0));
            Assert.Throws<InvalidOperationException>(() => _firstSlot.Start(0));
            AssertNoHosts();
            _secondScope = new TimerScope();
            _secondSlot = new ReusableTimer();
            var current = Timer.RepeatFrames(0, 1, _ => _callbacks++, count: 2);
            var currentScoped = _secondScope.PostNamedFrames("same", 1, () => _callbacks++);
            var currentStored = _secondSlot.StartFrames(1, () => _callbacks++);
            var newHost = Hosts(Driver).Single();
            Assert.That(newHost, Is.Not.SameAs(oldHost));
            // 仅重放真实旧会话对象的迟到消息；会话切换和退出本身没有使用模拟。
            Driver.GetMethod("OnApplicationQuit", InstancePrivate).Invoke(oldHost, null);
            Driver.GetMethod("OnDestroy", InstancePrivate).Invoke(oldHost, null);
            _firstScope.Dispose();
            _firstSlot.Dispose();
            foreach (var run in new[] { pending, paused, scoped, stored })
            {
                Assert.That(run.Cancel() || run.Pause() || run.Resume(), Is.False);
            }

            Assert.That(current.IsActive && currentScoped.IsActive && currentStored.IsActive, Is.True);
            Assert.That(Session.GetProperty("CanRunExecution", StaticPrivate).GetValue(null), Is.True);
            Assert.That(Hosts(Driver).Single(), Is.SameAs(newHost));
            for (var frame = 0; frame < 6 && _callbacks < 4; frame++)
            {
                yield return null;
            }

            Assert.That(_callbacks, Is.EqualTo(4));
            foreach (var run in new[] { current, currentScoped, currentStored })
            {
                Assert.That(run.Status, Is.EqualTo(TimerState.Completed));
            }

            Assert.That(current.ExecutionCount, Is.EqualTo(2), "没有残留双驱动或旧回调。");
            var secondPending = Timer.Delay(60, () => _callbacks++, onCancelled: OnRealExit);

            yield return new ExitPlayMode();
            Assert.That(secondPending.Status, Is.EqualTo(TimerState.Invalidated));
            Assert.That(_exitNotifications, Is.EqualTo(5));
            Assert.That(_exitRejections, Is.EqualTo(5));
            Assert.That(_callbacks, Is.EqualTo(4));
            AssertNoHosts();
        }
    }
}
