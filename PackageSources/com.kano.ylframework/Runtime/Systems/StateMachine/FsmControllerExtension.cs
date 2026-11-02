using System.Collections;
using System.Collections.Generic;
using UnityEngine;


namespace YLFramework.StateMachine
{

    /// <summary>管理按名启动的跨场景状态机宿主及名称哈希缓存，不承载单层执行内核。</summary>
    public class FsmControllerExtension
    {
        #region 单例

        private static FsmControllerExtension _instance;
        private static bool _isQuitting;

        internal static FsmControllerExtension Instance
        {
            get
            {
#pragma warning disable IDE0074 // 使用复合分配
                if (_instance == null)
                {
                    _instance = new FsmControllerExtension();
                }
#pragma warning restore IDE0074 // 使用复合分配

                return _instance;
            }
        }


        /// <summary>每轮运行清空静态登记和哈希缓存，并重新绑定应用退出标记。</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetSession()
        {
            _instance = null;
            _stringHashes.Clear();
            _hashStrings.Clear();
            _isQuitting = false;
            Application.quitting -= OnQuitting;
            Application.quitting += OnQuitting;
        }

        /// <summary>应用退出后禁止静态入口再创建状态机宿主。</summary>
        private static void OnQuitting() => _isQuitting = true;

        /// <summary>仅供内部按需建立本轮静态宿主登记表。</summary>
        private FsmControllerExtension()
        {
        }

        #endregion


        #region 静态字段

        private Dictionary<string, FsmController> _fsms = new Dictionary<string, FsmController>();

        private Transform _fsmParent;

        #endregion

        #region 静态属性


        private static Transform FsmParent
        {
            get
            {
                if (!Instance._fsmParent && YLFramework.Foundation.ApplicationTool.IsPlaying)
                {
                    GameObject obj = new GameObject("YL Asset FSM Controllers");
                    GameObject.DontDestroyOnLoad(obj);
                    Instance._fsmParent = obj.transform;
                }

                return Instance._fsmParent;
            }
        }

        #endregion

        #region 静态方法

        /// <summary>
        /// 创建按名登记的跨场景宿主，适合没有业务 GameObject 的状态管理；实际初始化仍由组件 Start 执行。
        /// </summary>
        /// <param name="fsmName">状态机名称</param>
        /// <param name="fsm">状态配置文件</param>
        /// <param name="userData">自定义参数或数据</param>
        /// <returns>新宿主；非运行期、退出中、参数无效或名称已占用时返回 null。</returns>
        public static FsmController StartupFsm(string fsmName, RuntimeFsmController fsm, object userData = null)
        {
            if (_isQuitting || !Application.isPlaying || !YLFramework.Foundation.ApplicationTool.IsPlaying)
            {
                return null;
            }

            if (!fsm || string.IsNullOrWhiteSpace(fsmName))
            {
                return null;
            }

            if (GetFsm(fsmName))
            {
                return null;
            }

            GameObject obj = new GameObject(fsmName);
            obj.transform.SetParent(FsmParent, false);
            FsmController controller = obj.AddComponent<FsmController>();
            controller.UserDataValue = userData;
            controller.AddRuntimeFsmController(fsm);
            Instance._fsms.Add(fsmName, controller);
            return controller;
        }

        /// <summary>
        /// 查询由静态启动入口登记的存活宿主，不搜索场景中直接挂载的控制器。
        /// </summary>
        /// <param name="fsmName">状态机名称</param>
        /// <returns>对应宿主；名称无效、未登记或对象已销毁时返回 null。</returns>
        public static FsmController GetFsm(string fsmName)
        {
            if (string.IsNullOrEmpty(fsmName))
            {
                return null;
            }

            if (Instance._fsms.TryGetValue(fsmName, out var controller))
            {
                if (controller)
                {
                    return controller;
                }

                Instance._fsms.Remove(fsmName);
            }
            return null;
        }

        /// <summary>
        /// 先移除按名登记，再请求销毁宿主 GameObject；实例清理由组件销毁回调执行。
        /// </summary>
        /// <param name="fsmName">状态机名称</param>
        public static void RemoveFsm(string fsmName)
        {
            if (string.IsNullOrEmpty(fsmName))
            {
                return;
            }

            if (!Instance._fsms.ContainsKey(fsmName))
            {
                return;
            }

            FsmController controller = Instance._fsms[fsmName];
            Instance._fsms.Remove(fsmName);
            if (controller)
            {
                GameObject.Destroy(controller.gameObject);
            }
        }

        /// <summary>在控制器销毁时按对象身份移除残余登记，不创建新的静态管理实例。</summary>
        /// <param name="controller">已进入销毁流程的控制器。</param>
        internal static void Unregister(FsmController controller)
        {
            if (_instance == null)
            {
                return;
            }

            string key = null;
            foreach (var pair in _instance._fsms)
            {
                if (ReferenceEquals(pair.Value, controller))
                {
                    key = pair.Key;
                    break;
                }
            }

            if (key != null)
            {
                _instance._fsms.Remove(key);
            }
        }

        #endregion


        #region StringToHash

        private static Dictionary<string, int> _stringHashes = new Dictionary<string, int>();
        private static Dictionary<int, string> _hashStrings = new Dictionary<int, string>();


        /// <summary>使用 Animator 的名称哈希算法并缓存结果；反查仅保留同哈希首次登记的名称。</summary>
        /// <param name="eventName">非 null 的状态或参数名称。</param>
        /// <returns>名称对应的整数哈希。</returns>
        public static int StringToHash(string eventName)
        {
            if (_stringHashes.ContainsKey(eventName))
            {
                return _stringHashes[eventName];
            }

            int hash = Animator.StringToHash(eventName);

            _stringHashes.Add(eventName, hash);
            if (!_hashStrings.ContainsKey(hash))
            {
                _hashStrings.Add(hash, eventName);
            }

            return _stringHashes[eventName];
        }

        /// <summary>反查本轮已缓存的名称，仅供诊断，不保证哈希碰撞时一一对应。</summary>
        /// <param name="hash">已计算的名称哈希。</param>
        /// <returns>首次登记的名称；未登记时为空字符串。</returns>
        internal static string HashToString(int hash)
        {
            if (_hashStrings.ContainsKey(hash))
            {
                return _hashStrings[hash];
            }

            return string.Empty;
        }

        #endregion

    }
}
