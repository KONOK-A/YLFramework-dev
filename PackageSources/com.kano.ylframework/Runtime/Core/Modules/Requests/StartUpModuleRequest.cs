using System;
using System.Collections;
using System.Collections.Generic;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.SceneManagement;

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
    /// ModuleManager创建的非池化启动请求；不支持Abort取消启动协程。
    /// </summary>
    public sealed class StartUpModuleRequest : YLFramework.Async.CustomAsyncOperation<StartUpModuleRequest>
    {

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

        /// <summary>记录模块初态及旧默认上下文，供启动失败时恢复，不在构造时启动或注册模块。</summary>
        internal StartUpModuleRequest(Module module, Module previousCurrentModule = null)
        {
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
            if (IsDone)
            {
                return;
            }
            // 先提交生命周期与移除进行中记录，通知异常不能反过来改写启动结果。
            Exception = error;
            ModuleManager.CompleteStartup(this, error);
            string message = error?.ToString();
            if (error?.Data["YLFramework.Core.CleanupException"] is Exception cleanupError)
            {
                message += "\nStartup cleanup errors: " + cleanupError;
            }

            try
            {
                Complete(message);
            }
            catch (Exception notificationError)
            {
                Debug.LogException(notificationError);
            }
        }

        /// <summary>逐步推进启动迭代器，收集原始失败和迭代器释放错误后统一结束请求。</summary>
        internal IEnumerator StartUp()
        {
            var routine = RunStartup();
            Exception failure = null;
            try
            {
                while (true)
                {
                    bool hasNext;
                    try
                    {
                        hasNext = ModuleManager.MoveStartup(Module, routine);
                    }
                    catch (Exception error)
                    {
                        failure = error;
                        break;
                    }
                    if (!hasNext)
                    {
                        break;
                    }

                    yield return routine.Current;
                }
            }
            finally
            {
                try
                {
                    (routine as IDisposable)?.Dispose();
                }
                catch (Exception cleanupError)
                {
                    if (failure == null)
                    {
                        failure = cleanupError;
                    }
                    else
                    {
                        AttachCleanupError(failure, cleanupError);
                    }
                }
            }
            Finish(failure);
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
            var module = Module;
            Progress = 0;
            string projectName = module.ProjectName;
            var provider = CoreResourceAccess.Provider;
            if (string.IsNullOrWhiteSpace(projectName))
            {
                throw new InvalidOperationException($"Resource project name is required: {module.ModuleName}");
            }

            Step = StartingModuleSteps.PreloadConfigTables;

#pragma warning disable CS0618 // 类型或成员已过时
            var tables = module.PreloadConfigTables;
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

                        yield return request;
                        if (request.Status != CoreResourceStatus.Succeeded)
                        {
                            throw new InvalidOperationException($"Config table load failed: {projectName}/{assetName}", request.Error);
                        }

                        json = request.TakeResult();
                        if (string.IsNullOrWhiteSpace(json))
                        {
                            throw new InvalidOperationException($"Config table asset or JSON text is empty: {projectName}/{assetName}");
                        }
                    }

                    // 文本请求已释放；之后的 JSON 构造及 Model 初始化不再占用本次文本请求。
                    var models = JsonConvert.DeserializeObject(json, table.Value,
                        new JsonSerializerSettings { CheckAdditionalContent = true }) as Model[];
                    if (models == null)
                    {
                        throw new InvalidOperationException($"Config table JSON must contain a Model array: {projectName}/{assetName}");
                    }

                    foreach (var model in models)
                    {
                        module.AddModel(model);
                    }

                    currentCount++;
                    Progress = currentCount / allCount * 0.5f; // 更新进度
                }
            }
#pragma warning restore CS0618 // 类型或成员已过时


            Step = StartingModuleSteps.LoadScene;

            string sceneName = module.DefaultStartUpScene;
            if (!string.IsNullOrEmpty(sceneName))
            {
                using var operation = CoreResourceAccess.RequireProvider(provider).LoadSceneAsync(projectName, sceneName, LoadSceneMode.Single);
                if (operation == null)
                {
                    throw new InvalidOperationException($"Default scene request is null: {projectName}/{sceneName}");
                }

                while (operation.Status == CoreResourceStatus.Pending)
                {
                    yield return null;
                    Progress = 0.5f + operation.Progress * 0.5f;
                }
                if (operation.Status != CoreResourceStatus.Succeeded)
                {
                    throw new InvalidOperationException($"Default scene load failed: {projectName}/{sceneName}", operation.Error);
                }

                var loadedScene = operation.TakeResult().Scene;
                if (!loadedScene.IsValid() || !loadedScene.isLoaded)
                {
                    throw new InvalidOperationException($"Default scene result is not loaded: {projectName}/{sceneName}");
                }

                // 纳管真实加载结果的场景实例；模块关闭只结束这些表现会话，不负责卸载场景。
                module.AttachViews(loadedScene);
            }

            module.OnStart();
        }



    }

}
