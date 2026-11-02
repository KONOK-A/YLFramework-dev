using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using YLFramework.Core;
using YLFramework.Foundation;
using Module = YLFramework.Core.Module;
using YLFramework.Async;

namespace YLFramework.Integrations.HybridCLR
{
    /// <summary>区分原生代码加载与两种仅用于 Editor 的执行路径。</summary>
    public enum HybridCLRLoadMode
    {
        /// <summary>通过原生 metadata 补充及程序集加载处理已校验代码包。</summary>
        Native,
        /// <summary>校验清单和代码字节后复用 Editor 已加载程序集，不调用 native。</summary>
        EditorSimulation,
        /// <summary>显式跳过代码产物校验，仅用 Editor 已加载程序集调试 Module。</summary>
        EditorFast
    }

    /// <summary>代码加载结果；持有程序集引用不表示拥有可卸载的代码实例。</summary>
    public sealed class HybridCLRLoadResult
    {
        /// <summary>本次入口对应的 Resource 项目身份。</summary>
        public string ProjectId
        {
            get; internal set;
        }
        /// <summary>已校验清单的代码包摘要；EditorFast 未读取清单时为空。</summary>
        public string CodePackageId
        {
            get; internal set;
        }
        /// <summary>本次实际使用的加载或调试路径，不代表其他路径已验证。</summary>
        public HybridCLRLoadMode Mode
        {
            get; internal set;
        }
        /// <summary>是否为任一 Editor 路径；保留对两种模拟模式的统一判断。</summary>
        public bool IsSimulation => Mode != HybridCLRLoadMode.Native;
        /// <summary>按精确简单名称索引的热更程序集，不包含 AOT metadata 程序集。</summary>
        public IReadOnlyDictionary<string, Assembly> Assemblies
        {
            get; internal set;
        }
    }

    /// <summary>可供协程等待的代码加载请求；终态保留原始异常，不支持取消。</summary>
    public sealed class HybridCLRLoadRequest : CustomAsyncOperation<HybridCLRLoadRequest>
    {
        private readonly List<Action<HybridCLRLoadRequest>> _continuations = new List<Action<HybridCLRLoadRequest>>();

        /// <summary>成功后的代码结果；失败时为空。</summary>
        public HybridCLRLoadResult Result
        {
            get; private set;
        }
        /// <summary>加载失败的原始异常；成功或尚未失败时为空。</summary>
        public Exception Exception
        {
            get; private set;
        }
        /// <summary>是否因进程代码身份冲突或不可逆加载失败而需要重启进程。</summary>
        public bool RequiresRestart
        {
            get; private set;
        }
        /// <summary>请求显式指定的 Resource 项目身份。</summary>
        public string ProjectId
        {
            get; internal set;
        }
        /// <summary>当前或失败所在阶段，终态可用于定位首因。</summary>
        public string Stage { get; internal set; } = "Manifest";
        /// <summary>当前处理或失败的程序集名称；无对应程序集时可为空。</summary>
        public string AssemblyName
        {
            get; internal set;
        }
        /// <summary>metadata native 调用失败的原始错误码；未发生该类失败时为空。</summary>
        public int? NativeErrorCode
        {
            get; internal set;
        }
        internal event Action<HybridCLRLoadRequest> ProgressChanged;

        // 单调更新进度并隔离观察者异常，避免通知失败中断加载。
        internal void ReportProgress(float value)
        {
            if (IsDone)
            {
                return;
            }

            if (!float.IsNaN(value))
            {
                Progress = Mathf.Max(Progress, Mathf.Clamp01(value));
            }

            var observers = ProgressChanged;
            if (observers == null)
            {
                return;
            }

            foreach (Action<HybridCLRLoadRequest> observer in observers.GetInvocationList())
            {
                try
                {
                    observer(this);
                }
                catch (Exception notificationError)
                {
                    Debug.LogException(notificationError);
                }
            }
        }

