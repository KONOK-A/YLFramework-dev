using System;
using System.Collections;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Scripting;
using YLFramework.Core;
using YLFramework.Integrations.HybridCLR;
using YLFramework.Resource;

namespace YLFramework.Samples.HybridCLR
{
    /// <summary>宿主与热更入口共用的样例通信状态，宿主无需静态引用热更类型。</summary>
    [Preserve]
    public static class HybridCLRExampleState
    {
        /// <summary>本次运行由宿主明确传入的 Resource 项目身份。</summary>
        public static string ProjectId
        {
            get; set;
        }
        /// <summary>热更 Module 从 Core 配置和 Support 版本信息组合的同步结果。</summary>
        public static string Message
        {
            get; set;
        }
        /// <summary>热更 Module 发起的 Support 异步任务，由宿主等待并读取。</summary>
        public static Task<string> Async
        {
            get; set;
        }
    }

    /// <summary>显式、应用持有的样例入口。没有自动 Awake/Start，也没有 AOT 到热更类型的静态引用。</summary>
    public sealed class HybridCLRExample
    {
        /// <summary>热更入口程序集的精确简单名称。</summary>
        public const string EntryAssembly = "YLFramework.HybridCLR.Sample.Entry";
        /// <summary>入口程序集内待解析的 Module 完整类型名，仅作为字符串保存。</summary>
        public const string ModuleType = "YLFramework.Samples.HybridCLR.HotUpdate.ExampleModule";
        /// <summary>本样例创建并负责关闭的资源启动入口，同时绑定 Core provider。</summary>
        public ResourceStartup Startup
        {
            get; private set;
        }
        /// <summary>借用本样例资源服务的代码加载器，须先关闭其 Module 再释放。</summary>
        public HybridCLRLoader Loader
        {
            get; private set;
        }
        /// <summary>本次代码及 Module 启动请求，保留错误、模式及重启要求供调用方检查。</summary>
        public HybridCLRModuleRequest ModuleRequest
        {
            get; private set;
        }
        /// <summary>运行状态或组合验证结果，包含模拟标记、异步输出及热更 Prefab 标记。</summary>
        public string Result { get; private set; } = "Not started";
        /// <summary>实例化后通过热更组件反射取得的标记，不能在 DLL 加载前读取。</summary>
        public string PrefabMarker
        {
            get; private set;
        }
        private InstanceLease _instance;
        private bool _isStarting;

        /// <summary>显式启动资源和热更 Module，等待异步示例后实例化并检查热更脚本 Prefab。</summary>
        /// <param name="configuration">应用提供的资源配置，须包含所需代码和样例资源。</param>
        /// <param name="projectId">本次代码及样例资源所在的项目身份。</param>
        /// <param name="compatibilityId">当前原生基座的内容兼容身份。</param>
        /// <returns>由应用启动并等待的协程枚举器，不自动在 Awake 或 Start 执行。</returns>
        /// <remarks>拒绝与本实例尚未关闭的职责重叠；发生异常后调用方仍负责关闭已创建的入口。</remarks>
        public IEnumerator Run(ResourceConfiguration configuration, string projectId, string compatibilityId)
        {
            if (_isStarting || Startup?.Resources != null || Loader != null)
            {
                throw new InvalidOperationException("Close this sample's prior responsibilities first.");
            }

            _isStarting = true;
            Result = "Starting";
            try
            {
                HybridCLRExampleState.ProjectId = projectId;
                HybridCLRExampleState.Message = null;
                HybridCLRExampleState.Async = null;
                Startup = new ResourceStartup(configuration, bindCore: true);
                using var initialized = Startup.StartAsync();
                yield return initialized;
                initialized.TakeResult();
                Loader = new HybridCLRLoader(Startup.Resources, compatibilityId);
                ModuleRequest = Loader.StartModuleAsync(new ResourceAddress(projectId, HybridCLRManifest.ManifestPath), EntryAssembly, ModuleType);
                yield return ModuleRequest;
                if (ModuleRequest.Exception != null)
                {
                    throw ModuleRequest.Exception;
                }

                var task = HybridCLRExampleState.Async ?? throw new InvalidOperationException("The hot Module did not start its async sample.");
                while (!task.IsCompleted)
                {
                    yield return null;
                }

                var asyncResult = task.GetAwaiter().GetResult();
                using var prefab = Startup.Resources.InstantiateAsync(new ResourceAddress(projectId, "sample/hot-view"));
                yield return prefab;
                _instance = prefab.TakeResult();
                var behaviour = _instance.GameObject.GetComponents<Component>().SingleOrDefault(c => c && c.GetType().FullName == "YLFramework.Samples.HybridCLR.HotUpdate.ExampleBehaviour");
                if (!behaviour)
                {
                    throw new InvalidOperationException("The instantiated prefab did not resolve its loaded hot-update script.");
                }

                PrefabMarker = (string)behaviour.GetType().GetProperty("Marker").GetValue(behaviour);
                Result = (ModuleRequest.Code.IsSimulation ? "Simulation: " : "Native: ") + HybridCLRExampleState.Message + "; async=" + asyncResult + "; prefab=" + PrefabMarker;
            }
            finally
            {
                _isStarting = false;
            }
        }

        /// <summary>启动结束后关闭运行中的 Module、释放 Prefab 实例及加载器屏障，保留资源服务。</summary>
        /// <remarks>
        /// 不取消启动、不卸载 DLL；实例的延迟销毁仍须排空，运行另一代码版本须重启进程。
        /// </remarks>
        public void CloseCode()
        {
            if (_isStarting || ModuleRequest != null && !ModuleRequest.IsDone)
            {
                throw new InvalidOperationException("Module startup cannot be canceled; wait for its terminal result.");
            }

            if (Startup == null)
            {
                throw new InvalidOperationException("The sample has not started.");
            }

            if (ModuleRequest?.Module != null && ModuleRequest.Module.State == ModuleState.Running)
            {
                ModuleRequest.Module.Close();
            }

            _instance?.Dispose();
            _instance = null;
            Loader?.Dispose();
            Loader = null;
        }

        /// <summary>先关闭样例代码职责，再请求停止本样例拥有的 ResourceStartup。</summary>
        /// <returns>需由应用等待并检查终态的资源关闭请求。</returns>
        public ResourceRequest<bool> CloseAsync()
        {
            CloseCode();
            return Startup.StopAsync();
        }
    }
}
