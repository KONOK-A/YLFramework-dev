using System;
using UnityEngine;

namespace YLFramework.Singletons
{
    /// <summary>
    /// 加载并缓存 ScriptableObject 单例资产，不克隆加载结果。
    /// 默认按类型名从 Resources 加载资产，可通过 InstanceWithLoader 替换加载方式。
    /// InstanceWithLoader 属于单次 Play Session 的运行期配置，进入 Play 时会恢复为默认加载方式。
    /// </summary>
    /// <typeparam name="T">要加载并缓存的 ScriptableObject 资产类型。</typeparam>
    public class ScriptableSingletonProperty<T> where T : ScriptableObject
    {
        private static readonly Func<string, T> _defaultLoader = Resources.Load<T>;

        /// <summary>
        /// 资产加载方式，可替换（例如接入 ResService）
        /// </summary>
        public static Func<string, T> ScriptableLoader = _defaultLoader;

        private static T _instance;
        private static bool _isLoadedFromDefaultLoader;
        private static bool _hasWarnedLoadFailure;
        private static bool _isCreating;

        /// <summary>登记资产缓存及加载配置的新 Play 会话重置。</summary>
        static ScriptableSingletonProperty()
        {
            SingletonSessionState.RegisterSessionReset(ResetSessionState);
        }

        /// <summary>
        /// 临时替换资产加载方式并返回实例：配置仅对本次 Play 会话有效，进入下一次 Play 会恢复默认 Resources 加载。
        /// </summary>
        /// <param name="loader">资产加载委托，参数为类型名；为 null 时本次加载会失败并输出一次警告。</param>
        /// <returns>已有缓存或本次加载的资产；加载器返回 null 时警告一次，抛出的异常不拦截。</returns>
        public static T InstanceWithLoader(Func<string, T> loader)
        {
            ScriptableLoader = loader;
            return Instance;
        }

        /// <summary>
        /// 单例资产；首次访问时按 <see cref="ScriptableLoader"/> 加载（默认按类型名从 Resources 加载）。
        /// 加载器返回 null 时输出一次警告（重复访问只警告一次），稍后可重试；加载器异常直接传播。
        /// 加载器内部重入 Instance 时不会递归加载。
        /// </summary>
        public static T Instance
        {
            get
            {
                if (_instance == null && !_isCreating)
                {
                    _isCreating = true;
                    try
                    {
                        _isLoadedFromDefaultLoader = ReferenceEquals(ScriptableLoader, _defaultLoader);
                        _instance = ScriptableLoader?.Invoke(typeof(T).Name);
                    }
                    finally
                    {
                        _isCreating = false;
                    }

                    if (_instance != null)
                    {
                        _hasWarnedLoadFailure = false;
                    }
                    else
                    {
                        WarnLoadFailed();
                    }
                }

                return _instance;
            }
        }

        /// <summary>
        /// 当前是否已经持有单例实例（不会触发加载）
        /// </summary>
        public static bool HasInstance => _instance != null;

        /// <summary>
        /// 尝试获取已经持有的单例实例（不会触发加载）
        /// </summary>
        public static bool TryGetInstance(out T instance)
        {
            instance = _instance;
            return instance != null;
        }

        /// <summary>
        /// 释放单例，下次访问 Instance 会重新加载。
        /// 仅当资产由默认 Resources 加载方式载入时才卸载资产，自定义 Loader 载入的资产由调用方自行管理。
        /// </summary>
        public static void Dispose()
        {
            if (_instance != null && _isLoadedFromDefaultLoader)
            {
                Resources.UnloadAsset(_instance);
            }

            _instance = null;
            _isLoadedFromDefaultLoader = false;
        }

        /// <summary>在当前连续失败期间只报告一次空加载结果，成功加载后可重新报告。</summary>
        private static void WarnLoadFailed()
        {
            if (_hasWarnedLoadFailure)
            {
                return;
            }

            _hasWarnedLoadFailure = true;
            Debug.LogWarning(
                $"[ScriptableSingletonProperty<{typeof(T).Name}>] 未能加载单例资产：ScriptableLoader 返回空。" +
                $"默认按类型名从 Resources 加载 \"{typeof(T).Name}\"，请确认该资产存在，或使用 InstanceWithLoader 提供加载方式。");
        }

        /// <summary>清空缓存和失败、重入标记，并将新 Play 会话的加载方式恢复为 Resources。</summary>
        private static void ResetSessionState()
        {
            _instance = null;
            _isLoadedFromDefaultLoader = false;
            _hasWarnedLoadFailure = false;
            _isCreating = false;
            ScriptableLoader = _defaultLoader;
        }
    }
}
