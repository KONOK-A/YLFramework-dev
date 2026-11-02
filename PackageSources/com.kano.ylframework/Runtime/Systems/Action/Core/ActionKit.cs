using System;
using System.Collections;
using System.Threading.Tasks;
using YLFramework.Events;

namespace YLFramework.Actions
{
    /// <summary>
    /// 动作创建、模块生命周期和全局 Unity 消息的入口。
    /// </summary>
    /// <remarks>
    /// 创建动作不会自动执行，需调用 Start、StartCurrentScene 或 StartGlobal。
    /// 标准执行器在动作完成或取消后负责清理回收，不应继续使用旧动作或控制器引用。
    /// 仅创建或手动 Execute 的动作不由执行器接管，调用方仍需负责 Deinit。
    /// 全局宿主及消息仅供运行时主线程使用。
    /// </remarks>
    public partial class ActionKit
    {
        /// <summary>动作分配标识的计数器；保留此公开字段，调用方不应主动重置。</summary>
        public static ulong IdGenerator = 0;

        /// <summary>创建等待指定秒数后调用回调的动作。</summary>
        /// <param name="seconds">等待时长，使用执行器传入的时间增量累计。</param>
        /// <param name="callback">等待结束时调用的回调，取消时不调用。</param>
        /// <returns>未启动的延迟动作。</returns>
        public static IAction Delay(float seconds, Action callback)
        {
            return global::YLFramework.Actions.Delay.Allocate(seconds, callback);
        }

        /// <summary>创建按加入顺序执行子动作的组合；即时完成的子动作可在同次推进中连续执行。</summary>
        /// <returns>可通过 Append 或组合扩展添加子动作的序列。</returns>
        public static ISequence Sequence()
        {
            return global::YLFramework.Actions.Sequence.Allocate();
        }

        /// <summary>创建按 Unity 帧数等待的动作，使用 Time.frameCount 计时，与执行器更新次数和 dt 无关。</summary>
        /// <param name="frameCount">启动帧之后需要经过的帧数；非正数在启动后的首次执行完成，不按秒数换算。</param>
        /// <param name="onDelayFinish">等待结束时的回调，取消时不调用。</param>
        /// <returns>未启动的帧延迟动作。</returns>
        public static IAction DelayFrame(int frameCount, Action onDelayFinish)
        {
            return global::YLFramework.Actions.DelayFrame.Allocate(frameCount, onDelayFinish);
        }

        /// <summary>创建在启动帧之后的首次执行中调用回调的帧延迟动作。</summary>
        /// <param name="onNextFrame">跨过一个 Unity 帧后、下一次执行动作时调用的回调。</param>
        public static IAction NextFrame(Action onNextFrame)
        {
            return global::YLFramework.Actions.DelayFrame.Allocate(1, onNextFrame);
        }

        /// <summary>创建在指定时长内从 a 线性插值到 b 的动作。</summary>
        /// <param name="a">起始值。</param>
        /// <param name="b">结束值。</param>
        /// <param name="duration">插值时长，单位为秒。</param>
        /// <param name="onLerp">接收启动和更新时的插值结果。</param>
        /// <param name="onLerpFinish">正常插值结束时的回调。</param>
        public static IAction Lerp(float a, float b, float duration, Action<float> onLerp, Action onLerpFinish = null)
        {
            return global::YLFramework.Actions.Lerp.Allocate(a, b, duration, onLerp, onLerpFinish);
        }

        /// <summary>创建在 OnStart 中调用一次回调并立即完成的动作。</summary>
        /// <param name="callback">启动时调用的回调。</param>
        public static IAction Callback(Action callback)
        {
            return CallbackAction.Allocate(callback);
        }

        /// <summary>创建在启动后的更新中检查条件、满足后完成的动作。</summary>
        /// <param name="condition">每次更新检查的条件；首次 OnStart 不检查。</param>
        /// <param name="onCondition">条件满足时调用的回调。</param>
        public static IAction Condition(Func<bool> condition, Action onCondition = null)
        {
            return ConditionAction.Allocate(condition, onCondition);
        }

        /// <summary>创建重复执行子序列的组合。</summary>
        /// <param name="repeatCount">正数表示总轮数，-1 和 0 表示无限重复；其他负数不推进且不自动完成。</param>
        /// <returns>可添加子动作的重复组合。</returns>
        public static IRepeat Repeat(int repeatCount = -1)
        {
            return global::YLFramework.Actions.Repeat.Allocate(repeatCount);
        }

