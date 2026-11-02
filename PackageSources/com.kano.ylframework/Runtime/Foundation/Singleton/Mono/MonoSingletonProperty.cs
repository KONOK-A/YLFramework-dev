using System;
using System.Collections.Generic;
using UnityEngine;

namespace YLFramework.Singletons
{
    /// <summary>
    /// MonoBehaviour 单例缓存入口：为每个封闭类型 T 维护当前实例引用。
    /// 查找/创建策略见 <see cref="SingletonCreator.CreateMonoSingleton{T}"/>；
    /// 同帧内刚被 Dispose 的实例会被跳过（Unity 尚未完成实际销毁），避免把待销毁对象重新认领回来。
    /// 进入新的 Play 会话时会清空实例引用。
    /// </summary>
    /// <typeparam name="T">单例类型。</typeparam>
    public class MonoSingletonProperty<T> where T : MonoBehaviour, ISingleton
    {
        private static T _instance;
        private static readonly HashSet<T> _disposedInstances = new();
        private static readonly object _lock = new();

        /// <summary>为当前组件类型登记新 Play 会话的缓存重置。</summary>
        static MonoSingletonProperty()
        {
            SingletonSessionState.RegisterSessionReset(ResetSessionState);
        }

        /// <summary>
        /// 单例实例；缓存缺失时查找或创建，非播放模式或退出阶段不创建。
        /// </summary>
        public static T Instance
        {
            get => GetInstance();
        }

        /// <summary>
        /// 显式指定查找/创建行为的 Instance 入口；默认参数与 <see cref="Instance"/> 行为一致。
        /// </summary>
        /// <param name="allowEditMode">非播放模式下是否允许查找/创建。</param>
        /// <param name="includeInactive">查找时是否包含未激活对象。</param>
        /// <param name="persistent">采用/创建顶级实例时是否 DontDestroyOnLoad。</param>
        /// <param name="onCreated">走路径或新建流程后调用的回调；直接采用场景候选时不调用，路径复用组件时也会调用。</param>
        /// <returns>已有缓存或取得的实例；缓存缺失且当前模式不允许查找/创建时返回 null。</returns>
        /// <remarks>查找选项仅在缓存缺失时生效；初始化回调返回后才写入缓存，回调中重入可能递归创建。</remarks>
        public static T GetInstance(bool allowEditMode = false, bool includeInactive = false,
            bool persistent = false, Action<T> onCreated = null)
        {
            _disposedInstances.RemoveWhere(instance => instance == null);
            lock (_lock)
            {
                if (_instance == null)
                {
                    _instance = SingletonCreator.CreateMonoSingleton<T>(null, allowEditMode, includeInactive,
                        persistent, onCreated, _disposedInstances.Contains);
                }
            }

            return _instance;
        }

        /// <summary>
        /// 释放一个宿主实例：清空缓存（若它是当前实例）、将其加入“已处置”集合（避免同一帧内被重新认领），
        /// 禁用组件后销毁 GameObject 或仅销毁组件；不会影响后续替换实例。
        /// </summary>
        /// <param name="instance">要释放的实例。</param>
        /// <param name="destroyGameObject">true 销毁整个 GameObject；false 只销毁组件。</param>
        public static void ReleaseHost(T instance, bool destroyGameObject = true)
        {
            ResetIfCurrent(instance);
            if (instance == null)
            {
                return;
            }

            _disposedInstances.Add(instance);
            instance.enabled = false;
            UnityEngine.Object target = destroyGameObject ? instance.gameObject : (UnityEngine.Object)instance;
            if (Application.isPlaying)
            {
                UnityEngine.Object.Destroy(target);
            }
            else
            {
                UnityEngine.Object.DestroyImmediate(target);
            }
        }

        /// <summary>
        /// 外部销毁实例后通知缓存清空引用；不会创建或销毁任何对象。
        /// </summary>
        public static void NotifyDestroyed(T instance) => ResetIfCurrent(instance);

        /// <summary>
        /// 让实例跨场景常驻：播放模式下对象为顶级节点时执行 DontDestroyOnLoad。
        /// </summary>
        public static void MakePersistent(T instance)
        {
            if (Application.isPlaying && instance != null && instance.transform.parent == null)
            {
                UnityEngine.Object.DontDestroyOnLoad(instance.gameObject);
            }
        }

        /// <summary>
        /// 当前是否已经持有单例实例（不会触发查找或创建）
        /// </summary>
        public static bool HasInstance => _instance != null;

        /// <summary>
        /// 尝试获取已经持有的单例实例（不会触发查找或创建）
        /// </summary>
        public static bool TryGetInstance(out T instance)
        {
            instance = _instance;
            return instance != null;
        }

        /// <summary>
        /// 释放当前T类型的单例实例
        /// </summary>
        /// <param name="isDestroy">是否同时销毁实例所在的GameObject</param>
        public static void Dispose(bool isDestroy = true)
        {
            var instance = _instance;
            _instance = null;
            if (!isDestroy || instance == null)
            {
                return;
            }

            //Unity 的 Destroy 到帧末才实际生效，同帧内 Instance 重新创建时需要跳过该对象，
            //否则会把一个正在销毁的实例重新认领回来。
            ReleaseHost(instance);
        }

        /// <summary>
        /// 传入实例是否为当前缓存实例
        /// </summary>
        internal static bool IsCurrent(T instance)
        {
            return ReferenceEquals(_instance, instance);
        }

        /// <summary>
        /// 当传入实例就是当前缓存实例时清空缓存（供 MonoSingleton 生命周期回调使用）
        /// </summary>
        internal static void ResetIfCurrent(T instance)
        {
            if (ReferenceEquals(_instance, instance))
            {
                _instance = null;
            }
        }

        /// <summary>清空新 Play 会话的实例缓存，仅移除已销毁的排除项，保留仍存活的已处置宿主。</summary>
        private static void ResetSessionState()
        {
            _instance = null;
            // 模块可能先于本回调清理宿主；仍存活的退休对象不能在新会话中被重新认领。
            _disposedInstances.RemoveWhere(instance => instance == null);
        }
    }
}
