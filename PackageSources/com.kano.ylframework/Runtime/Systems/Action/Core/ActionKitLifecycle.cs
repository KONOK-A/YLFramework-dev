using System;
using System.Collections.Generic;
using UnityEngine;

namespace YLFramework.Actions
{
    public partial class ActionKit
    {
        /// <summary>当前模块实例，由 Initialize 创建、Shutdown 释放。</summary>
        protected static ActionKit Architecture;
        /// <summary>每个新模块实例完成 Init 后执行的补充配置回调；回调中不能调用 Shutdown。</summary>
        public static Action<ActionKit> OnRegisterPatch = _ => { };
        /// <summary>本轮模块初始化及补充配置是否已成功完成。</summary>
        public static bool IsInitialized
        {
            get; private set;
        }
        // 关停标记：置位后拒绝新的初始化、启动和获取宿主，后续 Execute 不再进入动作钩子。
        internal static bool IsShuttingDown
        {
            get; private set;
        }
        // 在业务回调栈内请求的关停：等待最外层执行退出后再做实际释放。
        private static bool _isShutdownPending;
        // Initialize 执行期间为 true，用于阻止在 OnRegisterPatch 内调用 Shutdown。
        private static bool _isInitializing;

        // 关闭域重载时静态状态会跨 Play 会话保留：启动注册阶段先 Shutdown，清理上一轮的宿主、队列与模块引用。
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetModuleSession() => Shutdown();

        /// <summary>取得模块实例，尚未初始化时会先调用 Initialize。</summary>
        /// <exception cref="System.InvalidOperationException">模块正在关停时抛出。</exception>
        public static ActionKit Interface
        {
            get
            {
                Initialize();
                return Architecture;
            }
        }

        /// <summary>在主线程初始化模块；已存在实例时不重复初始化。</summary>
        /// <remarks>初始化失败时会清除本轮实例并传播异常。</remarks>
        /// <exception cref="System.InvalidOperationException">模块正在关停时不能重新初始化。</exception>
        public static void Initialize()
        {
            if (IsShuttingDown)
            {
                throw new InvalidOperationException("ActionKit is shutting down.");
            }

            if (Architecture != null)
            {
                return;
            }

            Architecture = new ActionKit();
            _isInitializing = true;
            try
            {
                Architecture.Init();
                OnRegisterPatch?.Invoke(Architecture);
                IsInitialized = true;
                // 不创建宿主；连接补丁期间已取得的宿主，失败时由桥接回滚本轮 token。
                if (ActionGlobalHost.TryGetInstance(out var host))
                {
                    host.RefreshUpdateBridge();
                }
            }
            catch
            {
                Architecture = null;
                IsInitialized = false;
                throw;
            }
            finally
            {
                _isInitializing = false;
            }
        }

        /// <summary>保留原初始化入口，行为与 Initialize 相同。</summary>
        public static void InitArchitecture() => Initialize();

        /// <summary>模块实例的初始化钩子，在 OnRegisterPatch 之前调用。</summary>
        protected virtual void Init()
        {
        }

        /// <summary>模块实例的清理钩子，在宿主和模块资源释放前调用。</summary>
        protected virtual void OnDeinit()
        {
        }

        /// <summary>仅当当前对象是模块实例时请求 Shutdown。</summary>
        public void Deinit()
        {
            if (ReferenceEquals(Architecture, this))
            {
                Shutdown();
            }
        }

        /// <summary>停止模块动作，解除模块订阅并释放自有宿主和资源，之后允许重新初始化。</summary>
        /// <remarks>
        /// 执行器回调中调用时先关闭后续推进，待当前执行栈退出后再完成清理，不中断当前方法内尚未返回的业务代码。
        /// 未交给执行器的动作不在调度清理范围内；静态动作池、IdGenerator 和 OnRegisterPatch 不随关停重置。
        /// </remarks>
        /// <exception cref="System.InvalidOperationException">在初始化配置期间（Init 或 OnRegisterPatch 回调内）调用时抛出。</exception>
        /// <exception cref="System.AggregateException">清理步骤失败时会聚合抛出；从回调栈内请求的清理延后执行。</exception>
        public static void Shutdown()
        {
            if (IsShuttingDown)
            {
                return;
            }

            if (_isInitializing)
            {
                throw new InvalidOperationException("Cannot shut down ActionKit from its initialization patch.");
            }

            IsShuttingDown = true;
            if (MonoUpdateActionExecutor.ExecutionScopes > 0)
            {
                // 不在业务回调栈内回收宿主，避免当前栈访问刚复用的动作或控制器。
                _isShutdownPending = true;
                foreach (var executor in UnityEngine.Object.FindObjectsByType<MonoUpdateActionExecutor>(FindObjectsInactive.Include, FindObjectsSortMode.InstanceID))
                {
                    executor.StopAll();
                }

                return;
            }
            FinishShutdown();
        }

        // 模块级执行作用域归零后，完成此前从回调栈内请求的关停。
        internal static void CompletePendingShutdown()
        {
            if (!_isShutdownPending)
            {
                return;
            }

            _isShutdownPending = false;
            FinishShutdown();
        }

        /// <summary>实际释放模块及各宿主资源，最终复位关停状态，并汇总受保护清理步骤的异常。</summary>
        private static void FinishShutdown()
        {
            // 逐项尽力清理：被 Cleanup 包裹的步骤失败后仍继续后续步骤，最后聚合抛出；finally 保证模块状态复位。
            List<Exception> errors = null;
            void Cleanup(Action operation)
            {
                try
                {
                    operation();
                }
                catch (Exception exception)
                {
                    if (errors == null)
                    {
                        errors = new List<Exception>();
                    }

                    errors.Add(exception);
                }
            }

            try
            {
                // 先通知模块清理，再停止并销毁执行器，随后释放各宿主与延迟回收队列；不取消外部 Task。
                if (Architecture != null)
                {
                    Cleanup(Architecture.OnDeinit);
                }

                foreach (var executor in UnityEngine.Object.FindObjectsByType<MonoUpdateActionExecutor>(FindObjectsInactive.Include, FindObjectsSortMode.InstanceID))
                {
                    Cleanup(executor.StopAll);
                    executor.enabled = false;
                    DestroyModuleObject(executor);
                }
                Cleanup(ActionGlobalHost.ReleaseAll);
                ActionKitCurrentScene.ReleaseOwnedInstances();
                ScreenTransitionCanvas.ReleaseOwnedInstance();
                Cleanup(ActionQueue.Drain);
                ActionRecycleDriver.ReleaseAll();
            }
            finally
            {
                Architecture = null;
                IsInitialized = false;
                IsShuttingDown = false;
                _isShutdownPending = false;
            }
            if (errors != null)
            {
                throw new AggregateException("ActionKit shutdown cleanup failed.", errors);
            }
        }

        // 仅销毁仍存活的模块对象；播放中使用延迟销毁，编辑状态立即销毁。
        internal static void DestroyModuleObject(UnityEngine.Object target)
        {
            if (!target)
            {
                return;
            }

            if (Application.isPlaying)
            {
                UnityEngine.Object.Destroy(target);
            }
            else
            {
                UnityEngine.Object.DestroyImmediate(target);
            }
        }
    }
}