        /// <summary>创建在同次推进中依次更新所有子动作的组合，全部完成后结束。</summary>
        /// <returns>可添加子动作的并行组合；空组合保留现有不完成行为。</returns>
        public static IParallel Parallel()
        {
            return global::YLFramework.Actions.Parallel.Allocate();
        }

        /// <summary>创建自定义动作，并立即配置它的生命周期回调。</summary>
        /// <param name="customSetting">配置回调，不代表动作已经启动。</param>
        /// <returns>配置完成、尚未启动的动作。</returns>
        public static IAction Custom(Action<ICustomApi<object>> customSetting)
        {
            var custom = global::YLFramework.Actions.Custom.Allocate();
            customSetting(custom);
            return custom;
        }

        /// <summary>创建携带指定类型数据的自定义动作，并立即配置生命周期回调。</summary>
        /// <typeparam name="TData">自定义动作持有的数据类型。</typeparam>
        /// <param name="customSetting">设置数据和生命周期回调的配置函数。</param>
        /// <returns>配置完成、尚未启动的动作。</returns>
        public static IAction Custom<TData>(Action<ICustomApi<TData>> customSetting)
        {
            var custom = global::YLFramework.Actions.Custom<TData>.Allocate();
            customSetting(custom);
            return custom;
        }

        /// <summary>创建等待协程结束的动作；协程由模块全局宿主运行。</summary>
        /// <param name="coroutineGetter">每次动作启动时创建协程枚举器的函数。</param>
        /// <remarks>仅在运行时主线程使用；取消动作会停止它持有的协程。</remarks>
        public static IAction Coroutine(Func<IEnumerator> coroutineGetter)
        {
            return CoroutineAction.Allocate(coroutineGetter);
        }

        /// <summary>创建等待 Task 成功完成的动作。</summary>
        /// <param name="taskGetter">每次动作启动时取得 Task 的函数。</param>
        /// <remarks>取消动作不取消外部 Task；任务取消或失败会清理动作而不正常完成，失败时记录异常。</remarks>
        public static IAction Task(Func<Task> taskGetter)
        {
            return TaskAction.Allocate(taskGetter);
        }

        /// <summary>全局宿主的 Update 通知，订阅后应按调用方生命周期解绑。</summary>
        /// <remarks>自动消息复用 Foundation 公共相位，仅有效启用宿主接收；事件内部保持 fail-fast，不承诺与动作执行器的先后顺序。</remarks>
        public static EasyEvent OnUpdate => ActionGlobalHost.Instance.OnUpdateEvent;
        /// <summary>全局宿主的 FixedUpdate 通知。</summary>
        /// <remarks>复用公共 FixedUpdate 相位，宿主停用时暂停自动通知。</remarks>
        public static EasyEvent OnFixedUpdate => ActionGlobalHost.Instance.OnFixedUpdateEvent;
        /// <summary>全局宿主的 LateUpdate 通知。</summary>
        /// <remarks>复用公共 LateUpdate 相位，Shutdown 只结束 Action 自己的消息及桥接订阅。</remarks>
        public static EasyEvent OnLateUpdate => ActionGlobalHost.Instance.OnLateUpdateEvent;
        /// <summary>全局宿主的 OnGUI 通知，同帧可能触发多次。</summary>
        public static EasyEvent OnGUI => ActionGlobalHost.Instance.OnGuiEvent;
        /// <summary>应用退出时的通知，不等同于显式 Shutdown。</summary>
        /// <remarks>通知前关闭会话创建门禁；监听中 Shutdown 后不能重建宿主，应提前保存需要注销的句柄。</remarks>
        public static EasyEvent OnApplicationQuit => ActionGlobalHost.Instance.OnApplicationQuitEvent;
        /// <summary>应用暂停状态变化通知，参数为当前是否暂停。</summary>
        public static EasyEvent<bool> OnApplicationPause => ActionGlobalHost.Instance.OnApplicationPauseEvent;
        /// <summary>应用焦点变化通知，参数为当前是否获得焦点。</summary>
        public static EasyEvent<bool> OnApplicationFocus => ActionGlobalHost.Instance.OnApplicationFocusEvent;
    }
}
