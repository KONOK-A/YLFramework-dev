using System;
using System.Collections;
using System.Collections.Generic;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.SceneManagement;
using CoroutineStarter = YLFramework.Async.CoroutineStarter;

namespace YLFramework.Core
{

    /// <summary>启动请求公开的资源处理阶段，不表示 Module 已进入 Running。</summary>
    public enum StartingModuleSteps
    {
        /// <summary>
        /// 加载配表
        /// </summary>
        PreloadConfigTables,

        /// <summary>
        /// 加载场景
        /// </summary>
        LoadScene
    }


    /// <summary>
    /// ModuleManager创建的非池化启动请求；会话或宿主失效时内部回滚，不支持Abort用户取消。
    /// </summary>
    public sealed class StartUpModuleRequest : YLFramework.Async.CustomAsyncOperation<StartUpModuleRequest>
    {
        private ModuleManager _owner;
        private IEnumerator _routine;
        private Coroutine _coroutine;
        private bool _isExecuting;
        private bool _isFinishing;
        /// <summary>本次生产者的实际宿主身份；完成后释放引用，不跟随静态新宿主。</summary>
        internal CoroutineStarter Host
        {
            get; private set;
        }

        /// <summary>当前资源处理阶段；业务 OnStart 仍在场景阶段之后执行。</summary>
        public StartingModuleSteps Step
        {
            get; private set;
        }

        /// <summary>本请求启动的模块实例；请求完成后仍保留此引用供调用方检查。</summary>
        public Module Module
        {
            get;
        }

        /// <summary>
        /// 首次导致启动失败的原始异常；额外清理错误附在Data["YLFramework.Core.CleanupException"]。
        /// 成功结果为null，失败完成通知前已结束本次Core逻辑所有权。
        /// </summary>
        public Exception Exception
        {
            get; private set;
        }

        internal Module PreviousCurrentModule
        {
            get; set;
        }
        internal ModuleState InitialState
        {
            get;
        }

        /// <summary>绑定原 manager、模块初态及旧上下文，供失败回滚；不在构造时启动或注册模块。</summary>
        internal StartUpModuleRequest(ModuleManager owner, Module module, Module previousCurrentModule = null)
        {
            _owner = owner;
            Module = module;
            InitialState = module.State;
            PreviousCurrentModule = previousCurrentModule;
        }

        /// <summary>
        /// 启动协程不支持安全取消；在任何完成状态修改前拒绝Abort。
        /// </summary>
        public override void Abort()
        {
            throw new NotSupportedException("Module startup does not support Abort.");
        }

        /// <summary>先提交模块生命周期与回滚结果，再完成请求；完成回调异常只记日志，不改写结果。</summary>
        internal void Finish(Exception error = null)
        {
            if (IsDone || _isFinishing)
            {
                return;
            }
            _isFinishing = true;
            RecordFailure(error);
            try
            {
                var coroutine = _coroutine;
                _coroutine = null;
                try
                {
                    CoroutineStarter.Stop(coroutine);
                }
                catch (Exception stopError)
                {
                    RecordFailure(stopError);
                }
                ReleaseRoutine();
                try
                {
                    _owner?.CompleteStartup(this, Exception);
                }
                catch (Exception cleanupError)
                {
                    RecordFailure(cleanupError);
                }
            }
            finally
            {
                if (ReferenceEquals(Module.PendingStartup, this))
                {
                    Module.PendingStartup = null;
                }
                // 完成请求不应把整个旧 manager 或执行句柄留给后续 Task 等待者。
                _owner = null;
                Host = null;
                PreviousCurrentModule = null;
                string message = null;
                try
                {
                    message = Exception?.ToString();
                    if (Exception?.Data["YLFramework.Core.CleanupException"] is Exception cleanupError)
                    {
                        message += "\nStartup cleanup errors: " + cleanupError;
                    }
                }
                catch (Exception formattingError)
                {
                    message = $"Module startup failed ({Exception.GetType().FullName}).";
                    RecordFailure(formattingError);
                    Debug.LogException(formattingError);
                }
                finally
                {
                    try
                    {
                        Complete(message);
                    }
                    catch (Exception notificationError)
                    {
                        Debug.LogException(notificationError);
                    }
                }
            }
        }

        /// <summary>原会话失效时只登记失败；启动或所属 MVC 事务仍在栈内时由其真实出口续接回滚。</summary>
        internal void Invalidate(Exception error)
        {
            if (IsDone || _isFinishing)
            {
                return;
            }
            RecordFailure(error);
            TryFinishInvalidated();
        }

        internal void TryFinishInvalidated()
        {
            if (!IsDone && !_isFinishing && !_isExecuting && Exception != null && !Module.HasObjectTransactions)
            {
                Finish();
            }
        }

        internal void AttachCoroutine(Coroutine coroutine)
        {
            if (IsDone || _isFinishing)
            {
                CoroutineStarter.Stop(coroutine);
            }
            else
            {
                _coroutine = coroutine;
            }
        }

        private void RecordFailure(Exception error)
        {
            if (error == null || ReferenceEquals(Exception, error))
            {
                return;
            }
            if (Exception == null)
            {
                Exception = error;
            }
            else
            {
                try
                {
                    AttachCleanupError(Exception, error);
                }
                catch (Exception attachmentError)
                {
                    Debug.LogException(new AggregateException("Startup cleanup errors could not be attached.", error, attachmentError));
                }
            }
        }

        private void ReleaseRoutine()
        {
            var routine = _routine;
            _routine = null;
            try
            {
                (routine as IDisposable)?.Dispose();
            }
            catch (Exception cleanupError)
            {
                RecordFailure(cleanupError);
            }
        }

