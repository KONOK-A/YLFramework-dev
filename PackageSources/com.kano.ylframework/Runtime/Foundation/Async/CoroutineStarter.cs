using System.Collections;
using System.Runtime.CompilerServices;
using UnityEngine;
using YLFramework.Singletons;

namespace YLFramework.Async
{
    /// <summary>
    /// 非 MonoBehaviour 调用者的协程启动支撑。仅主线程使用，独立 DDOL 宿主跨场景运行。
    /// </summary>
    /// <remarks>
    /// 外部销毁或失活宿主不会自动恢复旧工作。停止协程不代表业务操作完成取消或资源释放，
    /// 也不保证 Unity 在原生停止路径执行迭代器的 finally。
    /// </remarks>
    public class CoroutineStarter : MonoBehaviour
    {
        private static CoroutineStarter _instance;
        private static ConditionalWeakTable<Coroutine, CoroutineStarter> _owners = new();
        private object _session;
        private bool _isReleased;

        /// <summary>登记新 Play 会话的协程宿主及句柄归属清理。</summary>
        static CoroutineStarter() => SingletonSessionState.RegisterSessionReset(ResetSession);

        /// <summary>自身是否仍是未释放的当前宿主，且属于当前执行会话。</summary>
        private bool IsCurrent => !_isReleased && ReferenceEquals(_instance, this) &&
            ReferenceEquals(_session, SingletonSessionState.ExecutionSession);

        /// <summary>让协程宿主跨场景存活，不在此处认领静态宿主身份。</summary>
        private void Awake() => DontDestroyOnLoad(gameObject);

        /// <summary>
        /// 启动跨场景协程。非 Play、退出或宿主不可运行时返回 null；正常启动保持 Unity 的同步首步行为。
        /// </summary>
        /// <remarks>返回 null 表示没有可用句柄，不保证迭代器从未执行；同步执行可能已完成或使宿主失效。</remarks>
        public static Coroutine Start(IEnumerator enumerator)
        {
            if (!SingletonSessionState.CanRunExecution)
            {
                return null;
            }

            var host = _instance;
            var wasCreated = !host;
            if (wasCreated)
            {
                host = CreateHost();
            }

            if (!host.IsCurrent || !host.gameObject.activeInHierarchy)
            {
                return null;
            }

            try
            {
                var coroutine = host.StartCoroutine(enumerator);
                // StartCoroutine 返回前会推进用户代码；只向捕获的原 owner 提交，不能收养到重入后的新宿主。
                if (!host || !host.IsCurrent || !host.gameObject.activeInHierarchy ||
                    !SingletonSessionState.CanRunExecution)
                {
                    if (host && coroutine != null)
                    {
                        host.StopCoroutine(coroutine);
                    }

                    return null;
                }

                if (coroutine != null)
                {
                    _owners.Add(coroutine, host);
                }

                return coroutine;
            }
            catch
            {
                if (wasCreated && host)
                {
                    host.ReleaseOwner();
                    Destroy(host.gameObject);
                }

                throw;
            }
        }

        /// <summary>
        /// 幂等停止静态 Start 返回的协程，仅路由到有效的原宿主；无记录、旧会话或已销毁句柄为空操作。
        /// </summary>
        /// <remarks>不创建、查找或借用替代宿主；不停止其他 MonoBehaviour 或实例 StartCoroutine 拥有的工作。</remarks>
        public static void Stop(Coroutine coroutine)
        {
            if (coroutine == null || !_owners.TryGetValue(coroutine, out var host))
            {
                return;
            }

            _owners.Remove(coroutine);
            if (host && host.IsCurrent)
            {
                host.StopCoroutine(coroutine);
            }
        }

        /// <summary>在激活前提交执行会话和缓存身份，创建失败时释放本次产生的宿主。</summary>
        private static CoroutineStarter CreateHost()
        {
            GameObject obj = null;
            CoroutineStarter host = null;
            try
            {
                obj = new GameObject("YLFramework.Foundation.CoroutineStarter");
                obj.SetActive(false);
                host = obj.AddComponent<CoroutineStarter>();
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

        /// <summary>幂等停止本宿主的全部协程，仅当自身仍为当前宿主时清空缓存和句柄归属表。</summary>
        private void ReleaseOwner()
        {
            if (_isReleased)
            {
                return;
            }

            _isReleased = true;
            if (ReferenceEquals(_instance, this))
            {
                _instance = null;
                _owners = new ConditionalWeakTable<Coroutine, CoroutineStarter>();
            }
            if (this)
            {
                StopAllCoroutines();
            }
        }

        /// <summary>先切断旧句柄路由，再停止并销毁旧宿主，防止其迟到消息接管新会话。</summary>
        private static void ResetSession()
        {
            var old = _instance;
            _instance = null;
            _owners = new ConditionalWeakTable<Coroutine, CoroutineStarter>();
            if (!old)
            {
                return;
            }

            old.ReleaseOwner();
            old.enabled = false;
            Destroy(old.gameObject);
        }

        /// <summary>仅由当前执行会话的宿主关闭门禁并释放自身，忽略旧宿主的迟到退出消息。</summary>
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

        /// <summary>外部销毁宿主时停止其协程并释放原有归属，不恢复旧工作。</summary>
        private void OnDestroy() => ReleaseOwner();
    }
}
