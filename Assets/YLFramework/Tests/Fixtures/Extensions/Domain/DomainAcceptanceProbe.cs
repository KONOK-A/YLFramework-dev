using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;
using YLFramework.Buff;
using YLFramework.Logging;
using YLFramework.Mission;
using YLFramework.RedDots;
using YLFramework.Resource;
using YLFramework.Skill;
using ResourceLocation = YLFramework.Resource.ResourceLocation;

namespace YLFramework.Tests.DomainAcceptance
{
    // 配置仅以字符串引用三种派生类；消费者自己的定向link.xml保留它们，不扩大Runtime裁剪范围。
    public sealed class ReceiverSkill : SkillBase
    {
    }
    public sealed class ReceiverBuff : BuffBase
    {
    }
    public sealed class ReceiverMission : MissionBase
    {
    }

    public sealed class DomainAcceptanceProbe : MonoBehaviour, IEquippableSkill, IAddableBuff
    {
        [global::UnityEngine.Serialization.FormerlySerializedAs("_resourcePrefix")]
        [SerializeField] private string resourcePrefix;
        [global::UnityEngine.Serialization.FormerlySerializedAs("_run")]
        [SerializeField] private string run;
        [global::UnityEngine.Serialization.FormerlySerializedAs("_expectedBackend")]
        [SerializeField] private string expectedBackend;
        [global::UnityEngine.Serialization.FormerlySerializedAs("_expectLegacyLogger")]
        [SerializeField] private bool expectLegacyLogger;
        private readonly object _logLock = new();
        private readonly List<string> _unexpected = new();
        private readonly Dictionary<LogType, int> _expected = new();
        private Receipt _receipt;
        private string _resultPath;
        private ILogHandler _original;
        private ILogHandler _predecessor;

        private sealed class ForwardingHandler : ILogHandler
        {
            private readonly ILogHandler _destination;
            private readonly string _prefix;
            internal readonly List<LogType> ProbeKinds = new();

            internal ForwardingHandler(ILogHandler destination, string prefix)
            {
                _destination = destination;
                _prefix = prefix;
            }

            public void LogException(Exception error, UnityEngine.Object context)
            {
                if (error.Message.StartsWith(_prefix, StringComparison.Ordinal))
                {
                    ProbeKinds.Add(LogType.Exception);
                }
                _destination.LogException(error, context);
            }

            public void LogFormat(LogType type, UnityEngine.Object context, string format, params object[] args)
            {
                if (string.Format(format, args).StartsWith(_prefix, StringComparison.Ordinal))
                {
                    ProbeKinds.Add(type);
                }
                _destination.LogFormat(type, context, format, args);
            }
        }

        [Serializable]
        private sealed class Receipt
        {
            public string run;
            public string backend;
            public string unity;
            public string graphics;
            public string originalHandler;
            public string originalAssembly;
            public string loggerFile;
            public string error;
            public bool passed;
            public bool quitObserved;
            public bool batchMode;
            public bool development;
            public bool legacyExpected;
            public bool handlerRestored;
            public bool resourceReturned;
            public bool poolsReused;
            public int pid;
            public int width;
            public int height;
            public int expectedErrors;
            public int expectedAsserts;
            public int expectedExceptions;
            public string[] unexpectedErrors;
        }

        public Dictionary<int, SkillBase> Skills { get; } = new();
        public List<BuffBase> Buffs { get; } = new();
        public bool CanRelease() => true;
        public bool CanAdd(int id) => true;
        public void OnAddBuff(BuffBase buff)
        {
        }
        public void OnRemoveBuff(BuffBase buff)
        {
        }