        private void EnsureActive()
        {
            if (Exception != null)
            {
                throw Exception;
            }
            if (IsDone || _isFinishing)
            {
                throw new InvalidOperationException("Module startup has already ended.");
            }
        }

        /// <summary>逐步推进启动迭代器，收集原始失败和迭代器释放错误后统一结束请求。</summary>
        internal IEnumerator StartUp()
        {
            if (IsDone || _isFinishing)
            {
                yield break;
            }
            Host = CoroutineStarter.CurrentHost;
            _routine = RunStartup();
            try
            {
                while (!IsDone && !_isFinishing && Exception == null)
                {
                    bool hasNext = false;
                    _isExecuting = true;
                    try
                    {
                        hasNext = _owner.MoveStartup(Module, _routine);
                    }
                    catch (Exception error)
                    {
                        RecordFailure(error);
                    }
                    finally
                    {
                        _isExecuting = false;
                    }
                    if (!hasNext || IsDone || _isFinishing || Exception != null)
                    {
                        break;
                    }

                    yield return _routine.Current;
                }
            }
            finally
            {
                ReleaseRoutine();
            }
            _coroutine = null; // 正常展开的驱动即将退出，不在自身调用栈再次 StopCoroutine。
            Finish();
        }

        /// <summary>把后续清理错误附到原始异常的数据中，不替换最初的失败原因。</summary>
        internal static void AttachCleanupError(Exception error, Exception cleanupError)
        {
            const string Key = "YLFramework.Core.CleanupException";
            var previous = error.Data[Key] as Exception;
            error.Data[Key] = previous == null ? cleanupError :
                new AggregateException("Startup cleanup failed.", previous, cleanupError);
        }

        /// <summary>使用入口捕获的资源项目依次读取配表、加载默认场景并调用业务启动钩子。</summary>
        private IEnumerator RunStartup()
        {
            EnsureActive();
            var module = Module;
            Progress = 0;
            string projectName = module.ProjectName;
            EnsureActive();
            var provider = CoreResourceAccess.Provider;
            if (string.IsNullOrWhiteSpace(projectName))
            {
                throw new InvalidOperationException($"Resource project name is required: {module.ModuleName}");
            }

            Step = StartingModuleSteps.PreloadConfigTables;

            var tables = module.PreloadConfigTables;
            EnsureActive();
            if (tables != null && tables.Count != 0)
            {
                float allCount = tables.Count;
                float currentCount = 0;
                foreach (var table in tables)
                {
                    string assetName = table.Key;
                    if (string.IsNullOrWhiteSpace(assetName))
                    {
                        throw new InvalidOperationException("Config table asset name is required.");
                    }

                    if (table.Value == null || !table.Value.IsArray || table.Value.GetArrayRank() != 1 ||
                        !typeof(Model).IsAssignableFrom(table.Value.GetElementType()))
                    {
                        throw new InvalidOperationException($"Config table must specify a Model array type: {assetName}");
                    }

                    string json;
                    using (var request = CoreResourceAccess.RequireProvider(provider).ReadTextAsync(projectName, assetName))
                    {
                        if (request == null)
                        {
                            throw new InvalidOperationException($"Config table request is null: {projectName}/{assetName}");
                        }

                        EnsureActive();
                        yield return request;
                        EnsureActive();
                        if (request.Status != CoreResourceStatus.Succeeded)
                        {
                            throw new InvalidOperationException($"Config table load failed: {projectName}/{assetName}", request.Error);
                        }

                        json = request.TakeResult();
                        EnsureActive();
                        if (string.IsNullOrWhiteSpace(json))
                        {
                            throw new InvalidOperationException($"Config table asset or JSON text is empty: {projectName}/{assetName}");
                        }
                    }

                    EnsureActive();
                    // 文本请求已释放；之后的 JSON 构造及 Model 初始化不再占用本次文本请求。
                    var models = JsonConvert.DeserializeObject(json, table.Value,
                        new JsonSerializerSettings { CheckAdditionalContent = true }) as Model[];
                    EnsureActive();
                    if (models == null)
                    {
                        throw new InvalidOperationException($"Config table JSON must contain a Model array: {projectName}/{assetName}");
                    }

                    foreach (var model in models)
                    {
                        module.AddModel(model);
                        EnsureActive();
                    }

                    currentCount++;
                    Progress = currentCount / allCount * 0.5f; // 更新进度
                }
            }


            Step = StartingModuleSteps.LoadScene;

            string sceneName = module.DefaultStartUpScene;
            EnsureActive();
            if (!string.IsNullOrEmpty(sceneName))
            {
                using var operation = CoreResourceAccess.RequireProvider(provider).LoadSceneAsync(projectName, sceneName, LoadSceneMode.Single);
                if (operation == null)
                {
                    throw new InvalidOperationException($"Default scene request is null: {projectName}/{sceneName}");
                }
                EnsureActive();

                while (operation.Status == CoreResourceStatus.Pending)
                {
                    yield return null;
                    EnsureActive();
                    Progress = 0.5f + operation.Progress * 0.5f;
                }
                if (operation.Status != CoreResourceStatus.Succeeded)
                {
                    throw new InvalidOperationException($"Default scene load failed: {projectName}/{sceneName}", operation.Error);
                }

                var loadedScene = operation.TakeResult().Scene;
                EnsureActive();
                if (!loadedScene.IsValid() || !loadedScene.isLoaded)
                {
                    throw new InvalidOperationException($"Default scene result is not loaded: {projectName}/{sceneName}");
                }

                // 纳管真实加载结果的场景实例；模块关闭只结束这些表现会话，不负责卸载场景。
                module.AttachViews(loadedScene);
                EnsureActive();
            }

            EnsureActive(); // 场景 using 的 Dispose 也是外部回调边界，不能跨过已登记的失效。
            module.OnStart();
            EnsureActive();
        }



    }

}
