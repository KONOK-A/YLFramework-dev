using System;
using UnityEngine;
using YLFramework.Singletons;

namespace YLFramework.Timing.Internal
{
    /// <summary>默认计时服务的独立宿主，提供渲染样本和调度器生命周期，不接管 Update 广播或 Action 系统推进。</summary>
    internal sealed class TimerDriver : MonoBehaviour
    {
        private static TimerDriver _instance;
        private static bool _isShuttingDown;
        private TimerScheduler _scheduler;
        private object _session;
        private bool _isReleased;
        private bool _isReady;
        private int _unityFrame;
        private long _frame;
        private long _secondsFrame;
        private double _scaled;
        private double _unscaled;

        /// <summary>登记新 Play 会话的默认计时宿主清理。</summary>
        static TimerDriver() => SingletonSessionState.RegisterSessionReset(ResetSession);
        /// <summary>自身是否仍是当前未释放宿主，且属于当前执行会话。</summary>
        private bool IsCurrent => !_isReleased && ReferenceEquals(_instance, this) &&
            ReferenceEquals(_session, SingletonSessionState.ExecutionSession);

        /// <summary>取得或惰性创建默认内核；门禁关闭时拒绝，停用或释放的存活宿主须先 Shutdown 才能重建。</summary>
        internal static TimerScheduler GetScheduler()
        {
            if (_isShuttingDown || !SingletonSessionState.CanRunExecution)
            {
                throw new InvalidOperationException("Timer execution is not available.");
            }

            var host = _instance;
            if (!ReferenceEquals(host, null))
            {
                if (!host)
                {
                    _instance = null;
                    host.ReleaseOwner();
                }
                else if (!host.IsCurrent || !host.isActiveAndEnabled)
                {
                    throw new InvalidOperationException("Timer driver is disabled or released; shut it down before restarting.");
                }
            }
            if (!_instance)
            {
                CreateHost();
            }

            return _instance._scheduler;
        }

        /// <summary>在激活前建立会话身份、自动内核与缓存，创建失败时回收本次宿主。</summary>
        private static void CreateHost()
        {
            GameObject obj = null;
            TimerDriver host = null;
            try
            {
                obj = new GameObject("YLFramework.Timing.TimerDriver");
                obj.SetActive(false);
                host = obj.AddComponent<TimerDriver>();
                host._session = SingletonSessionState.ExecutionSession;
                host._scheduler = new TimerScheduler(host.Synchronize);
                DontDestroyOnLoad(obj);
                _instance = host;
                obj.SetActive(true);
            }
            catch
            {
                if (host)
                {
                    host.ReleaseOwner();
                }

                if (ReferenceEquals(_instance, host))
                {
                    _instance = null;
                }

                if (obj)
                {
                    Destroy(obj);
                }

                throw;
            }
        }

        /// <summary>为登记或句柄操作刷新合法样本，不直接派发到期任务。</summary>
        private void Synchronize() => Synchronize(false);
        /// <summary>核对执行权限后同步渲染帧和可用秒样本；宿主失效则结束其调度责任。</summary>
        private void Synchronize(bool update)
        {
            if (!IsCurrent || !SingletonSessionState.CanRunExecution || !this || !isActiveAndEnabled)
            {
                ReleaseOwner();
                return;
            }
            // 首个 Update 之前不读取 Awake 中未定义的时间/帧；Fixed 步只更新已知帧身份。
            if (!_isReady && !update)
            {
                return;
            }

            var unityFrame = Time.frameCount;
            var newFrame = !_isReady || unityFrame != _unityFrame;
            if (newFrame)
            {
                _frame = checked(_frame + 1);
                _unityFrame = unityFrame;
            }
            var validSeconds = update || !Time.inFixedTimeStep;
            if (validSeconds && _secondsFrame != _frame)
            {
                _scaled = Time.timeAsDouble;
                _unscaled = Time.unscaledTimeAsDouble;
                _secondsFrame = _frame;
            }
            _isReady = true;
            _scheduler.Sample(_scaled, _unscaled, _frame, validSeconds);
        }

        /// <summary>采集本次 Update 的合法渲染样本，再由自动内核推进一次到期派发。</summary>
        private void Update()
        {
            Synchronize(true);
            if (_isReleased)
            {
                return;
            }

            _scheduler.AdvanceAutomatic();
        }

        /// <summary>先标记释放再幂等关闭内核，取消通知重入时不能继续使用本宿主。</summary>
        private void ReleaseOwner()
        {
            if (_isReleased)
            {
                return;
            }

            _isReleased = true;
            _scheduler?.Dispose();
        }

        /// <summary>先撤下缓存并禁止关闭期间重建，再失效旧任务和销毁旧宿主；不改变执行会话令牌。</summary>
        internal static void Shutdown()
        {
            if (_isShuttingDown)
            {
                return;
            }

            _isShuttingDown = true;
            try
            {
                var old = _instance;
                _instance = null;
                if (ReferenceEquals(old, null))
                {
                    return;
                }

                old.ReleaseOwner();
                if (old)
                {
                    old.enabled = false;
                    Destroy(old.gameObject);
                }
            }
            finally
            {
                _isShuttingDown = false;
            }
        }

        /// <summary>新 Play 会话初始化时关闭旧默认宿主，不恢复旧任务。</summary>
        private static void ResetSession() => Shutdown();
        /// <summary>宿主停用即永久结束本内核的责任，重新启用不恢复旧任务。</summary>
        private void OnDisable() => ReleaseOwner();
        /// <summary>外部销毁时释放内核，并且仅移除自身仍占据的缓存。</summary>
        private void OnDestroy()
        {
            ReleaseOwner();
            if (ReferenceEquals(_instance, this))
            {
                _instance = null;
            }
        }
        /// <summary>仅由当前会话宿主关闭执行门禁并释放内核，忽略旧宿主的迟到退出通知。</summary>
        private void OnApplicationQuit()
        {
            if (!IsCurrent)
            {
                return;
            }

            SingletonSessionState.StopExecution();
            ReleaseOwner();
        }
    }
}
