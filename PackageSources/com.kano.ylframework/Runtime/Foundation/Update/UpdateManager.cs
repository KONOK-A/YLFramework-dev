using System;
using UnityEngine;
using YLFramework.Singletons;
using YLFramework.Events;

namespace YLFramework.Update
{
    /// <summary>
    /// 公共 Unity 三相位消息桥。仅主线程使用，有效 Play 注册惰性创建独立 DDOL 宿主。
    /// 各相位按注册顺序通知，异常继续并统一报告；不承诺与其他 MonoBehaviour 的执行全序。
    /// </summary>
    /// <remarks>
    /// 每次注册均为独立身份。普通退订保留在途快照，领域结束仍需自己的存活/轮次检查。
    /// 空闲宿主保留到会话结束；外部销毁会结束全部旧订阅，后续新注册不会恢复旧工作。
    /// 不应由业务停用或销毁宿主，不提供公共 Tick、Clear 或 Shutdown。
    /// </remarks>
    public sealed class UpdateManager : MonoBehaviour
    {
        private static UpdateManager _instance;
        private readonly EventChannel _update = new();
        private readonly EventChannel _fixedUpdate = new();
        private readonly EventChannel _lateUpdate = new();
        private object _session;
        private bool _isReleased;

        /// <summary>登记新 Play 会话的宿主和三相位订阅清理。</summary>
        static UpdateManager() => SingletonSessionState.RegisterSessionReset(ResetSession);
        /// <summary>用于选择独立订阅通道的 Unity 消息相位。</summary>
        private enum Phase
        {
            /// <summary>逐渲染帧的 Update 相位。</summary>
            Update,
            /// <summary>固定时间步的 FixedUpdate 相位。</summary>
            FixedUpdate,
            /// <summary>每帧后段的 LateUpdate 相位。</summary>
            LateUpdate
        }
        /// <summary>自身是否仍为未释放的缓存宿主，且执行会话身份未过期。</summary>
        private bool IsCurrent => !_isReleased && ReferenceEquals(_instance, this) &&
            ReferenceEquals(_session, SingletonSessionState.ExecutionSession);
        /// <summary>取得指定相位的独立订阅通道。</summary>
        private EventChannel Channel(Phase phase) => phase == Phase.Update ? _update :
            phase == Phase.FixedUpdate ? _fixedUpdate : _lateUpdate;

        /// <summary>注册 Update，无效输入或退出期间返回空句柄，不创建宿主。</summary>
        public static IUnregister AddUpdateEvent(Action action) => Register(action, Phase.Update);
        /// <summary>注册 FixedUpdate，每次调用返回独立的精确注销身份。</summary>
        public static IUnregister AddFixedUpdateEvent(Action action) => Register(action, Phase.FixedUpdate);
        /// <summary>注册 LateUpdate，保持相位内注册顺序。</summary>
        public static IUnregister AddLateUpdateEvent(Action action) => Register(action, Phase.LateUpdate);
        /// <summary>移除最后相等的普通委托子序列；仅访问既有宿主。</summary>
        public static void RemoveUpdateEvent(Action action) => Unregister(action, Phase.Update);
        /// <summary>移除最后相等的 FixedUpdate 委托子序列，不创建宿主。</summary>
        public static void RemoveFixedUpdateEvent(Action action) => Unregister(action, Phase.FixedUpdate);
        /// <summary>移除最后相等的 LateUpdate 委托子序列，不创建宿主。</summary>
        public static void RemoveLateUpdateEvent(Action action) => Unregister(action, Phase.LateUpdate);

        /// <summary>通过执行门禁后按需创建宿主并注册；首次注册失败时回收本次新建的宿主。</summary>
        private static IUnregister Register(Action action, Phase phase)
        {
            if (action == null || !SingletonSessionState.CanRunExecution)
            {
                return EmptyUnregister.Instance;
            }

            var host = _instance;
            var created = !host;
            if (created)
            {
                host = CreateHost();
            }

            try
            {
                return host.Channel(phase).Register(action);
            }
            catch
            {
                if (created)
                {
                    host.ReleaseOwner();
                    Destroy(host.gameObject);
                }
                throw;
            }
        }

        /// <summary>先提交执行会话和缓存身份再激活独立常驻宿主，失败时释放本次创建的对象。</summary>
        private static UpdateManager CreateHost()
        {
            GameObject obj = null;
            UpdateManager host = null;
            try
            {
                obj = new GameObject("YLFramework.Foundation.UpdateManager");
                obj.SetActive(false);
                host = obj.AddComponent<UpdateManager>();
                host._session = SingletonSessionState.ExecutionSession;
                DontDestroyOnLoad(obj);
                _instance = host;
                obj.SetActive(true);
                return host;
            }
            catch
            {
                if (host)
                {
                    host.ReleaseOwner();
                }

                if (obj)
                {
                    Destroy(obj);
                }

                throw;
            }
        }

        /// <summary>仅从当前有效宿主移除匹配委托，不创建或查找替代宿主。</summary>
        private static void Unregister(Action action, Phase phase)
        {
            var host = _instance;
            if (action != null && host && host.IsCurrent)
            {
                host.Channel(phase).Unregister(action);
            }
        }

        /// <summary>将 Unity 的 Update 消息交给对应通道。</summary>
        private void Update() => Dispatch(_update);
        /// <summary>将 Unity 的 FixedUpdate 消息交给对应通道。</summary>
        private void FixedUpdate() => Dispatch(_fixedUpdate);
        /// <summary>将 Unity 的 LateUpdate 消息交给对应通道。</summary>
        private void LateUpdate() => Dispatch(_lateUpdate);
        /// <summary>在宿主身份与门禁有效时同步派发当前相位，采用继续通知策略并在边界统一记录异常。</summary>
        private void Dispatch(EventChannel channel)
        {
            if (!IsCurrent || !SingletonSessionState.CanRunExecution || !channel.HasListener)
            {
                return;
            }

            try
            {
                channel.TriggerAll();
            }
            catch (Exception error)
            {
                Debug.LogException(error);
            }
        }

        /// <summary>幂等结束三相位通道，仅清空自身占据的缓存，避免旧宿主清理替换实例。</summary>
        private void ReleaseOwner()
        {
            if (_isReleased)
            {
                return;
            }

            _isReleased = true;
            _update.Dispose();
            _fixedUpdate.Dispose();
            _lateUpdate.Dispose();
            if (ReferenceEquals(_instance, this))
            {
                _instance = null;
            }
        }

        /// <summary>先撤下旧宿主缓存，再结束其全部订阅并停用、销毁旧对象。</summary>
        private static void ResetSession()
        {
            var old = _instance;
            _instance = null;
            if (!old)
            {
                return;
            }

            old.ReleaseOwner();
            old.enabled = false;
            Destroy(old.gameObject);
        }

        /// <summary>仅由当前执行会话的宿主关闭门禁并释放自身，旧宿主的迟到退出消息不影响新会话。</summary>
        private void OnApplicationQuit()
        {
            if (!IsCurrent)
            {
                return;
            }

            SingletonSessionState.StopExecution();
            ReleaseOwner();
            enabled = false;
            Destroy(gameObject);
        }
        /// <summary>外部销毁时结束本宿主的全部订阅，不将旧订阅转移给后续宿主。</summary>
        private void OnDestroy() => ReleaseOwner();
    }
}
