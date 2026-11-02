using System;
using UnityEngine;
using YLFramework.Singletons;

namespace YLFramework.Actions
{
    /// <summary>当前场景的动作宿主，由 StartCurrentScene 等场景范围入口按需创建，随所在场景卸载。</summary>
    public class ActionKitCurrentScene : MonoSingletonHost<ActionKitCurrentScene>
    {
        /// <summary>当前缓存的场景宿主；保留原公开字段，调用方应通过 SceneComponent 取得实例。</summary>
        public static ActionKitCurrentScene CachedSceneComponent = null;
        /// <summary>自建宿主不设 DontDestroyOnLoad，随所属场景卸载一起销毁并取消其动作。</summary>
        protected override bool PersistentHost => false;

        /// <summary>取得可用场景宿主，不存在时在当前活动场景中创建；关停期间不可获取。</summary>
        /// <exception cref="System.InvalidOperationException">模块正在关停时抛出。</exception>
        public static ActionKitCurrentScene SceneComponent
        {
            get
            {
                if (ActionKit.IsShuttingDown)
                {
                    throw new InvalidOperationException("ActionKit is shutting down.");
                }

                if (!CachedSceneComponent || CachedSceneComponent.IsHostReleased)
                {
                    CachedSceneComponent = GetInstance(true);
                }
                return CachedSceneComponent;
            }
        }

        /// <summary>执行基类宿主登记，并为内部宿主组件设置层级隐藏标记。</summary>
        protected override void Awake()
        {
            base.Awake();
            hideFlags = HideFlags.HideInHierarchy;
        }

        /// <summary>执行宿主释放，并仅在场景缓存仍指向自身时清空缓存。</summary>
        protected override void OnDestroy()
        {
            try
            {
                base.OnDestroy();
            }
            finally
            {
                if (CachedSceneComponent == this)
                {
                    CachedSceneComponent = null;
                }
            }
        }

        // Shutdown 专用：只释放模块自建的宿主（借用场景组件时保留组件本身），再清空单例与静态缓存。
        internal static void ReleaseOwnedInstances()
        {
            foreach (var instance in FindObjectsByType<ActionKitCurrentScene>(FindObjectsInactive.Include, FindObjectsSortMode.InstanceID))
            {
                if (instance.OwnsHostGameObject)
                {
                    instance.ReleaseHost();
                }
            }
            ResetHostReference();
            CachedSceneComponent = null;
        }
    }
}