        // 只提交一次终态，并在公开通知之后继续内部 Module 启动流程。
        internal void Finish(HybridCLRLoadResult result, Exception exception = null, bool restart = false)
        {
            if (IsDone)
            {
                return;
            }

            Result = result;
            Exception = exception;
            RequiresRestart = restart;
            ProgressChanged = null;
            try
            {
                Complete(exception?.ToString());
            }
            catch (Exception notificationError)
            {
                Debug.LogException(notificationError);
            }
            finally
            {
                var pending = _continuations.ToArray();
                _continuations.Clear();
                foreach (var continuation in pending)
                {
                    try
                    {
                        continuation(this);
                    }
                    catch (Exception continuationError)
                    {
                        Debug.LogException(continuationError);
                    }
                }
            }
        }

        // 注册不受公开完成事件移除影响的内部续接，已完成时立即执行。
        internal void ContinueWith(Action<HybridCLRLoadRequest> continuation)
        {
            if (IsDone)
            {
                continuation(this);
            }
            else
            {
                _continuations.Add(continuation);
            }
        }

        // 移除已结束 Module 请求持有的加载续接。
        internal void RemoveContinuation(Action<HybridCLRLoadRequest> continuation) => _continuations.Remove(continuation);

        /// <summary>始终拒绝取消，且不改变请求状态；已发生的 native 加载无法撤销。</summary>
        /// <exception cref="NotSupportedException">任何调用均抛出。</exception>
        public override void Abort() => throw new NotSupportedException("HybridCLR code loading does not support Abort; native loads cannot be undone.");
    }

    /// <summary>串联代码加载与 Core Module 启动的协程请求，不支持中途取消。</summary>
    public sealed class HybridCLRModuleRequest : CustomAsyncOperation<HybridCLRModuleRequest>
    {
        internal Action Detach;
        internal Action<Exception> Invalidate;
        internal Coroutine ProgressRoutine;

        /// <summary>Core 启动请求创建的 Module；失败时可能为空，关闭责任仍由入口管理。</summary>
        public Module Module
        {
            get; internal set;
        }
        /// <summary>启动使用的代码结果；代码阶段尚未成功时可为空。</summary>
        public HybridCLRLoadResult Code
        {
            get; internal set;
        }
        /// <summary>代码加载或 Module 启动失败的原始异常。</summary>
        public Exception Exception
        {
            get; private set;
        }
        /// <summary>代码加载阶段是否要求重启进程；普通 Module 启动失败不等于代码可回滚。</summary>
        public bool RequiresRestart
        {
            get; private set;
        }
        /// <summary>本启动入口所属的 Resource 项目身份。</summary>
        public string ProjectId
        {
            get; internal set;
        }
        /// <summary>代码阶段或具体 Core 启动阶段；完成后保留终态阶段。</summary>
        public string Stage { get; internal set; } = "LoadCode";
        /// <summary>代码阶段当前程序集或 Module 的精确入口程序集名称。</summary>
        public string AssemblyName
        {
            get; internal set;
        }
        /// <summary>由代码请求转发的 metadata native 失败码，其他失败时可为空。</summary>
        public int? NativeErrorCode
        {
            get; internal set;
        }

        // 合并代码与 Core 阶段的进度，拒绝 NaN 和倒退值。
        internal void ReportProgress(float value)
        {
            if (!IsDone && !float.IsNaN(value))
            {
                Progress = Mathf.Max(Progress, Mathf.Clamp01(value));
            }
        }

        // 只完成一次启动请求，并保留不被通知异常替换的原始结果。
        internal void Finish(Exception exception = null, bool restart = false)
        {
            if (IsDone)
            {
                return;
            }

            Exception = exception;
            RequiresRestart = restart;
            try
            {
                Complete(exception?.ToString());
            }
            catch (Exception notificationError)
            {
                Debug.LogException(notificationError);
            }
        }

        /// <summary>始终拒绝取消，不改变代码加载或 Core 启动状态。</summary>
        /// <exception cref="NotSupportedException">任何调用均抛出。</exception>
        public override void Abort() => throw new NotSupportedException("HybridCLR Module startup does not support Abort.");
    }
}
