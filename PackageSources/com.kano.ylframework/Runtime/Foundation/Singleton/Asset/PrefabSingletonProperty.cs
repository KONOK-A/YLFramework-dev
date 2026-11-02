using System;
using UnityEngine;
using Object = UnityEngine.Object;

namespace YLFramework.Singletons
{
    /// <summary>
    /// 通过预制体创建并持有 MonoBehaviour 单例。
    /// 默认按类型名从 Resources 加载预制体，可通过 InstanceWithLoader 替换加载方式。
    /// InstanceWithLoader 属于单次 Play Session 的运行期配置，进入 Play 时会恢复为默认加载方式。
    /// </summary>
    /// <typeparam name="T">从场景或预制体实例上取得的单例组件类型。</typeparam>
    public class PrefabSingletonProperty<T> where T : MonoBehaviour
    {
        private static readonly Func<string, GameObject> _defaultLoader = Resources.Load<GameObject>;

        /// <summary>
        /// 预制体加载方式，可替换（例如接入 ResService）
        /// </summary>
        public static Func<string, GameObject> PrefabLoader = _defaultLoader;

        private static T _instance;
        private static T _disposedInstance;
        private static bool _hasWarnedCreationFailure;
        private static bool _isCreating;

        /// <summary>登记预制体单例缓存及加载配置的新 Play 会话重置。</summary>
        static PrefabSingletonProperty()
        {
            SingletonSessionState.RegisterSessionReset(ResetSessionState);
        }

        /// <summary>
        /// 临时替换预制体加载方式并返回实例：配置仅对本次 Play 会话有效，进入下一次 Play 会恢复默认 Resources 加载。
        /// </summary>
        /// <param name="loader">预制体加载委托，参数为类型名；为 null 时创建会失败并输出一次警告。</param>
        /// <returns>已有缓存或本次取得的组件；预制体为空或缺少目标组件时警告一次，加载器异常直接传播。</returns>
        public static T InstanceWithLoader(Func<string, GameObject> loader)
        {
            PrefabLoader = loader;
            return Instance;
        }

        /// <summary>
        /// 单例实例；首次访问时先查找场景，找不到则按 <see cref="PrefabLoader"/> 加载预制体并实例化
        /// （默认按类型名从 Resources 加载），实例化后 DontDestroyOnLoad；退出流程中仅返回已有缓存，不再查找或创建。
        /// 场景无实例且加载器返回空或预制体缺少目标组件时，返回 null 并警告一次；加载器异常直接传播。
        /// 同帧内刚被 Dispose 的实例会被跳过。
        /// 加载器内部重入 Instance 时不会递归创建。
        /// </summary>
        public static T Instance
        {
            get
            {
                if (_instance == null)
                {
                    if (SingletonSessionState.IsApplicationQuitting)
                    {
                        return null;
                    }

                    _instance = FindSceneInstance();
                    if (_instance == null)
                    {
                        _instance = CreateFromPrefab();
                    }

                    if (_instance != null)
                    {
                        _disposedInstance = null;
                        _hasWarnedCreationFailure = false;
                    }
                    else if (!_isCreating)
                    {
                        WarnCreationFailed();
                    }
                }

                return _instance;
            }
        }

        /// <summary>
        /// 当前是否已经持有单例实例（不会触发创建）
        /// </summary>
        public static bool HasInstance => _instance != null;

        /// <summary>
        /// 尝试获取已经持有的单例实例（不会触发创建）
        /// </summary>
        public static bool TryGetInstance(out T instance)
        {
            instance = _instance;
            return instance != null;
        }

        /// <summary>
        /// 销毁单例实例，下次访问 Instance 会重新创建
        /// </summary>
        public static void Dispose()
        {
            var instance = _instance;
            _instance = null;
            if (instance == null)
            {
                return;
            }

            //Unity 的 Destroy 到帧末才实际生效，同帧内重新创建时需要跳过该对象
            _disposedInstance = instance;
            Object.Destroy(instance.gameObject);
        }

        /// <summary>按 InstanceID 顺序采用场景中的活动组件，跳过尚未实际销毁的已处置实例。</summary>
        private static T FindSceneInstance()
        {
            foreach (var candidate in Object.FindObjectsByType<T>(FindObjectsSortMode.InstanceID))
            {
                if (candidate == null)
                {
                    continue;
                }

                if (ReferenceEquals(candidate, _disposedInstance))
                {
                    continue;
                }

                return candidate;
            }

            return null;
        }

        /// <summary>以重入保护加载并克隆预制体，取得目标组件并常驻；组件缺失时销毁克隆。</summary>
        private static T CreateFromPrefab()
        {
            if (_isCreating)
            {
                return null;
            }

            _isCreating = true;
            try
            {
                var prefab = PrefabLoader?.Invoke(typeof(T).Name);
                if (prefab == null)
                {
                    return null;
                }

                var prefabInstance = Object.Instantiate(prefab);
                var instance = prefabInstance.GetComponent<T>();
                if (instance == null)
                {
                    //预制体缺少目标组件：销毁克隆，不留下半初始化对象
                    if (prefabInstance)
                    {
                        Object.Destroy(prefabInstance);
                    }
                    return null;
                }

                Object.DontDestroyOnLoad(instance.gameObject);
                return instance;
            }
            finally
            {
                _isCreating = false;
            }
        }

        /// <summary>在当前连续创建失败期间仅输出一次警告，成功取得实例后允许重新报告。</summary>
        private static void WarnCreationFailed()
        {
            if (_hasWarnedCreationFailure)
            {
                return;
            }

            _hasWarnedCreationFailure = true;
            Debug.LogWarning(
                $"[PrefabSingletonProperty<{typeof(T).Name}>] 未能创建单例：场景中不存在该组件，且 PrefabLoader 返回空。" +
                $"默认按类型名从 Resources 加载 \"{typeof(T).Name}\"，请确认该预制体存在，或使用 InstanceWithLoader 提供加载方式。");
        }

        /// <summary>清空缓存、待销毁引用和创建标记，并恢复新 Play 会话的默认加载方式。</summary>
        private static void ResetSessionState()
        {
            _instance = null;
            _disposedInstance = null;
            _hasWarnedCreationFailure = false;
            _isCreating = false;
            PrefabLoader = _defaultLoader;
        }
    }
}