        private IEnumerator Start()
        {
#if ENABLE_IL2CPP
            const string Backend = "IL2CPP";
#else
            const string Backend = "Mono2x";
#endif
            _original = Debug.unityLogger.logHandler;
            _predecessor = _original;
            _receipt = new Receipt
            {
                run = run,
                backend = Backend,
                unity = Application.unityVersion,
                pid = System.Diagnostics.Process.GetCurrentProcess().Id,
                graphics = SystemInfo.graphicsDeviceType + ":" + SystemInfo.graphicsDeviceName,
                width = Screen.width,
                height = Screen.height,
                batchMode = Application.isBatchMode,
                development = Debug.isDebugBuild,
                legacyExpected = expectLegacyLogger,
                originalHandler = _original.GetType().FullName,
                originalAssembly = _original.GetType().Assembly.GetName().Name
            };
            Application.logMessageReceivedThreaded += ObserveLog;
            Application.quitting += OnQuitting;
            var work = Run();
            Exception failure = null;
            while (true)
            {
                bool next = false;
                object current = null;
                try
                {
                    next = work.MoveNext();
                    if (next)
                    {
                        current = work.Current;
                    }
                }
                catch (Exception error)
                {
                    failure = error;
                }
                if (failure != null || !next)
                {
                    break;
                }

                yield return current;
            }
            try
            {
                (work as IDisposable)?.Dispose();
                SkillManager.CloseHost(this);
                BuffManager.CloseHost(this);
                SkillManager.Shutdown();
                BuffManager.Shutdown();
                MissionManager.Shutdown();
                LoggerManager.Disable();
                ReddotManager.Reset();
                Require(ReferenceEquals(Debug.unityLogger.logHandler, _predecessor),
                    "Logger did not restore its actual predecessor; leave an unexpected later owner untouched.");
                Debug.unityLogger.logHandler = _original;
                _receipt.handlerRestored = ReferenceEquals(Debug.unityLogger.logHandler, _original);
                Require(_receipt.handlerRestored, "Logger did not restore its actual predecessor.");
            }
            catch (Exception error)
            {
                failure = failure == null ? error : new AggregateException(failure, error);
            }
            _receipt.error = failure?.ToString();
            _receipt.passed = failure == null;
            RefreshReceipt();
            if (_receipt.passed)
            {
                Debug.Log("YL_C_ACCEPTANCE_PASS run=" + run + " backend=" + Backend);
            }
            else
            {
                Debug.LogError("YL_C_ACCEPTANCE_FAIL run=" + run + " " + _receipt.error);
            }

            if (!string.IsNullOrEmpty(_resultPath))
            {
                WriteReceipt();
            }
#if !UNITY_EDITOR
            Application.Quit(_receipt.passed ? 0 : 1);
#endif
        }

