using System;
using UnityEngine;
using UnityEngine.SceneManagement;
using YLFramework.Singletons;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace YLFramework.Core
{
    /// <summary>Core 资源请求的观察状态；进入终态不等于结果已被领取。</summary>
    public enum CoreResourceStatus
    {
        /// <summary>当前消费者仍在等待结果。</summary>
        Pending,
        /// <summary>结果已准备好，可由该请求单次交割。</summary>
        Succeeded,
        /// <summary>加载失败，原因由请求的 Error 提供。</summary>
        Failed,
        /// <summary>当前消费者已取消等待，不代表共享加载或场景副作用被撤销。</summary>
        Canceled,
        /// <summary>在途请求因后端会话失效而结束。</summary>
        Invalidated
    }
    /// <summary>Core 可识别的资源失败类别，不暴露具体后端的错误枚举。</summary>
    public enum CoreResourceError
    {
        /// <summary>资源地址未在指定项目中找到；View 搜索可继续尝试下一项目。</summary>
        Missing,
        /// <summary>操作仍在途，当前入口不能立即交付结果。</summary>
        InProgress,
        /// <summary>资源存在加载失败；View 搜索允许继续尝试下一项目。</summary>
        LoadFailed,
        /// <summary>项目、地址或调用参数不符合后端要求。</summary>
        InvalidInput,
        /// <summary>未绑定后端、后端会话不可用或请求能力不受支持。</summary>
        Unavailable
    }

    /// <summary>Core 消费失败分类；InnerException 保留实际后端 cause，不导入具体 Systems 类型。</summary>
    public sealed class CoreResourceException : Exception
    {
        /// <summary>供 Core 决定是否继续资源项目搜索的失败类别。</summary>
        public CoreResourceError Kind
        {
            get;
        }
        /// <summary>保留 Core 失败分类、上下文和可选的底层异常。</summary>
        /// <param name="kind">Core 消费方可识别的失败类别。</param>
        /// <param name="message">本次失败的说明。</param>
        /// <param name="cause">作为 InnerException 保留的后端原因；可为 null。</param>
        public CoreResourceException(CoreResourceError kind, string message, Exception cause = null) : base(message, cause) => Kind = kind;
    }

    /// <summary>消费方窄请求合同。yield 只观察；TakeResult 单次交割，Dispose 不释放已交割结果。</summary>
    /// <typeparam name="T">单次交割的结果类型；结果有独立释放责任时由领取方接管。</typeparam>
    /// <remarks>派生实现须保持终态稳定并逐个隔离完成通知异常；协议按 Unity 主线程使用，不保证线程安全。</remarks>
    public abstract class CoreResourceRequest<T> : CustomYieldInstruction, IDisposable
    {
        /// <summary>请求状态；结果被领取或请求被释放后，既有终态保持不变。</summary>
        public abstract CoreResourceStatus Status
        {
            get;
        }
        /// <summary>供展示的 0 到 1 进度；不能用它代替 Status 判断完成。</summary>
        public abstract float Progress
        {
            get;
        }
        /// <summary>失败、取消或失效的原因；尚无错误时为 null。</summary>
        public abstract CoreResourceException Error
        {
            get;
        }
        /// <summary>请求是否已释放；已成功但未领取的请求也可能因会话退出而被释放。</summary>
        public abstract bool IsDisposed
        {
            get;
        }
        /// <summary>仅在 Pending 时让协程继续等待，不领取结果或替调用方释放请求。</summary>
        public override bool keepWaiting => Status == CoreResourceStatus.Pending;
        /// <summary>终态通知；终态后订阅立即回放，已释放后不允许新增订阅。</summary>
        /// <remarks>订阅可能同步重入；各观察者异常分别报告，不改变结果状态或跳过其余观察者。</remarks>
        public abstract event Action<CoreResourceRequest<T>> Completed;
        /// <summary>单次领取成功结果，并将其独立所有权转交给调用方。</summary>
        /// <returns>该请求持有的成功结果。</returns>
        /// <exception cref="ObjectDisposedException">请求已释放。</exception>
        /// <exception cref="InvalidOperationException">请求尚未完成或结果已被领取。</exception>
        /// <exception cref="CoreResourceException">请求以失败、取消或失效状态结束。</exception>
        public abstract T TakeResult();
        /// <summary>取消本消费者的在途等待；不撤销其他消费者，终态后调用不改变原状态。</summary>
        public abstract void Cancel();
        /// <summary>结束本请求并释放未领取结果；不释放已交割给调用方的结果。</summary>
        public abstract void Dispose();
    }

    /// <summary>Core 借读 prefab 并持有自己的责任；Instantiate 不消费 prefab lease，实例另持独立责任。</summary>
    public abstract class CorePrefabLease : IDisposable
    {
        /// <summary>有效租约内借读的 Prefab 资产；不可把资产当作可归还的场景实例。</summary>
        public abstract GameObject Prefab
        {
            get;
        }
        /// <summary>当前租约是否仍允许访问资产或创建实例。</summary>
        public abstract bool IsValid
        {
            get;
        }
        /// <summary>取得独立的实例 checkout，不消费当前 Prefab 租约。</summary>
        /// <param name="parent">实例父节点；非空时以其所在场景为准。</param>
        /// <param name="scene">无父节点时使用的已加载目标场景。</param>
        /// <returns>由调用方负责 Return 或 Discard 的独立实例租约。</returns>
        public abstract CoreInstanceLease Instantiate(Transform parent, Scene scene);
        /// <summary>释放本租约的 Prefab 持有责任，不代替已借出实例的归还。</summary>
        public abstract void Dispose();
    }

    /// <summary>Core 实例 checkout；正常 Return、损坏 Discard。Dispose 等价 Return，不归还其他 checkout。</summary>
    public abstract class CoreInstanceLease : IDisposable
    {
        /// <summary>仅在本次 checkout 有效期间借读的场景实例。</summary>
        public abstract GameObject Instance
        {
            get;
        }
        /// <summary>本次 checkout、实例对象与所属资源会话是否仍有效。</summary>
        public abstract bool IsValid
        {
            get;
        }
        /// <summary>正常结束本次借用；由后端决定缓存或销毁，不归还后来取得的 checkout。</summary>
        public abstract void Return();
        /// <summary>将本次实例视为不可复用并交给后端清理，不放回可复用缓存。</summary>
        public abstract void Discard();
        /// <summary>按正常归还路径结束本次借用；损坏实例应显式使用 Discard。</summary>
        public void Dispose() => Return();
    }

    /// <summary>本次真实 Scene 与副作用观察值；不拥有 Scene 卸载权，不随 Module.Close 隐式卸载。</summary>
    public readonly struct CoreSceneResult
    {
        /// <summary>本次加载返回的真实 Scene，不按名称重新查找或隐式卸载。</summary>
        public Scene Scene
        {
            get;
        }
        /// <summary>本结果是否记录了场景加载副作用；不是卸载或回滚许可。</summary>
        public bool HasSceneSideEffects
        {
            get;
        }
        /// <summary>保存场景身份与本次操作的副作用观察值。</summary>
        /// <param name="scene">加载后实际返回的场景。</param>
        /// <param name="hasSceneSideEffects">是否已观察到场景加载副作用。</param>
        public CoreSceneResult(Scene scene, bool hasSceneSideEffects)
        {
            Scene = scene;
            HasSceneSideEffects = hasSceneSideEffects;
        }
    }

    /// <summary>仅覆盖 Core 文本、prefab/实例和场景消费。命名操作必须显式绑定后端。</summary>
    /// <remarks>项目标识与地址交由后端解析；按 Unity 主线程协议使用，接口不提供默认实现或线程安全保证。</remarks>
    public interface ICoreResourceProvider
    {
        /// <summary>读取文本值，不向 Core 交付 TextAsset 或其资产租约。</summary>
        /// <param name="projectId">正式资源项目标识。</param>
        /// <param name="address">该项目内的文本资源地址。</param>
        /// <returns>调用方须领取结果并释放的独立请求；文本值本身无资源归还责任。</returns>
        CoreResourceRequest<string> ReadTextAsync(string projectId, string address);
        /// <summary>同步取得 Prefab 租约；不能立即交付时由后端明确报错，不在此阻塞等待。</summary>
        /// <param name="projectId">正式资源项目标识。</param>
        /// <param name="address">该项目内的 Prefab 地址。</param>
        /// <returns>由调用方独立释放的 Prefab 租约。</returns>
        CorePrefabLease AcquirePrefab(string projectId, string address);
        /// <summary>异步取得 Prefab 租约；请求释放与结果租约释放是两项独立责任。</summary>
        /// <param name="projectId">正式资源项目标识。</param>
        /// <param name="address">该项目内的 Prefab 地址。</param>
        /// <returns>单次交割 Prefab 租约的请求；未领取的结果随请求释放。</returns>
        CoreResourceRequest<CorePrefabLease> AcquirePrefabAsync(string projectId, string address);
        /// <summary>提交场景加载意图；取消等待或释放请求不承诺回滚原生场景副作用。</summary>
        /// <param name="projectId">正式资源项目标识。</param>
        /// <param name="address">该项目内的场景地址。</param>
        /// <param name="mode">Single 或 Additive 加载方式。</param>
        /// <returns>交付场景观察结果的请求；结果不包含场景卸载所有权。</returns>
        CoreResourceRequest<CoreSceneResult> LoadSceneAsync(string projectId, string address, LoadSceneMode mode);
    }

    /// <summary>显式主线程绑定槽，无默认后端、隐式回退或加载表。</summary>
    public static class CoreResourceAccess
    {
        private static ICoreResourceProvider _provider;
        /// <summary>当前执行会话可用的显式绑定；非 Play/Player 执行期或尚未绑定时为 null。</summary>
        public static ICoreResourceProvider Provider => SingletonSessionState.CanRunExecution ? _provider : null;

        /// <summary>确认调用方捕获的后端存在；不重新查询绑定，避免在途消费静默换后端。</summary>
        internal static ICoreResourceProvider RequireProvider(ICoreResourceProvider value) => value ??
            throw new CoreResourceException(CoreResourceError.Unavailable, "Named Core resources require an explicitly bound resource provider.");

        /// <summary>仅允许 Missing/LoadFailed 继续尝试下一资源项目，不把缺后端或非法输入当作可回退错误。</summary>
        internal static bool CanFallback(Exception error) => error is CoreResourceException resource &&
            (resource.Kind == CoreResourceError.Missing || resource.Kind == CoreResourceError.LoadFailed);

        /// <summary>登记静态绑定的会话重置与退出清理；这些订阅与该静态类型同寿命。</summary>
        static CoreResourceAccess()
        {
            SingletonSessionState.RegisterSessionReset(Reset);
            Application.quitting += Reset;
#if UNITY_EDITOR
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
#endif
        }

        /// <summary>在有效执行会话中替换显式后端，不释放旧后端或接管其在途请求。</summary>
        /// <param name="value">由装配方负责生命周期的非 null 后端。</param>
        /// <exception cref="InvalidOperationException">当前不在有效 Play/Player 执行会话。</exception>
        /// <exception cref="ArgumentNullException">value 为 null。</exception>
        public static void Bind(ICoreResourceProvider value)
        {
            if (!SingletonSessionState.CanRunExecution)
            {
                throw new InvalidOperationException("Core resource binding requires an active Play/Player session.");
            }

            _provider = value ?? throw new ArgumentNullException(nameof(value));
        }

        /// <summary>旧 owner 解绑不覆盖后来显式绑定的 provider。</summary>
        /// <param name="value">尝试解除绑定的后端实例，按引用身份匹配。</param>
        /// <returns>当前绑定恰为该实例且已清空时为 true；null 或非当前后端返回 false。</returns>
        /// <remarks>只清空绑定槽，不释放后端、请求、资产租约或场景。</remarks>
        public static bool Unbind(ICoreResourceProvider value)
        {
            if (value == null || !ReferenceEquals(_provider, value))
            {
                return false;
            }

            _provider = null;
            return true;
        }

        /// <summary>清空当前会话的绑定引用；后端资源收尾仍由后端所有者执行。</summary>
        private static void Reset() => _provider = null;
#if UNITY_EDITOR
        /// <summary>在编辑与运行边界丢弃旧绑定，避免禁用 Domain Reload 时跨会话沿用。</summary>
        private static void OnPlayModeChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingPlayMode || state == PlayModeStateChange.ExitingEditMode ||
                state == PlayModeStateChange.EnteredEditMode)
            {
                Reset();
            }
        }
#endif
    }
}
