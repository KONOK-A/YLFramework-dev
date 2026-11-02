using Topic = YLFramework.Editor.Actions.ActionKitApiHelpContent.Topic;

namespace YLFramework.Editor.Actions
{
    /// <summary>保存 ActionKit 帮助指南的静态内容和示例文本，不负责执行示例。</summary>
    internal static class ActionKitApiHelpGuides
    {
        private const string MethodContext =
            "将方法放在自己的类内；文件顶部添加 using UnityEngine; using YLFramework.Actions; " +
            "直接使用 ActionKit 类型名。带 owner 参数的方法需要传入仍存活的 MonoBehaviour，" +
            "其 GameObject 应处于激活状态；请在 Unity 主线程调用。" +
            "若业务命名空间或其他导入使 ActionKit 名称冲突，使用完整类型名 YLFramework.Actions.ActionKit，不使用通用 Kit 别名。";

        /// <summary>按指南顺序声明的主题；类型名用于建立 API 关联，而不是运行时调用。</summary>
        internal static readonly Topic[] Topics =
        {
            new Topic("overview", "从一个动作序列开始", "入门与生命周期",
                "把回调和等待按顺序组合，再绑定到场景对象执行。",
                "ActionKit.Sequence() 创建序列，Callback 添加要执行的代码，Delay 添加以秒为单位的等待，Start(this) 开始执行。" +
                "Start 会自动初始化模块，不必为每个使用动作的组件先调用 Initialize。\n" +
                "示例先输出开始消息，等待动作完成后再输出结束消息。序列由对象上的执行器推进，对象销毁时取消未完成动作。",
                "等待随执行器的时间步长推进，不保证恰好在某个真实时间点结束。不要在每个使用者的 OnDisable 中调用 Shutdown，" +
                "它会关闭整个模块，而不是只关闭这个对象的动作。\n" +
                "使用独立程序集定义文件时，需要引用 YLFramework.Runtime。Action 是 YLFramework 内建模块，沿用框架依赖契约，" +
                "动作核心不要求安装可选 DOTween 包装。",
                "将完整脚本保存为 ActionKitQuickStart.cs，挂到场景中已激活的 GameObject 上，保持组件启用后进入播放模式。" +
                "控制台应先出现“动作开始”，随后经过等待出现“等待结束”；不承诺精确一秒的日志时间差。",
                "using UnityEngine;\nusing YLFramework.Actions;\n\n" +
                "public class ActionKitQuickStart : MonoBehaviour\n{\n    private void Start()\n    {\n" +
                "        ActionKit.Sequence()\n            .Callback(() => Debug.Log(\"动作开始\"))\n" +
                "            .Delay(1f)\n            .Callback(() => Debug.Log(\"等待结束\"))\n" +
                "            .Start(this);\n    }\n}",
                "ActionKit", "ISequence", "IActionExtensions")
            {
                FeaturedTypeNames = new[] { "ActionKit", "ISequence", "IActionExtensions" }
            },
            new Topic("lifecycle", "初始化与模块关闭", "入门与生命周期",
                "由应用入口管理整个模块的初始化、关闭和重新启用。",
                "Initialize 与 InitArchitecture 在同一轮初始化中不会重复创建实例。OnRegisterPatch 对每个新初始化的实例执行一次，" +
                "Interface 返回本模块的 ActionKit 实例，并在需要时初始化。\n" +
                "Shutdown 取消模块管理的执行，释放模块自己的执行器、宿主、资源和全局事件监听；" +
                "保留用户 GameObject 及不属于模块的协程。IdGenerator 和 OnRegisterPatch 不会因此重置。" +
                "若动作回调中请求关闭，实际收尾会等执行栈退出后完成。",
                "只在应用级边界调用 Shutdown，不要把它作为每个动作使用者的停用处理。清理错误会向调用方报告。" +
                "关闭回调或初始化补丁中不能立即重新初始化；需要等当前动作调用及其关闭收尾全部返回。\n" +
                "Interface 只返回当前 ActionKit 模块实例，不提供系统、模型、工具的注册，也不提供命令、查询或事件容器。",
                MethodContext + "\n下面的方法由应用入口在动作执行栈外调用，例如切换一轮运行环境时；" +
                "不要从 Callback、OnExecute、OnFinish 或 OnRegisterPatch 内调用它。普通动作启动不需要这段代码。",
                "public static void RestartAtApplicationBoundary()\n{\n" +
                "    // 仅在上一轮动作调用和关闭收尾均已返回后，由应用入口调用。\n" +
                "    ActionKit.Shutdown();\n    ActionKit.Initialize();\n}",
                "ActionKit")
            {
                FeaturedTypeNames = new[] { "ActionKit" }
            },
            new Topic("delay", "按时间等待", "基础动作",
                "等待指定秒数后执行回调，或在序列中只等待而不执行回调。",
                "ActionKit.Delay(seconds, callback) 的 callback 参数必须传入；序列的 Delay 可以省略回调。" +
                "序列也接受 Func<float>，在这次等待开始时计算一次时长，不会每次更新重新计算。\n" +
                "等待动作先判断已累计时间是否达到时长，再累加本次 dt。正常执行默认使用受时间缩放影响的步长，" +
                "控制器的 IgnoreTimeScale 可改用未缩放步长。完成后由执行器释放动作；取消不会调用成功完成回调。",
                "不能用参数秒数保证精确的真实时间完成点。控制器暂停时不推进等待，但时间缩放和对象停用也会影响自动更新。",
                MethodContext + "\n调用 DelayedCallback(owner) 后会启动两个独立等待；第二个序列只等待，不输出日志。",
                "public static void DelayedCallback(MonoBehaviour owner)\n{\n" +
                "    ActionKit.Delay(1f, () => Debug.Log(\"时间等待结束\")).Start(owner);\n" +
                "    ActionKit.Sequence().Delay(() => 0.5f).Start(owner);\n}",
                "ActionKit", "DelayExtension")
            {
                FeaturedTypeNames = new[] { "ActionKit", "DelayExtension" }
            },
            new Topic("frames", "按帧等待", "基础动作",
                "使用 Unity 的全局帧计数等待，NextFrame 等价于等待一帧。",
                "DelayFrame 开始时记录 Time.frameCount，之后在执行时检查是否达到起始帧数加 frameCount。" +
                "它不累计 dt，也不是按动作实际获得的更新次数计数。NextFrame(callback) 使用相同机制，帧数为 1。\n" +
                "frameCount 小于或等于零时，动作也要到开始后的执行检查才结束，不在开始钩子中完成。",
                "Pause 只停止动作推进，Unity 的 Time.frameCount 在其他帧仍继续增长，因此恢复后可能立即达到完成条件。" +
                "不要用它表示“暂停期间也冻结的剩余帧数”。",
                MethodContext + "\n调用 FrameCallbacks(owner) 启动两个独立动作。日志说明达到帧阈值，不用于保证准确的显示时刻。",
                "public static void FrameCallbacks(MonoBehaviour owner)\n{\n" +
                "    ActionKit.DelayFrame(10, () => Debug.Log(\"已达到帧数阈值\")).Start(owner);\n" +
                "    ActionKit.NextFrame(() => Debug.Log(\"下一帧等待结束\")).Start(owner);\n}",
                "ActionKit", "DelayFrameExtension")
            {
                FeaturedTypeNames = new[] { "ActionKit", "DelayFrameExtension" }
            },
            new Topic("condition", "回调与条件等待", "基础动作",
                "立即执行回调，持续等待条件，或按开始时的条件选择一个分支。",
                "Callback 在动作开始时执行委托，可能在 Start 返回前完成。Condition 接受返回布尔值的条件，" +
                "从执行阶段开始检查，条件为真时执行可选的条件回调并完成。\n" +
                "ConditionSequence 不是持续等待：只在分支开始时判断一次。为真则执行配置的子序列，为假则跳过；" +
                "子序列取消也会取消这个分支。",
                "条件委托会被反复调用，应避免把只能执行一次的副作用放在 Condition 的判断中。" +
                "如果希望等待条件未来成立，使用 Condition，而不是 ConditionSequence。",
                MethodContext + "\n在播放模式调用 ConditionalCallback(owner)，点击鼠标左键后输出消息；" +
                "随后根据分支开始时 owner.enabled 的值决定是否再等待。",
                "public static void ConditionalCallback(MonoBehaviour owner)\n{\n" +
                "    ActionKit.Sequence().Condition(() => Input.GetMouseButtonDown(0))\n" +
                "        .Callback(() => Debug.Log(\"检测到点击\"))\n" +
                "        .ConditionSequence(() => owner.enabled, branch => branch.Delay(0.2f))\n" +
                "        .Start(owner);\n}",
                "ActionKit", "CallbackExtension", "ConditionAction", "ConditionExtension", "ConditionSequence", "ConditionSequenceExtension")
            {
                FeaturedTypeNames = new[] { "ActionKit", "ConditionExtension", "ConditionSequenceExtension", "CallbackExtension" }
            },
            new Topic("sequence", "顺序执行与嵌套组合", "组合与执行",
                "按添加顺序执行动作，可嵌套序列和并行动作组。",
                "ISequence.Append 接受子动作并返回当前序列，便于继续添加。现代序列可以在同一次推进中经过多个立即完成的回调，" +
                "启动后续动作时使用 Execute(0)，不会把已经使用的整段 dt 再交给下一个动作。\n" +
                "子动作取消会取消父序列及其余子动作，不触发成功完成回调。序列结束或取消时负责释放自己的子动作。",
                "不要把同一活动动作同时交给多个执行者。",
                MethodContext + "\nComposition(owner) 先等待并行组的两项都结束，再执行嵌套序列和最外层完成回调。",
                "public static void Composition(MonoBehaviour owner)\n{\n" +
                "    ActionKit.Sequence().Parallel(p => p.Delay(1f).Delay(2f))\n" +
                "        .Sequence(s => s.Callback(() => Debug.Log(\"并行部分结束\")))\n" +
                "        .Start(owner, () => Debug.Log(\"整个序列结束\"));\n}",
                "ActionKit", "ISequence", "SequenceExtension", "ParallelExtension")
            {
                FeaturedTypeNames = new[] { "ActionKit", "ISequence", "SequenceExtension", "ParallelExtension" }
            },
            new Topic("parallel", "并行推进多个动作", "组合与执行",
                "在同一个更新阶段推进多个子动作，全部完成后结束动作组。",
                "ActionKit.Parallel() 创建并行组；添加的各个子动作在 Unity 主线程逐项推进，不会创建工作线程。" +
                "只有所有子动作正常完成时，动作组才正常完成。任一子动作取消都会取消整个组。\n" +
                "动作组结束或取消时释放子动作；一个子动作清理出错时，仍会尝试清理其他子动作并报告错误。",
                "当前空并行组不会自动结束，不要把未添加子动作的 Parallel 当作可立即完成的空操作。" +
                "并行表示动作时间重叠，不表示线程并发，也不保证子回调在同一真实时刻触发。",
                MethodContext + "\n调用 ParallelActions(owner) 后，两项等待独立推进，最后一项完成后才输出全部结束消息。",
                "public static void ParallelActions(MonoBehaviour owner)\n{\n" +
                "    ActionKit.Parallel().Delay(1f, () => Debug.Log(\"第一项结束\"))\n" +
                "        .Delay(2f, () => Debug.Log(\"第二项结束\"))\n" +
                "        .Start(owner, () => Debug.Log(\"全部结束\"));\n}",
                "ActionKit", "IParallel", "ParallelExtension")
            {
                FeaturedTypeNames = new[] { "ActionKit", "IParallel", "ParallelExtension" }
            },
            new Topic("repeat", "重复执行一个序列", "组合与执行",
                "把一组动作作为一次迭代，按指定次数重复执行。",
                "ActionKit.Repeat(repeatCount) 的默认值为 -1。现代 Repeat 中，-1 和 0 都表示无限重复，1 表示执行一次，" +
                "正整数 n 表示执行 n 次。每次内部序列完成后会重置，下一次执行推进下一轮。\n" +
                "暂停通过父动作的执行门控制；内部动作取消时，整个重复动作取消。IRepeat 不提供当前迭代次数。",
                "其他负数目前不会正常推进，避免传入 -1 以外的负值。无限重复需要由拥有者明确取消。",
                MethodContext + "\nRepeatedCallbacks(owner) 每轮先等待再输出一次消息，总共五轮，然后执行成功完成回调。",
                "public static void RepeatedCallbacks(MonoBehaviour owner)\n{\n" +
                "    ActionKit.Repeat(5).Delay(0.2f).Callback(() => Debug.Log(\"完成一轮\"))\n" +
                "        .Start(owner, () => Debug.Log(\"五轮执行结束\"));\n}",
                "ActionKit", "Repeat", "IRepeat", "RepeatExtension")
            {
                FeaturedTypeNames = new[] { "ActionKit", "IRepeat", "RepeatExtension" }
            },
            new Topic("custom", "用回调定义动作", "扩展与工具",
                "配置开始、更新和完成行为，用泛型数据保存本次动作的状态。",
                "ActionKit.Custom<TData> 的配置委托接收 ICustomApi<TData>，工厂本身返回 IAction。" +
                "OnStart 设置开始回调，OnExecute 的 dt 来自执行器选定的时间步长；调用 Finish 才把动作标记为成功完成。\n" +
                "OnFinish 设置成功完成回调。取消时清空数据及委托，但不会替用户调用数据对象的 Dispose。" +
                "对同一回调位置多次调用设置方法会替换原委托。",
                "仅从 OnExecute 回调返回不会结束动作。OnFinish 不是取消清理钩子，外部资源必须由自己的拥有者管理。" +
                "完成或取消后不要长期保留配置接口，因为动作实例会被池复用。",
                MethodContext + "\nCustomAction(owner) 用 Data 累计步长，到达阈值后显式 Finish；" +
                "示例数据为数值，不涉及外部资源释放。",
                "public static void CustomAction(MonoBehaviour owner)\n{\n" +
                "    ActionKit.Custom<float>(a => a.OnStart(() => a.Data = 0f)\n" +
                "        .OnExecute(dt => { a.Data += dt; if (a.Data >= 1f) a.Finish(); })\n" +
                "        .OnFinish(() => Debug.Log(\"自定义动作结束\"))).Start(owner);\n}",
                "ActionKit", "ICustomApi", "CustomExtension")
            {
                FeaturedTypeNames = new[] { "ActionKit", "ICustomApi" }
            },
            new Topic("coroutine", "把协程接入动作", "组合与执行",
                "等待协程枚举器结束，再让动作序列继续执行。",
                "ActionKit.Coroutine 接受返回 IEnumerator 的工厂；序列的 Coroutine 扩展也可接收工厂，" +
                "已有枚举器可以通过 ToAction 转为动作。工厂在动作开始时提供枚举器。\n" +
                "动作取消时只停止自己拥有的协程句柄，不停止宿主上不相关的协程。模块协程宿主销毁时也会取消相关动作。",
                "控制器 Pause 不会暂停 Unity 正在运行的底层协程，IgnoreTimeScale 也不改变 WaitForSeconds 的计时规则。" +
                "这与取消后停止该协程是两种不同操作。\n" +
                "嵌套 IEnumerator 的 MoveNext 异常仍由 Unity 报告，不保证所有此类故障都会自动完成或取消动作。" +
                "重复执行时应使用工厂，已有枚举器的 ToAction 不会自动创建新的枚举器。",
                MethodContext + "\n另需添加 using System.Collections;。WaitAndLog 和 CoroutineAction 两个方法放在同一个类中。调用 CoroutineAction(owner) 开始等待，" +
                "不要在这里自行启动另一个相同的协程。",
                "private static IEnumerator WaitAndLog()\n{\n" +
                "    yield return new WaitForSeconds(1f);\n    Debug.Log(\"协程结束\");\n}\n" +
                "public static void CoroutineAction(MonoBehaviour owner)\n{\n" +
                "    ActionKit.Sequence().Coroutine(WaitAndLog).Start(owner);\n}",
                "ActionKit", "CoroutineExtension")
            {
                FeaturedTypeNames = new[] { "ActionKit", "CoroutineExtension" }
            },
            new Topic("task", "等待异步任务", "组合与执行",
                "将 Task 的完成状态接入动作流程，成功后继续后续动作。",
                "ActionKit.Task 接受返回 Task 的工厂，序列的 Task 扩展用于追加任务等待，已有 Task 可通过 ToAction 接入。" +
                "任务成功完成后动作完成；任务失败或被取消时，等待动作及其父组合取消，不执行成功完成回调。" +
                "活动等待中的任务异常会报告。\n" +
                "动作 Deinit 解除对这次任务结果的监听，不会 Dispose 或 Cancel 外部拥有的 Task。",
                "Pause 不会暂停底层 Task，取消动作也不等于取消任务本身。需要停止外部操作时，" +
                "由该操作的拥有者提供并触发自己的取消机制。不要在工作线程中访问 Unity 对象。",
                MethodContext + "\n另需添加 using System.Threading.Tasks;。WaitTask 和 TaskAction 两个方法放在同一个类中。任务只执行异步等待，" +
                "日志放在随后由动作执行器推进的回调中。",
                "private static async Task WaitTask()\n{\n    await Task.Delay(100);\n}\n" +
                "public static void TaskAction(MonoBehaviour owner)\n{\n" +
                "    ActionKit.Sequence().Task(WaitTask).Callback(() => Debug.Log(\"异步任务结束\")).Start(owner);\n}",
                "ActionKit", "TaskAction", "TaskExtension")
            {
                FeaturedTypeNames = new[] { "ActionKit", "TaskExtension" }
            },
            new Topic("observable", "等待可观察数据源的首个值", "扩展与工具",
                "用内建的 BCL Observable 适配器把订阅接入动作；不需要第三方 UniRx SDK。",
                "UniRxAction<T>.Allocate 接受 Func<IObservable<T>> 工厂，在动作 OnStart 时取得 source 并订阅。" +
                "ActionKitUniRxExtension.ToAction 包装已有 source，ISequence.UniRx 追加工厂动作；UniRx 是保留的兼容接口名。\n" +
                "首个 OnNext 标记成功；没有值的 OnCompleted 取消动作；OnError 记录异常并取消，不调用成功完成回调。" +
                "主线程通知即时 Finish，但 Finish 只标记状态，执行器随后调用 OnFinish 才释放 subscription。" +
                "OnFinish 保留 factory 到 Deinit；Reset 释放旧 subscription 并保留 factory，供 Repeat 每轮重新订阅。" +
                "Deinit 清空 factory 并安排回池，不 Dispose source 本体。",
                "Observable 内建于 YLFramework.Runtime；消费者 asmdef 只需引用 YLFramework.Runtime，无需额外安装适配器。\n" +
                "所有动作生命周期调用必须在 Unity 主线程；只有 source 的通知可以跨线程。后台通知通过启动时捕获的 SynchronizationContext " +
                "Post 回拥有线程；没有上下文时由拥有线程的 OnExecute 消费。Pause 不暂停 source，不能阻止主线程通知或已投递通知标记成功。\n" +
                "同步 Subscribe 返回前发生 Deinit 或 Reset，迟到的 IDisposable 仍只释放一次；旧订阅通知不会影响重置或复用后的动作。" +
                "factory 或 Subscribe 抛错会取消并向调用方抛出；Dispose 错误可见，Deinit 仍安排回池。" +
                "ToAction 每轮重订阅同一 source；Repeat 需要新 source 时应使用返回新实例的工厂。",
                MethodContext + "\n另需 using System;。把下面的微型 BCL 数据源和 helper 方法放在同一个类中。" +
                "ObservableAction(owner) 先建立订阅，再发送一个值；完成阶段的 Dispose 解除该订阅。" +
                "这是方法片段，不是完整示例场景，也不适用于多个同时订阅者。",
                "private sealed class ActionKitSignal : IObservable<int>\n{\n" +
                "    private IObserver<int> mObserver;\n" +
                "    public IDisposable Subscribe(IObserver<int> observer)\n" +
                "    { mObserver = observer; return new Unsubscribe(this); }\n" +
                "    public void Send() => mObserver?.OnNext(1);\n" +
                "    private sealed class Unsubscribe : IDisposable\n    {\n" +
                "        private ActionKitSignal mSource;\n" +
                "        public Unsubscribe(ActionKitSignal source) { mSource = source; }\n" +
                "        public void Dispose()\n" +
                "        { if (mSource == null) return; mSource.mObserver = null; mSource = null; }\n" +
                "    }\n}\n" +
                "public static void ObservableAction(MonoBehaviour owner)\n{\n" +
                "    var source = new ActionKitSignal();\n" +
                "    ActionKit.Sequence().UniRx(() => source).Callback(() => Debug.Log(\"收到首个值\")).Start(owner);\n" +
                "    source.Send();\n}",
                "UniRxAction", "ActionKitUniRxExtension", "IAction", "ISequence")
            {
                FeaturedTypeNames = new[] { "UniRxAction", "ActionKitUniRxExtension", "IAction", "ISequence" }
            },
            new Topic("dotween", "把独立补间接入动作", "扩展与工具",
                "用可选 DOTween wrapper 管理一个独立根 Tween 的执行与取消，而不是盲等播放状态。",
                "DOTweenAction.Allocate 接受 Func<DG.Tweening.Tween>，每次 OnStart 调用 getter 恰一次，取得并接管该 Tween 后 Restart。" +
                "ISequence.DOTween 追加该工厂动作；Tween.ToAction 在 Pause 前预绑定原 Tween 生命周期一次，保留立即 Pause 的行为，启动时只复用该绑定并 Restart。" +
                "启动前原 Tween 已 kill 或复租时，不会认领同一对象上的新生命周期。ToAction 是单次绑定；首次启动前 Reset 保留同一预绑定，支持组合的准备步骤。" +
                "执行开始后，Reset 结束原绑定，再 Start 会取消，不重新认领。\n" +
                "首个有效完成标记成功，包括 Complete() 和 Kill(true) 触发的完成；外部 Pause 只是等待，不视为完成。" +
                "Kill(false) 取消，不通知成功。动作 Pause 转发给所管理且正在播放的 Tween，Resume 只恢复由动作暂停的 Tween。\n" +
                "Deinit 立即标记 IsDeinitialized，Reset 立即恢复 NotStart 和非暂停状态；物理 Kill(false) 通过捕获的 UnitySynchronizationContext.Post 延后执行，包含 paused Tween。" +
                "待清理期间保留 pending marker，阻止其他动作重复认领；执行时复核实际主线程、active、原 marker 及其他 owner，仅清理仍 owned 的 Tween。" +
                "OnFinish 仅 detach hooks，不 kill 已完成且 autoKill=false 的 Tween；source owner 负责该 Tween 之后的生命周期。",
                "复制完整 Integrations/DOTween 文件夹；消费者 asmdef 引用 YLFramework.Runtime、YLFramework.Actions.DOTween " +
                "及实际 DG.Tweening.Tween 所在的 DOTween.dll。核心和 Observable-only 不要求此 DLL。当前核对版本为 1.2.825。" +
                "必须传独立根 Tween 或 DG.Tweening.Sequence 本身，不能传 nested Sequence 的内部子 Tween：" +
                "库没有公开 isSequenced 查询，且忽略子 Tween 的独立控制。YLFramework.Actions.Sequence 与 DG.Tweening.Sequence 不是同一类型。\n" +
                "时间由 Tween 自己的 SetUpdate(Manual/independent) 配置管理；Manual 需调用方推进 DOTween.ManualUpdate，" +
                "IgnoreTimeScale 不自动改变 Tween clock。所有动作与 Tween 操作在 Unity 主线程执行。" +
                "ToAction 和 OnStart 要求当前上下文为真实 Unity 主线程的原始 UnitySynchronizationContext：校验完整类型名、UnityEngine 程序集身份、sealed 类型和初始化时记录的主线程 ID。" +
                "没有该上下文会抛错，并且不调用 factory、不 Pause Tween；不支持自定义同步 Post 或人工 pump 上下文。\n" +
                "Shutdown 不保证同步物理 Kill；Domain Reload 或退出前 Post 未执行时，可能没有物理清理。\n" +
                "adapter 仅以 += 添加自己的 onComplete/onKill，解除时仅移除自己的 hooks，不覆盖 source callbacks。" +
                "onKill marker 丢失或被替换时 ownership 无法证实，保守取消且放弃 Kill 可能复租的 Tween，不保证物理清理；onPause 不清理，也不是 ownership marker。" +
                "公开 API 没有唯一租期身份，不能完全区分已 kill 后复租和用户覆写 marker。" +
                "前置用户 callback 抛错时，safeMode 对整个 multicast 捕获可能跳过 adapter 并漏掉完成或取消信号，不能承诺逐 callback 异常隔离。" +
                "Reset 的内部 version 只保护旧 callback/调用栈重入，不是新增公共 API。Repeat 应用工厂每轮创建新 Tween，" +
                "ToAction 不会为被 kill 的旧 Tween 创建新实例，也不能用于执行开始后的 Reset 后重新绑定；getter 返回 null、已 kill 或带活动/待清理 ownership marker 的 Tween 会报错并取消。",
                MethodContext + "\n仅在已安装 wrapper 与 DOTween.dll 后添加 using YLFramework.Integrations.DOTween; 和 using DG.Tweening;，供 DOTween 扩展方法使用。" +
                "在 Unity 正常主线程回调且保留原始 UnitySynchronizationContext 时调用；不要替换上下文或人工 pump。" +
                "示例用普通 float 局部变量和 DG.Tweening.DOTween.To，不使用依赖额外 Modules 的 DOMove。" +
                "调用 TweenAction(owner) 以 ActionKit 序列等待独立根 Tween，再输出完成日志；默认 autoKill 负责完成后的 Tween 释放。",
                "public static void TweenAction(MonoBehaviour owner)\n{\n" +
                "    float value = 0f;\n" +
                "    ActionKit.Sequence().DOTween(() => DG.Tweening.DOTween.To(() => value, x => value = x, 1f, 0.5f))\n" +
                "        .Callback(() => Debug.Log(\"补间动作结束\"))\n" +
                "        .Start(owner);\n}",
                "DOTweenAction", "ActionKitDOTweenExtension", "IAction", "ISequence")
            {
                RequiredAssembly = "YLFramework.Actions.DOTween",
                FeaturedTypeNames = new[] { "DOTweenAction", "ActionKitDOTweenExtension", "IAction", "ISequence" }
            },
            new Topic("control", "选择执行范围与控制动作", "入门与生命周期",
                "选择对象、当前场景或全局范围，并控制仍在运行的动作。",
                "Start(owner) 在 owner 的 GameObject 上使用 Update 执行器，执行器销毁时取消动作。" +
                "只禁用 owner 组件不会取消动作；GameObject 不激活时自动 Update 暂停。\n" +
                "StartCurrentScene 使用场景共享宿主，在宿主所属场景卸载时取消，不是每次活动场景切换都取消。" +
                "StartGlobal 使用跨场景的模块宿主。\n" +
                "Start 返回控制器；Pause 停止动作推进，Resume 恢复，IgnoreTimeScale 改用未缩放 dt，" +
                "Deinit 取消当前动作且不调用成功完成回调。\n" +
                "StartNamed(owner, flag)、StartNamedCurrentScene(flag)、StartNamedGlobal(flag) 返回 ActionHandle，命名的是根执行而非子节点。" +
                "同作用域名字采用 ordinal、区分大小写，拒绝空白和有效重名；重名拒绝不消费新动作。" +
                "ReplaceNamed 及对应 CurrentScene/Global 入口先验证，再取消旧任务并启动新任务；失败不恢复旧任务，也不通知旧任务成功。\n" +
                "ActionKit.TryGetNamed/ContainsNamed/CancelNamed/PauseNamed/ResumeNamed 及 CurrentScene/Global 后缀只查询现有宿主和执行器，不初始化或创建。" +
                "名字在根执行正常结束、取消、异常、宿主销毁或 Shutdown 时释放；正常完成通知可新建同名任务，旧收尾不会删除新登记。" +
                "Repeat 子节点每轮回调不等于根完成，仍占用名字，最终根完成才释放。",
                "控制器和动作都是池中的原始引用，完成或回收后不能作为永久句柄保存。立即完成的动作可能在 Start 返回前已释放控制器。" +
                "同一活动租约的重复启动会被拒绝。\n" +
                "IgnoreTimeScale 不改变 Task、协程或 DOTween 自己的时钟。Recycle、Reset 和修改 ActionId 属于基础设施操作，" +
                "不是日常取消方式。\n" +
                "ActionHandle 的 IsActive/Pause/Resume/Cancel/SetUpdateMode 每次验证原执行租约，默认值和已结束句柄返回 false；" +
                "ActionId=0 也可有效，运行中 Reset 不换租约。Start 返回的原始 IActionController 仍不是安全长期句柄。" +
                "对象作用域是 GameObject，不是单独组件；不同对象、场景/全局执行器及 Timer 的名字索引互不共享。",
                MethodContext + "\n示例用较长等待确保获取控制器后仍有活动动作，立即演示暂停、恢复和取消；" +
                "因此该等待的完成日志不应出现。另两项日志来自独立的场景和全局动作。",
                "public static void ControlledAction(MonoBehaviour owner)\n{\n" +
                "    var controller = ActionKit.Delay(10f, () => Debug.Log(\"等待正常结束\")).Start(owner).IgnoreTimeScale();\n" +
                "    controller.Pause();\n    controller.Resume();\n" +
                "    controller.Deinit(); // 只取消这个仍在运行的动作。\n" +
                "    ActionKit.NextFrame(() => Debug.Log(\"场景范围动作\")).StartCurrentScene();\n" +
                "    ActionKit.NextFrame(() => Debug.Log(\"全局范围动作\")).StartGlobal();\n" +
                "    var handle = ActionKit.Delay(10f, null).StartNamed(owner, \"named\");\n" +
                "    handle.SetUpdateMode(ActionUpdateModes.UnscaledDeltaTime);\n" +
                "    handle.Pause();\n    handle.Resume();\n" +
                "    if (ActionKit.TryGetNamed(owner, \"named\", out var current)) current.Cancel();\n" +
                "    ActionKit.Delay(1f, null).ReplaceNamed(owner, \"named\");\n}",
                "IActionController", "ActionController", "ActionHandle", "ActionKit", "ActionUpdateModes", "IActionControllerExtensions", "IActionExtensions",
                "IActionExecutor", "IActionExecutorExtensions", "MonoUpdateActionExecutorExtension", "ActionKitCurrentScene")
            {
                FeaturedTypeNames = new[] { "IActionExtensions", "IActionController", "IActionControllerExtensions", "ActionHandle", "ActionKit" }
            },
            new Topic("messages", "监听 Unity 消息", "扩展与工具",
                "通过模块事件监听更新、界面绘制、应用退出、暂停和焦点消息。",
                "OnUpdate、OnFixedUpdate、OnLateUpdate、OnGUI 和 OnApplicationQuit 使用无参数 EasyEvent。" +
                "OnApplicationPause 与 OnApplicationFocus 使用带 bool 参数的事件，分别表示暂停和焦点状态。\n" +
                "运行时访问这些事件属性可能创建全局宿主。Register 返回注销句柄（IUnregister），" +
                "可绑定到对象销毁时注销，也可以由调用方自行注销。",
                "这些是 YLFramework.Actions 的 Unity 消息事件。每个监听都要有明确生命周期，" +
                "不能只注册而忽略注销。OnGUI 等消息由 Unity 的相应阶段触发，不等同于每帧一次普通 Update。",
                MethodContext + "\nSubscribeToMessages(owner) 将监听绑定到 owner 的 GameObject 销毁，" +
                "只禁用 owner 组件不会触发这里的销毁注销。更新监听在示例中不执行额外操作，焦点变化输出中文消息。" +
                "另需添加 using YLFramework.Events;。",
                "public static void SubscribeToMessages(MonoBehaviour owner)\n{\n" +
                "    ActionKit.OnUpdate.Register(() => { }).UnregisterWhenGameObjectDestroyed(owner.gameObject);\n" +
                "    ActionKit.OnApplicationFocus.Register(focused => Debug.Log(focused ? \"获得焦点\" : \"失去焦点\"))\n" +
                "        .UnregisterWhenGameObjectDestroyed(owner);\n}",
                "ActionKit", "EasyEvent", "IUnregister")
            {
                FeaturedTypeNames = new[] { "ActionKit" }
            },
            new Topic("subscriptions", "管理监听与注销", "扩展与工具",
                "Action 消息的订阅与注销由 YLFramework.Events 提供：注册返回注销句柄（IUnregister），在对象或场景生命周期结束时解除监听。",
                "ActionKit 的 7 个全局消息返回 YLFramework.Events.EasyEvent；Register 返回注销句柄（IUnregister），调用 Unregister 移除监听。" +
                "注销句柄重复注销是安全的（幂等），不需要额外的判重或包装。" +
                "RegisterWithACall 在返回句柄前先同步调用一次回调。\n" +
                "注销列表可集中保存句柄；RegisterListExtension.UnregisterAll 先对列表做快照，再清空列表，最后逐个注销快照中的句柄。" +
                "生命周期扩展可在 GameObject 销毁、停用或收到场景卸载通知时注销；OrEvent 用于合并事件订阅，不用于合并动作执行。\n" +
                "ActionKit.Shutdown 只清空模块自有的事件监听，不处置用户创建的触发器与注销句柄。",
                "句柄与生命周期触发器都来自 YLFramework.Events；绑定到生命周期后，应让该绑定负责释放，避免手工再释放同一句柄。" +
                "不要把同一个句柄同时交给多个生命周期触发器反复处置。\n" +
                "UnregisterWhenAnySceneUnloaded 响应任意场景卸载；UnregisterWhenSceneUnloaded(Scene) 只绑定显式指定的场景。",
                MethodContext + "\nLocalEvent() 不需要 owner：创建独立事件，触发一次后手工注销一次；" +
                "实际对象上的长期监听应选择与拥有者一致的生命周期。另需添加 using YLFramework.Events;。",
                "public static void LocalEvent()\n{\n    var signal = new EasyEvent();\n" +
                "    IUnregister unregister = signal.Register(() => Debug.Log(\"收到本地事件\"));\n" +
                "    signal.Trigger();\n    unregister.Unregister();\n}",
                "ActionKit", "EasyEvent", "IUnregister")
            {
                FeaturedTypeNames = new[] { "ActionKit" }
            },
            new Topic("lerp", "插值与缓动曲线", "基础动作",
                "在一段时间内从起点插值到终点，并按需要应用缓动曲线。",
                "ActionKit.Lerp(a, b, duration, onLerp, onLerpFinish) 使用执行器 dt 累计时间，以秒为单位的 duration 控制过程。" +
                "开始时回调起点，推进时回调当前插值，完成时回调终点及可选完成委托。" +
                "序列的 Lerp01 是从 0 到 1 的便捷形式。\n" +
                "EaseUtility 提供 31 个缓动函数，接受起点、终点和归一化位置 pos；它们本身是计算函数，不负责动作生命周期。",
                "缓动函数并非全部限制输入范围。部分 Bounce 曲线的终点行为、Expo 曲线的非精确端点，" +
                "以及 Circle 在定义域外产生 NaN 都需要调用方留意。EaseType.Count 是计数标记，不是额外曲线。" +
                "插值取消不会调用正常完成委托。",
                MethodContext + "\nInterpolation(owner) 先产生 0 到 1 的进度，再通过三次缓动映射到 0 到 10，" +
                "控制台输出当前值。实际界面或变换动画可在该回调中使用结果。",
                "public static void Interpolation(MonoBehaviour owner)\n{\n" +
                "    ActionKit.Lerp(0f, 1f, 1f, t => Debug.Log(\"当前插值：\" + EaseUtility.InOutCubic(0f, 10f, t)))\n" +
                "        .Start(owner);\n}",
                "ActionKit", "Lerp", "LerpExtension", "EaseUtility", "EaseType")
            {
                FeaturedTypeNames = new[] { "ActionKit", "Lerp", "EaseUtility" }
            },
            new Topic("screen", "屏幕遮罩过渡", "扩展与工具",
                "用模块自己的遮罩资源控制透明度，组合遮住与揭开画面的过程。",
                "FadeIn 默认使用黑色遮罩，透明度从 0 到 1；FadeOut 从 1 到 0，两者默认时长均为一秒。" +
                "Duration 设置秒数，Color 设置颜色，FromAlpha 与 ToAlpha 设置透明度起止值。" +
                "独立淡入和淡出动作可通过 Append 放入同一序列，由父序列统一等待和取消。",
                "先通过 YLFramework > Action > 创建屏幕过渡资源 创建资源，加载路径为 " +
                "ActionKit/ScreenTransitionCanvas。当前资源根节点缩放为 0，且没有 GraphicRaycaster；" +
                "实际显示及输入遮挡效果需要检查，不能仅凭动作完成判断可见。\n" +
                "FadeInOut 会另外启动全局子序列，但父动作不会随它完成，父动作取消也不会取消该子序列，" +
                "暂停和未缩放时间设置同样不会传递。不要把它作为需要可靠完成的序列子项。\n" +
                "所有过渡共享同一个 Image，重叠执行会相互覆盖；取消不会恢复透明度。直接 Allocate 或复用实例可能保留设置，" +
                "优先使用工厂并明确配置。",
                MethodContext + "\n先创建并检查遮罩资源。OwnedFadeSequence(owner) 使用独立 FadeIn 和 FadeOut，" +
                "避免 FadeInOut 的父子生命周期限制；日志只表示动作时序，不证明画面已正确覆盖。",
                "public static void OwnedFadeSequence(MonoBehaviour owner)\n{\n" +
                "    ActionKit.Sequence().Append(ActionKit.ScreenTransition.FadeIn().Duration(0.3f))\n" +
                "        .Callback(() => Debug.Log(\"遮罩淡入动作结束\"))\n" +
                "        .Append(ActionKit.ScreenTransition.FadeOut().Duration(0.3f)).Start(owner);\n}",
                "ScreenTransition", "ScreenTransitionFade", "ScreenTransitionInOut", "ISequence")
            {
                FeaturedTypeNames = new[] { "ActionKit.ScreenTransition", "ScreenTransitionFade", "ISequence" }
            },
            new Topic("extend", "派生动作与音源动作", "扩展与工具",
                "派生动作基类或实现动作接口，让自定义行为参与同一执行流程。",
                "AbstractAction<T> 要求公开无参构造；Allocate 获取池实例、分配新编号并重置动作。" +
                "OnStart、OnExecute、OnFinish 提供执行阶段，OnReset 和 OnDeinit 是保护级生命周期钩子。\n" +
                "Finish 表示成功完成，Deinit 表示取消及释放；OnDeinit 出错仍会安排回收，并保留错误。" +
                "自己调用 Execute 时，需要自己管理初始化、推进和最终释放。\n" +
                "AudioSource.PlayAction 使用 Unity AudioSource 播放，并按 isPlaying 判断完成，不依赖 AudioKit。",
                "清理自己拥有的委托和数据，不要替借用对象释放资源。直接实现 IAction 时也必须遵守这些所有权边界。\n" +
                "音源暂停会让 isPlaying 为假，当前 PlayAction 可能把它当成播放完成；音源销毁后也可能一直等待。" +
                "不要用它保证暂停后继续等待或销毁后正常结束。",
                MethodContext + "\nOneTickAction 类型和 DerivedAction 方法放在同一个类中，OneTickAction 可以作为公开嵌套类型。" +
                "调用 DerivedAction(owner) 后，第一次执行阶段会显式 Finish；不要在外部手工回收正在运行的实例。",
                "public sealed class OneTickAction : AbstractAction<OneTickAction>\n{\n" +
                "    public override void OnExecute(float dt) { this.Finish(); }\n}\n" +
                "public static void DerivedAction(MonoBehaviour owner)\n{\n    OneTickAction.Allocate().Start(owner);\n}",
                "IAction", "AbstractAction", "ActionStatus", "IActionExtensions", "AudioSourceShortCutExtensions")
            {
                FeaturedTypeNames = new[] { "AbstractAction", "IActionExtensions", "AudioSourceShortCutExtensions" }
            },
            new Topic("distribution", "框架内建与可选集成", "扩展与工具",
                "通过正式 YLFramework 安装使用内建动作，并确认可选集成边界。",
                "Action 与 Observable 内建于 YLFramework.Runtime，消费者 asmdef 引用该唯一 Runtime。" +
                "通过 YLFramework > Toolkit > 导出框架包 使用既有框架导出流程；不再提供 Action 单文件替代安装。" +
                "默认框架包包含动作源码、组件、资源及编辑器帮助，不包含 Tests 或可选 Integrations。" +
                "DOTween 需要完整 Integrations/DOTween 文件夹及单独安装的 DOTween.dll。",
                "沿用 YLFramework 的 UGUI 等既有依赖契约，不承诺零外部依赖；不要同时安装两份 Runtime。\n" +
                "当前可选 DOTween 动作集成已可用，提供 DOTweenAction 与 ActionKitDOTweenExtension；" +
                "消费者还需引用 YLFramework.Actions.DOTween 与实际 DOTween.dll。帮助仅索引已加载的精确可选程序集，不把供应商 API 当作 ActionKit API。" +
                "内建 Observable BCL 适配提供 UniRxAction<T> 与 ActionKitUniRxExtension，不依赖第三方 UniRx SDK。" +
                "导入另一工程后仍需检查目标平台编译及实际资源效果。",
                "此页不需要运行时调用示例。请使用正式框架安装或框架包导出流程，按需另行引入可选 DOTween 源码及供应商依赖。",
                "",
                "ActionKit", "UniRxAction", "ActionKitUniRxExtension")
            {
                FeaturedTypeNames = new string[0]
            }
        };
    }
}