        private IEnumerator Run()
        {
            var args = Environment.GetCommandLineArgs();
            int result = Array.IndexOf(args, "-domainResult"), run = Array.IndexOf(args, "-domainRun");
            Require(result >= 0 && result + 1 < args.Length && run >= 0 && run + 1 < args.Length && args[run + 1] == this.run, "Missing exact runtime Run binding.");
            _resultPath = args[result + 1];
            Require(Path.IsPathRooted(_resultPath) && Directory.Exists(Path.GetDirectoryName(_resultPath)) && !File.Exists(_resultPath), "Result path is not a new owned artifact.");
            Require(_receipt.backend == expectedBackend && !Application.isBatchMode && !Debug.isDebugBuild, "Actual release backend mismatch.");
            Require(SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null && Screen.width > 0 && Screen.height > 0, "Real graphical Player required.");
            Require(ReferenceEquals(Debug.unityLogger.logHandler, _original), "The fixture must not replace the application's handler before its probe.");
            Require(!LoggerManager.IsEnabled, "YL Logger must remain passive until explicitly enabled.");
            string logger = Path.Combine(Path.GetDirectoryName(_resultPath), "logger-" + this.run);
            Require(!Directory.Exists(logger), "Do not overwrite a prior Logger probe.");
            // 保留既有序列化开关；共存分支使用测试自有转发器，不要求外部包或修改应用 handler 的内部状态。
            var forwarding = expectLegacyLogger ? new ForwardingHandler(_original, this.run + "/probe-") : null;
            _predecessor = forwarding ?? _original;
            Debug.unityLogger.logHandler = _predecessor;
            var session = LoggerManager.Enable(new LoggerOptions { WriteToFile = true, Directory = logger });
            var kinds = new List<LogType>();
            session.OnLogEvent += (type, _) => kinds.Add(type);
            Debug.Log(this.run + "/probe-log");
            Debug.LogWarning(this.run + "/probe-warning");
            Debug.LogError(this.run + "/probe-error");
            // Debug.LogAssertion受UNITY_ASSERTIONS条件裁剪；Release仍通过真实Unity logger验证Assert分派。
            Debug.unityLogger.Log(LogType.Assert, this.run + "/probe-assert");
            Debug.LogException(new InvalidOperationException(this.run + "/probe-exception"));
            Require(kinds.SequenceEqual(new[] { LogType.Log, LogType.Warning, LogType.Error, LogType.Assert, LogType.Exception }), "Five actual Debug kinds did not reach the YL writer.");
            if (forwarding != null)
            {
                Require(forwarding.ProbeKinds.SequenceEqual(kinds), "Five actual Debug kinds must reach the owned predecessor exactly once.");
            }
            _receipt.loggerFile = session.LogFilePath;
            var source = ReddotManager.RegisterPath("C/Inbox");
            ReddotManager.AddPath("C/Inbox");
            ReddotManager.RemovePath("C/Inbox");
            Require(ReddotManager.ShouldBeDisplayed("C") && !ReddotManager.ShouldBeDisplayed("Ca"), "Independent Reddot source or segment semantics failed.");
            source.Dispose();
            Require(!ReddotManager.ShouldBeDisplayed("C"), "Reddot token was not returned.");

            var entries = new[]
            {
                new ResourceCatalogEntry("skills", new ResourceAddress("c-acceptance", "skills"), new ResourceLocation(ResourceSourceId.Resources, resourcePrefix + "/Skills", typeof(SkillsConfig))),
                new ResourceCatalogEntry("buffs", new ResourceAddress("c-acceptance", "buffs"), new ResourceLocation(ResourceSourceId.Resources, resourcePrefix + "/Buffs", typeof(BuffsConfig))),
                new ResourceCatalogEntry("missions", new ResourceAddress("c-acceptance", "missions"), new ResourceLocation(ResourceSourceId.Resources, resourcePrefix + "/Missions", typeof(MissionsConfig)))
            };
            Require(ResourceSystem.Current == null, "Do not take over an existing Resource authority.");
            using var initialized = ResourceSystem.InitializeAsync(new ResourceConfiguration(new[] { new ResourceCatalog("c-acceptance", entries) }));
            var resources = initialized.TakeResult();
            using var independent = resources.Acquire<SkillsConfig>(new ResourceAddress("c-acceptance", "skills"));
            SkillManager.LoadConfig(resources, new ResourceAddress("c-acceptance", "skills"));
            BuffManager.LoadConfig(resources, new ResourceAddress("c-acceptance", "buffs"));
            MissionManager.LoadConfig(resources, new ResourceAddress("c-acceptance", "missions"));
            var skill = SkillManager.AddSkills(this, 100);
            var buff = BuffManager.AddBuff(this, 100);
            Require(skill.GetType().FullName == "YLFramework.Tests.DomainAcceptance.ReceiverSkill" && buff.GetType().FullName == "YLFramework.Tests.DomainAcceptance.ReceiverBuff", "AOT reflected Skill/Buff subtype missing.");
            Require(!ReferenceEquals(skill.SkillData, independent.Asset.skills[0]), "Skill instance is modifying its definition.");
            Require(skill.Release() == ReleaseSkillStatus.Success, "Skill release failed.");
            var mission = MissionManager.GetMission("Missions", 100);
            Require(mission.GetType().FullName == "YLFramework.Tests.DomainAcceptance.ReceiverMission", "AOT reflected Mission subtype missing.");
            mission.State = MissionState.InProgress;
            MissionManager.SetFloat("Missions", "amount", 1);
            Require(mission.State == MissionState.Completed && mission.MissionConfig.rewards[0].count == 2, "Mission condition/reward state failed.");
            yield return new WaitForSecondsRealtime(0.25f);
            Require(skill.IsCooldown && !skill.IsReleasing && Buffs.Count == 0 && buff.Owner == null, "Real Update cooldown/expiry failed.");
            SkillManager.RemoveSkills(this, 100);
            var reusedSkill = SkillManager.AddSkills(this, 100);
            var reusedBuff = BuffManager.AddBuff(this, 100, 0f);
            Require(ReferenceEquals(skill, reusedSkill) && ReferenceEquals(buff, reusedBuff) && reusedBuff.ExistedTime == 0, "Domain pool instances were not safely reused.");
            SkillManager.CloseHost(this);
            BuffManager.CloseHost(this);
            Require(Skills.Count == 0 && Buffs.Count == 0 && reusedSkill.Releaser == null && reusedBuff.Owner == null, "CloseHost did not return pooled runtime state.");
            _receipt.poolsReused = true;
            SkillManager.Shutdown();
            BuffManager.Shutdown();
            MissionManager.Shutdown();
            Require(resources.GetDiagnosticsSnapshot().RecordCount == 1, "Managers released an independent root lease or leaked theirs.");
            independent.Dispose();
            Require(resources.GetDiagnosticsSnapshot().RecordCount == 0, "Real root leases were not returned.");
            using (var shutdown = resources.ShutdownAsync())
            {
                Require(shutdown.Status == ResourceRequestStatus.Succeeded, "Owned Resource authority did not close.");
            }

            _receipt.resourceReturned = ResourceSystem.Current == null;
            Require(_receipt.resourceReturned, "Owned Resource authority remained active.");
            LoggerManager.Disable();
            Require(ReferenceEquals(Debug.unityLogger.logHandler, _predecessor), "YL replaced its actual predecessor.");
            using (var file = new FileStream(session.LogFilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                Require(file.Length > 0, "Writer remained locked/empty.");
            }

            foreach (string marker in new[] { "log", "warning", "error", "assert", "exception" })
            {
                Require(File.ReadAllText(session.LogFilePath).Contains(this.run + "/probe-" + marker), "Writer did not preserve an expected Debug probe.");
            }
            // 留出真实显示与晚退日志窗口；OS侧仍须独立确认窗口、图形设备和正常退出。
            ScreenCapture.CaptureScreenshot(Path.Combine(Path.GetDirectoryName(_resultPath), this.run + ".png"));
            yield return new WaitForSecondsRealtime(0.75f);
            RefreshReceipt();
            Require(_receipt.expectedErrors == 1 && _receipt.expectedAsserts == 1 && _receipt.expectedExceptions == 1 && _receipt.unexpectedErrors.Length == 0,
                "Unexpected errors or missing/duplicated exact Logger probes.");
        }

        private void ObserveLog(string condition, string stack, LogType type)
        {
            if (type != LogType.Error && type != LogType.Assert && type != LogType.Exception)
            {
                return;
            }

            lock (_logLock)
            {
                bool expected = type == LogType.Error && condition == run + "/probe-error" ||
                    type == LogType.Assert && condition == run + "/probe-assert" ||
                    type == LogType.Exception && condition == "InvalidOperationException: " + run + "/probe-exception";
                if (expected)
                {
                    _expected[type] = _expected.TryGetValue(type, out int count) ? count + 1 : 1;
                }
                else
                {
                    _unexpected.Add(type + ":" + condition + Environment.NewLine + stack);
                }
            }
        }

        private void RefreshReceipt()
        {
            lock (_logLock)
            {
                _receipt.expectedErrors = _expected.TryGetValue(LogType.Error, out int errors) ? errors : 0;
                _receipt.expectedAsserts = _expected.TryGetValue(LogType.Assert, out int asserts) ? asserts : 0;
                _receipt.expectedExceptions = _expected.TryGetValue(LogType.Exception, out int exceptions) ? exceptions : 0;
                _receipt.unexpectedErrors = _unexpected.ToArray();
                _receipt.passed &= _unexpected.Count == 0;
            }
        }

        private void WriteReceipt() => File.WriteAllText(_resultPath, JsonUtility.ToJson(_receipt, true));
        private void OnQuitting()
        {
            _receipt.quitObserved = true;
            RefreshReceipt();
            if (!string.IsNullOrEmpty(_resultPath))
            {
                WriteReceipt();
            }
        }
        private void OnDestroy()
        {
            Application.logMessageReceivedThreaded -= ObserveLog;
            Application.quitting -= OnQuitting;
        }
        private void OnGUI() => GUI.Label(new Rect(24, 24, Screen.width - 48, 80), "C DOMAIN ACCEPTANCE\n" + run + " / " + expectedBackend);
        private static void Require(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }
    }
}
