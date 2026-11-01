using System;
using System.Collections.Generic;
using System.Reflection;
using YLFramework.Actions;
using MemberHelp = YLFramework.Editor.Help.HelpDocumentation;
using ParameterHelp = YLFramework.Editor.Help.HelpParameter;

namespace YLFramework.Editor.Actions
{
    /// <summary>按已核对的类型和成员元数据提供 ActionKit 帮助说明，不调用被描述的 API。</summary>
    internal static class ActionKitApiHelpReference
    {
        private const string Entry = "Action 入口与契约";
        private const string Actions = "基础与组合动作";
        private const string Execution = "执行与生命周期";
        private const string Tools = "插值与屏幕工具";

        private static readonly Dictionary<string, string> _typeDescriptions = new Dictionary<string, string>
        {
            { "ActionKit", "YLFramework 内建的动作工厂、Unity 消息订阅及模块初始化入口。工厂通常只配置动作，需显式启动；模块实例只负责自身生命周期，不是通用服务容器。" },
            { "IAction", "现代动作生命周期契约，定义标识、状态、暂停、启动/推进/结束钩子及重置、释放。具体状态类型和清理行为由实现决定。" },
            { "AbstractAction", "现代动作的可继承池化基类。Allocate 为本次使用分配 ActionId 并重置状态；派生类通过 OnReset/OnDeinit 管理自身数据。" },
            { "ActionStatus", "现代动作的三个执行状态；取消由 IsDeinitialized 标志表达，不占用额外的状态值。" },
            { "ActionUpdateModes", "选择执行器传入的缩放或未缩放 dt，不改变 Task、Unity 协程或全局帧计数的时钟。" },
            { "ISequence", "可追加 IAction 子动作的现代组合契约。标准序列依次推进子动作；Append 返回组合本身。" },
            { "IParallel", "以 ISequence 的追加方式配置并行组。标准实现同帧推进子动作，并非创建工作线程；空组不自动完成。" },
            { "IRepeat", "以 ISequence 的追加方式配置重复动作。标准实现重复内部序列，-1 和 0 都无限重复。" },
            { "ICustomApi", "自定义动作的配置契约，保存 TData 并设置启动、推进、正常完成回调；需要调用 Finish 显式标记完成。" },
            { "Delay", "按执行器 dt 累积时间的现代延时动作；先检查已累计时间，再加入本次 dt，不承诺精确墙钟时间。" },
            { "DelayFrame", "以 OnStart 时的 Time.frameCount 为起点，等待 Unity 全局帧计数达到阈值的现代动作。" },
            { "CallbackAction", "在 OnStart 调用回调并标记完成的现代动作，可以在 Start 调用期间同步完成。" },
            { "ConditionAction", "执行阶段重复检查谓词，成立时调用回调并完成；OnStart 本身不检查条件。" },
            { "ConditionSequence", "启动时只检查一次条件，成立则创建并配置分支序列，否则跳过并完成；不是等待条件成立的 Condition。" },
            { "Sequence", "按追加顺序执行现代子动作，可在同一步越过多个即时动作；后继子动作以 Execute(0) 启动。子动作取消会取消组合。" },
            { "Parallel", "同帧推进多个现代子动作，全部正常结束才完成；子动作取消会取消组合，空组不会自动完成。" },
            { "Repeat", "重复执行内部现代序列，-1 和 0 无限重复，正数指定轮数；其他负数保留不推进的边界。" },
            { "Custom", "由用户回调驱动的现代动作，支持 TData；配置回调会替换原回调。释放清空委托和数据，不 Dispose 用户数据。" },
            { "CoroutineAction", "把 IEnumerator 工厂包装为现代动作，保存其专属协程句柄。取消停止该句柄，但 IsPaused 不暂停 Unity 协程。" },
            { "TaskAction", "观察 Task 结果的现代动作。任务成功才完成动作，任务取消或失败取消动作；动作取消仅解除观察，不取消或 Dispose 外部 Task。" },
            { "UniRxAction", "内建 BCL IObservable<T> 订阅动作，首个 OnNext 成功，空 OnCompleted 取消，OnError 记录异常后取消。只释放 subscription，不 Dispose source 本体；UniRx 是兼容名称，不要求第三方 SDK。" },
            { "ActionKitUniRxExtension", "内建 Observable 接入扩展：ToAction 包装已有 source，UniRx 向 ISequence 追加 source 工厂；动作未启动时不订阅。" },
            { "DOTweenAction", "可选独立根 Tween 所有权动作，要求真实 Unity 主线程及原始 UnitySynchronizationContext。工厂每次执行取得一个新 Tween；ToAction 仅复用一次预绑定生命周期。取消/重置逻辑立即生效，物理 Kill(false) 通过 Post 延后且复核 ownership，正常完成仅解除自身 hooks。" },
            { "ActionKitDOTweenExtension", "可选 DOTween 接入扩展：ToAction 在 Pause 前预绑定已有独立根 Tween 的原生命周期，Reset 后不可重新认领；DOTween 向 ISequence 追加每次执行创建新 Tween 的工厂。" },
            { "DelayExtension", "向现代 ISequence 追加定时等待，可直接给秒数或在动作启动时求值一次的时间工厂。" },
            { "DelayFrameExtension", "向现代 ISequence 追加基于 Unity 全局帧计数的等待，NextFrame 等价于等待一帧。" },
            { "CallbackExtension", "向现代 ISequence 追加在子动作启动时执行的即时回调。" },
            { "ConditionExtension", "向现代 ISequence 追加重复检测谓词的等待动作。" },
            { "ConditionSequenceExtension", "向现代 ISequence 追加只在启动时检查一次条件的分支序列。" },
            { "SequenceExtension", "配置独立的内层现代序列并把它追加到外层组合。" },
            { "ParallelExtension", "配置内层现代并行组并把它追加到外层组合，不启动多线程。" },
            { "RepeatExtension", "配置内层现代重复动作并把它追加到外层组合。" },
            { "CustomExtension", "配置自定义现代动作并追加到序列，配置回调接收 ICustomApi<TData>，不是执行完成回调。" },
            { "CoroutineExtension", "向现代序列追加协程，或把已有 IEnumerator 包装成 IAction；已有枚举器无法为每轮重复重新创建。" },
            { "TaskExtension", "向现代序列追加 Task 工厂，或把已有 Task 包装成 IAction；不接管外部任务的取消和释放。" },
            { "IActionController", "控制一次现代动作执行的契约，连接 Action、ActionId 和 dt 模式；不能把池化控制器当作永久句柄。" },
            { "ActionController", "现代动作的池化控制器。Reset/Deinit 仅作用于标识匹配的关联动作；Recycle 回收控制器而不是普通取消接口。" },
            { "ActionHandle", "命名根执行的只读值类型安全句柄。每次查询和操作都复核动作标识、标准控制器代次及本次登记，结束或池复用后不会控制后来同名任务；默认值无效。" },
            { "IActionControllerExtensions", "调整现代控制器的 dt 来源，返回原控制器继续配置。" },
            { "IActionExtensions", "现代动作的手动推进、完成标记与对象/场景/全局启动扩展。" },
            { "IActionExecutor", "接收控制器和正常完成回调的执行器契约。调度方式和资源生命周期由实现决定。" },
            { "IActionExecutorExtensions", "推进控制器当前关联的现代动作，在有效动作正常完成时通知并清理；失效或取消不作为成功通知。" },
            { "MonoUpdateActionExecutorExtension", "在 MonoBehaviour 所属 GameObject 上获取或添加现代 Update 执行器并调度动作。" },
            { "ActionKitCurrentScene", "当前场景动作的共享宿主；所属场景卸载时宿主销毁，不等同于每次切换 activeScene 都取消。" },
            { "Lerp", "按执行器 dt 在 A 与 B 间线性插值的现代动作，启动发送起点，结束发送终点并调用完成回调。" },
            { "LerpExtension", "向现代序列追加数值插值，Lerp01 固定使用 0 到 1。" },
            { "EaseUtility", "31 个返回 float 的缓动计算函数，输入起点、终点和归一化位置；保留源码曲线及边界，不统一裁剪或修正端点。" },
            { "EaseType", "缓动曲线标识；Count 是数量哨兵，不代表额外曲线。InoutQuart 保留原拼写。" },
            { "ActionKit.ScreenTransition", "创建黑色屏幕淡入、淡出及进出包装动作，依赖 Resources/ActionKit/ScreenTransitionCanvas。" },
            { "ScreenTransitionFade", "修改共享屏幕 Image 的颜色和透明度的现代动作；多个过渡会覆盖同一图像，取消不恢复透明度。" },
            { "ScreenTransitionInOut", "配置进出动作并另启全局序列的包装动作。当前源码未桥接子序列的完成或取消，不能当作完成安全的普通组合子动作。" },
            { "AudioSourceShortCutExtensions", "把 UnityEngine.AudioSource.Play 及 isPlaying 轮询包装为现代动作；不依赖 AudioKit，也不接管音频资源。" }
        };

        /// <summary>查找已整理的类型说明；未收录类型返回 null，不凭名称推断契约。</summary>
        /// <param name="type">待描述的非 null 类型。</param>
        internal static string TypeSummary(Type type)
        {
            if (type == null)
            {
                throw new ArgumentNullException(nameof(type));
            }

            var key = Key(type);
            return _typeDescriptions.TryGetValue(key, out var summary) ? ActionKitApiHelpContent.FormatType(type) + "：" + summary : null;
        }

        /// <summary>为类型选择帮助分组，并单列尚未核实说明的公开类型。</summary>
        /// <param name="type">待分组的非 null 类型。</param>
        internal static string TypeGroup(Type type)
        {
            if (type == null)
            {
                throw new ArgumentNullException(nameof(type));
            }

            switch (Key(type))
            {
                case "ActionKit":
                case "IAction":
                case "AbstractAction":
                case "ActionStatus":
                case "ICustomApi":
                    return Entry;
                case "IActionController":
                case "ActionController":
                case "ActionHandle":
                case "IActionControllerExtensions":
                case "IActionExtensions":
                case "IActionExecutor":
                case "IActionExecutorExtensions":
                case "ActionUpdateModes":
                case "MonoUpdateActionExecutorExtension":
                case "ActionKitCurrentScene":
                    return Execution;
                case "Lerp":
                case "LerpExtension":
                case "EaseUtility":
                case "EaseType":
                case "ActionKit.ScreenTransition":
                case "ScreenTransitionFade":
                case "ScreenTransitionInOut":
                case "AudioSourceShortCutExtensions":
                    return Tools;
                default:
                    return _typeDescriptions.ContainsKey(Key(type)) ? Actions : "其他公开类型（说明待核实）";
            }
        }

        /// <summary>组合成员摘要、参数、返回值及限制；未接管的成员交由共享帮助层读取源码 XML。</summary>
        /// <param name="member">待描述的非 null 类型或成员元数据。</param>
        internal static MemberHelp Describe(MemberInfo member)
        {
            if (member == null)
            {
                throw new ArgumentNullException(nameof(member));
            }

            if (member is Type type)
            {
                return new MemberHelp(TypeSummary(type), null, null, null);
            }
            // 命名执行契约直接采用 Runtime XML，避免套用 Start 的池控制器返回值说明。
            if (member.DeclaringType == typeof(ActionHandle) ||
                ((member.DeclaringType == typeof(ActionKit) || member.DeclaringType == typeof(IActionExtensions)) &&
                 member.Name.Contains("Named")))
            {
                return MemberHelp.Empty;
            }

            if (!_typeDescriptions.ContainsKey(Key(member.DeclaringType)))
            {
                return MemberHelp.Empty;
            }

            var method = member as MethodBase;
            var property = member as PropertyInfo;
            var parameters = method != null ? method.GetParameters() : property != null ? property.GetIndexParameters() : Array.Empty<ParameterInfo>();
            var help = new List<ParameterHelp>();
            foreach (var parameter in parameters)
            {
                var description = ParameterDescription(member, parameter);
                if (!string.IsNullOrWhiteSpace(description))
                {
                    help.Add(new ParameterHelp(parameter.Name, description));
                }
            }
            return new MemberHelp(Summary(member), ReturnDescription(member), Remarks(member), help.ToArray());
        }

        /// <summary>为支持的 Action 类型生成说明表键，排除无关类型并保留嵌套层级。</summary>
        /// <param name="type">待识别类型，可为 null。</param>
        private static string Key(Type type)
        {
            if (type == null || (type.Namespace != "YLFramework.Actions" &&
                type.FullName is not ("YLFramework.Integrations.DOTween.DOTweenAction" or
                    "YLFramework.Integrations.DOTween.ActionKitDOTweenExtension")))
            {
                return "";
            }

            var name = type.Name.Split('`')[0];
            return type.IsNested ? Key(type.DeclaringType) + "." + name : name;
        }

        /// <summary>判断类型是否使用此说明表中的现代动作生命周期模板。</summary>
        /// <param name="type">待识别类型。</param>
        private static bool Modern(Type type)
        {
            switch (Key(type))
            {
                case "IAction":
                case "ISequence":
                case "IParallel":
                case "IRepeat":
                case "AbstractAction":
                case "Delay":
                case "DelayFrame":
                case "Lerp":
                case "CallbackAction":
                case "ConditionAction":
                case "ConditionSequence":
                case "Sequence":
                case "Parallel":
                case "Repeat":
                case "Custom":
                case "CoroutineAction":
                case "TaskAction":
                case "UniRxAction":
                case "DOTweenAction":
                case "ScreenTransitionFade":
                case "ScreenTransitionInOut":
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>判断说明表键是否对应现代动作工厂或组合扩展。</summary>
        /// <param name="key">类型说明表键。</param>
        private static bool ModernFactory(string key)
        {
            switch (key)
            {
                case "ActionKit":
                case "DelayExtension":
                case "DelayFrameExtension":
                case "LerpExtension":
                case "CallbackExtension":
                case "ConditionExtension":
                case "ConditionSequenceExtension":
                case "SequenceExtension":
                case "ParallelExtension":
                case "RepeatExtension":
                case "CustomExtension":
                case "CoroutineExtension":
                case "TaskExtension":
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>按成员类别和已整理规则选择摘要，无法匹配时不补造说明。</summary>
        /// <param name="member">待描述的成员元数据。</param>
        private static string Summary(MemberInfo member)
        {
            var type = member.DeclaringType;
            var key = Key(type);
            var name = member.Name;
            if (member is ConstructorInfo)
            {
                return ConstructorSummary(type);
            }

            if (member is FieldInfo || member is PropertyInfo)
            {
                return StorageSummary(member);
            }

            if (member is Type nested)
            {
                return TypeSummary(nested);
            }

            if (member is EventInfo)
            {
                return null;
            }

            if (key == "ActionKitUniRxExtension")
            {
                return name == "ToAction" ? "把已有 BCL IObservable<T> 包装为尚未启动的 IAction；每次启动订阅同一 source。" :
                    "向当前 ISequence 追加 Observable 工厂动作，在该子动作启动时取得 source 并订阅。";
            }

            if (key == "ActionKitDOTweenExtension")
            {
                return name == "ToAction" ? "先校验真实 Unity 主线程及原始上下文，在 Pause 前预绑定原 Tween 生命周期一次，再立即 Pause；启动只复用该绑定，不认领原对象复租后的新生命周期。" :
                    "向当前 ISequence 追加 DOTween 工厂动作，在该子动作每次启动时取得一个独立根 Tween 并接管其执行与取消。";
            }

            if (key == "DOTweenAction")
            {
                switch (name)
                {
                    case "Allocate":
                        return "从池取得 DOTweenAction，分配 ActionId、清除 IsDeinitialized 并重置状态，保存非空 tweenGetter；此时不调用 getter。";
                    case "OnStart":
                        return "首先校验真实 Unity 主线程及原始 UnitySynchronizationContext。工厂模式调用 tweenGetter 恰一次，接管根 Tween 后 Restart；ToAction 模式只复用预绑定，绑定已结束则取消，不调用 getter 重新认领。";
                    case "OnExecute":
                        return "轮询当前 Tween 的完成及 ownership；外部 Pause 只等待，首个有效完成标记成功，Kill(false) 或失去 ownership 取消，不按 dt 推进 Tween。";
                    case "OnFinish":
                        return "仅 detach hooks 并解除当前 Tween 租约，不 Kill 已完成且 autoKill=false 的 Tween；保留 getter 到 Deinit。";
                    case "Reset":
                        return "更新内部执行代次，立即恢复 NotStart 和非暂停状态；保留 getter，不清除 IsDeinitialized。ToAction 首次启动前 Reset 保留同一预绑定，供组合准备，不重新认领其他 Tween；执行开始后结束旧绑定并保留 pending marker，通过原上下文 Post 延后复核 ownership 再 Kill(false)，包括 paused Tween；绑定结束后再 Start 会取消。";
                    case "Deinit":
                        return "首次调用立即标记 IsDeinitialized，清空 getter、断开动作与 Tween 的关联；保留 pending marker，通过原上下文 Post 延后复核 ownership 再 Kill(false)，包括 paused Tween。即使安排清理抛错仍安排延后回池，不通知成功，也不保证同步物理清理。";
                }
            }
            if (ModernFactory(key))
            {
                if (name == "ToAction")
                {
                    return key == "CoroutineExtension" ? "把已有 IEnumerator 包装为尚未启动的现代 IAction，工厂仍引用同一个枚举器。" : "把已有 Task 包装为尚未启动的现代 IAction，不新建或取消该 Task。";
                }

                var operation = FactoryOperation(name);
                if (operation != null)
                {
                    return (key == "ActionKit" ? "配置并取得" : "向当前现代组合追加") + operation + "。";
                }
            }
            if (Modern(type))
            {
                switch (name)
                {
                    case "Allocate":
                        return "从动作池取得并配置 " + ActionKitApiHelpContent.FormatType(type) + "，分配本次 ActionId、清除 IsDeinitialized 并重置执行状态；尚未启动。";
                    case "OnStart":
                        return ModernStart(key);
                    case "OnExecute":
                        return ModernExecute(key);
                    case "OnFinish":
                        return key == "IAction" ? "正常完成钩子的契约；具体通知及资源处理由实现决定。" : key == "UniRxAction" ? "释放本次 subscription，保留 factory 到 Deinit，便于 Reset 后由 Repeat 重新建立订阅。" : key == "ScreenTransitionFade" ? "把共享屏幕图像透明度设置为目标值。" : key == "ConditionSequence" ? "释放内部已完成的分支序列并清空引用。" : "正常完成阶段钩子。此类型的基础实现为空，派生实现可定义正常完成处理。";
                    case "OnReset":
                        return "派生动作的重置钩子；基类实现为空，不负责池回收。";
                    case "OnDeinit":
                        return "派生动作的释放钩子；基类实现为空，由派生类清理其拥有的数据或资源。";
                    case "Deinit":
                        return key == "IAction" ? "释放动作的契约入口；具体清理、取消及回收方式由实现决定。" : key == "UniRxAction" ? "标记已释放，清空 factory 引用并释放 subscription；即使 Dispose 抛错仍安排延后回池，不通知成功。" : "标记本次动作已释放并执行本类型清理，安排延后回池；不是正常完成通知。";
                    case "Reset":
                        return ModernReset(key);
                    case "Append":
                        return key == "ISequence" ? "向现代组合追加 IAction 并返回 ISequence 的契约；子动作所有权和推进算法由实现决定。" : "把 IAction 加入该组合的子动作集合；Repeat 添加到内部序列，其他组合按自身算法推进。";
                }
            }
            switch (key)
            {
                case "ActionKit":
                    switch (name)
                    {
                        case "Initialize":
                            return "初始化模块实例，调用 Init 和 OnRegisterPatch；已有实例时直接返回。";
                        case "InitArchitecture":
                            return "Initialize 的同义入口，初始化当前 ActionKit 模块，不创建通用服务容器。";
                        case "Init":
                            return "模块实例初始化钩子；基础实现为空，派生类可以覆盖。";
                        case "OnDeinit":
                            return "模块实例关闭钩子；基础实现为空，在 Shutdown 清理过程中调用。";
                        case "Deinit":
                            return "仅当此对象是当前模块实例时关闭模块；不释放任意外部对象。";
                        case "Shutdown":
                            return "关闭模块管理的动作执行、协程、延迟回收队列、订阅和模块拥有的宿主/屏幕资源。";
                    }
                    break;
                case "IActionExtensions":
                    switch (name)
                    {
                        case "Finish":
                            return "只将现代动作 Status 标记为 Finished；不在此调用结束钩子或回收。";
                        case "Execute":
                            return "在同一分配租约内手动推进现代动作；仍存活且到达 Finished 时调用 OnFinish。回调中取消后再标记 Finished 可返回终止结果，但不会调用结束钩子或成功回调。";
                        case "Start":
                            return "在 MonoBehaviour 所属 GameObject 的 Update 执行器上启动动作，首先同步推进 Execute(0)。";
                        case "StartCurrentScene":
                            return "在当前场景共享宿主上启动动作，所属场景卸载时随执行器销毁取消。";
                        case "StartGlobal":
                            return "在模块全局常驻宿主上启动动作，普通场景卸载不会结束该执行。";
                        case "Pause":
                            return "当控制器 ActionId 与关联动作匹配时，将动作 IsPaused 设为 true。";
                        case "Resume":
                            return "当控制器 ActionId 与关联动作匹配时，将动作 IsPaused 设为 false。";
                    }
                    break;
                case "IActionController":
                case "ActionController":
                    switch (name)
                    {
                        case "Allocate":
                            return "从池取得现代控制器并更新本次使用代次；调用方仍需设置 Action、ActionId 和执行模式。";
                        case "Reset":
                            return key == "IActionController" ? "重置关联执行的契约；具体范围由控制器实现决定。" : "标识匹配时调用关联动作 Reset，不回收控制器。";
                        case "Deinit":
                            return key == "IActionController" ? "清理关联执行的契约；取消方式由实现决定。" : "标识匹配时调用关联动作 Deinit，取消不触发执行器成功回调。";
                        case "Recycle":
                            return key == "IActionController" ? "回收控制器的契约；具体池和资源策略由实现决定。" : "把此控制器归还池并清空关联信息；不会替代 Action.Deinit。";
                    }
                    break;
                case "IActionControllerExtensions":
                    return "将控制器 UpdateMode 设为 UnscaledDeltaTime，使用未缩放 dt 推进动作。";
                case "IActionExecutor":
                    return "把控制器及正常完成回调交给执行器；接口不规定具体调度算法。";
                case "IActionExecutorExtensions":
                    return "检查关联动作及标识，推进有效动作；正常完成时通知回调并尝试 Deinit，失效或取消直接结束跟踪。";
                case "MonoUpdateActionExecutorExtension":
                    return "在对象上获取或添加可用的 Update 执行器，并提交指定控制器；返回传入的 action。";
                case "ICustomApi":
                    switch (name)
                    {
                        case "OnStart":
                            return "设置启动回调并返回配置接口；标准实现替换此前回调。";
                        case "OnExecute":
                            return "设置接收 dt 的推进回调并返回配置接口；返回回调本身不会完成动作。";
                        case "OnFinish":
                            return "设置正常完成回调并返回配置接口；不是取消清理回调。";
                        case "Finish":
                            return "显式标记自定义动作正常完成，供执行器后续处理完成阶段。";
                    }
                    break;
                case "EaseUtility":
                    return "按" + Curve(name) + "计算 start 与 end 之间的数值，不创建或启动动作。";
                case "ActionKit.ScreenTransition":
                    if (name == "FadeIn")
                    {
                        return "配置黑色、1 秒、透明度从 0 到 1 的屏幕淡入动作，尚未启动。";
                    }

                    if (name == "FadeOut")
                    {
                        return "配置黑色、1 秒、透明度从 1 到 0 的屏幕淡出动作，尚未启动。";
                    }

                    return "取得包含默认 FadeIn 和 FadeOut 的进出包装动作，尚未启动。";
                case "ScreenTransitionFade":
                    if (name == "Color")
                    {
                        return "设置屏幕过渡使用的颜色，在动作启动时应用到共享 Image。";
                    }

                    if (name == "FromAlpha")
                    {
                        return "设置动作启动时使用的透明度。";
                    }

                    if (name == "ToAlpha")
                    {
                        return "设置插值目标和正常结束时使用的透明度。";
                    }

                    if (name == "Duration")
                    {
                        return "设置透明度插值的持续秒数，推进使用执行器 dt。";
                    }

                    break;
                case "ScreenTransitionInOut":
                    if (name == "In")
                    {
                        return "设置启动时配置进入动作的回调，替换此前配置。";
                    }

                    if (name == "Out")
                    {
                        return "设置启动时配置退出动作的回调，替换此前配置。";
                    }

                    if (name == "OnInFinish")
                    {
                        return "设置全局子序列中进入动作之后、退出动作之前的回调。";
                    }

                    break;
                case "AudioSourceShortCutExtensions":
                    return "配置在启动时 AudioSource.Play、推进时检查 isPlaying 的动作；检测到有效音源停止播放才 Finish。";
            }
            return null;
        }

        /// <summary>将已知工厂方法名映射为操作说明。</summary>
        /// <param name="name">工厂或扩展方法名。</param>
        private static string FactoryOperation(string name)
        {
            switch (name)
            {
                case "Delay":
                    return "按秒累计的延时动作（时间工厂在启动时求值一次）";
                case "DelayFrame":
                    return "基于 Unity 全局帧计数的等待动作";
                case "NextFrame":
                    return "等待 Unity 全局帧计数增加一帧的动作";
                case "Callback":
                    return "启动时执行回调并立即完成的动作";
                case "Condition":
                    return "执行阶段等待谓词成立的动作";
                case "ConditionSequence":
                    return "启动时检查一次条件的分支序列";
                case "Sequence":
                    return "按追加顺序推进的序列";
                case "Parallel":
                    return "全部子动作完成才完成的并行组（非多线程）";
                case "Repeat":
                    return "重复执行内部序列的动作";
                case "Custom":
                    return "由用户回调和显式 Finish 控制的自定义动作";
                case "Lerp":
                    return "从 a 到 b 的线性插值动作";
                case "Lerp01":
                    return "从 0 到 1 的线性插值动作";
                case "Coroutine":
                    return "由 IEnumerator 工厂提供协程的动作";
                case "Task":
                    return "观察 Task 工厂结果的动作";
                case "ToAction":
                    return "包装已有异步对象的动作（不启动新一份对象）";
                default:
                    return null;
            }
        }

        /// <summary>取得指定动作类型的启动钩子说明。</summary>
        /// <param name="key">类型说明表键。</param>
        private static string ModernStart(string key)
        {
            switch (key)
            {
                case "IAction":
                    return "动作启动钩子的契约；调用时机由执行器决定，具体行为由实现决定。";
                case "Lerp":
                    return "清零累计时间并调用 OnLerp 发送起点 A。";
                case "ConditionAction":
                    return "启动钩子为空，不在这里判断条件。";
                case "ConditionSequence":
                    return "只检查一次条件，成立则取得并配置分支序列，否则标记完成。";
                case "Repeat":
                    return "清零轮数并检查内层序列是否已取消，不在此推进第一轮。";
                case "TaskAction":
                    return "调用 taskGetter 并开始观察 Task，成功标记完成，失败或任务取消执行 Deinit。";
                case "UniRxAction":
                    return "调用 observableGetter 取得 source 并订阅；首个 OnNext 即时标记成功，空 OnCompleted 或 OnError 取消，错误通知会记录异常。";
                case "ScreenTransitionFade":
                    return "取得共享屏幕 Image，应用配置颜色和起始透明度，并清零累计时间。";
                case "ScreenTransitionInOut":
                    return "调用进入/退出配置回调，另建全局序列依次执行进入、期间回调和退出；不会把子序列结束桥接为本动作完成。";
                case "Delay":
                    return "若配置了 DelayTimeFactory，在启动时求值一次并更新延时秒数。";
                case "DelayFrame":
                    return "记录当前 Time.frameCount 作为等待的起点。";
                case "CallbackAction":
                    return "调用配置回调后将 Status 标记为 Finished。";
                case "Sequence":
                    return "启动首个子动作，并以 Execute(0) 越过连续即时动作；空序列立即完成。";
                case "Parallel":
                    return "检查子动作是否已取消，不在此推进子动作。";
                case "Custom":
                    return "调用配置的启动回调，不自动完成动作。";
                case "CoroutineAction":
                    return "取得 IEnumerator 并在模块协程宿主上运行，保存本动作专属句柄。";
                case "AbstractAction":
                    return "现代动作启动虚钩子；基类实现为空，派生类决定启动行为。";
                default:
                    return null;
            }
        }

        /// <summary>取得指定动作类型的推进钩子说明。</summary>
        /// <param name="key">类型说明表键。</param>
        private static string ModernExecute(string key)
        {
            switch (key)
            {
                case "IAction":
                    return "动作推进钩子的契约，接收 dt；是否及何时完成由实现决定。";
                case "Lerp":
                    return "累计 dt，调用 OnLerp 发送插值值；达到 Duration 后标记完成、发送终点并调用 OnLerpFinish。";
                case "ConditionAction":
                    return "检查谓词，成立时调用 onCondition 并标记完成。";
                case "ConditionSequence":
                    return "推进分支序列，正常结束时标记本动作完成，内层取消时取消本动作。";
                case "Repeat":
                    return "推进内部序列，每轮结束后按次数重置或完成；内层取消则取消本动作。";
                case "TaskAction":
                case "CoroutineAction":
                    return "此推进钩子为空；完成由异步观察或协程结束回调更新，不通过累计 dt 等待。";
                case "UniRxAction":
                    return "在订阅拥有线程消费尚未处理的首个通知；没有捕获 SynchronizationContext 的后台通知由此处理，不按 dt 计时。";
                case "ScreenTransitionFade":
                    return "累计 dt 并更新共享 Image 透明度，达到配置持续时间后标记完成。";
                case "ScreenTransitionInOut":
                case "AbstractAction":
                    return "继承的推进虚钩子基础实现为空；没有额外的自动完成逻辑。";
                case "Delay":
                    return "先比较已累计秒数与 DelayTime，满足时完成并回调，然后加入本次 dt。";
                case "DelayFrame":
                    return "比较全局帧计数与启动帧加等待帧数，不使用 dt 计数。";
                case "CallbackAction":
                    return "推进钩子为空，回调在启动时已经执行。";
                case "Sequence":
                    return "推进当前子动作，完成后以 Execute(0) 启动后继，取消的子动作会取消组合。";
                case "Parallel":
                    return "推进未完成子动作并累计完成数，全部结束后完成；取消子动作会取消组合。";
                case "Custom":
                    return "把 dt 传给配置的执行回调；由用户调用 Finish 决定完成。";
                default:
                    return null;
            }
        }

        /// <summary>按类型选择重置行为说明，区分接口契约和具体实现。</summary>
        /// <param name="key">类型说明表键。</param>
        private static string ModernReset(string key)
        {
            if (key == "IAction")
            {
                return "重置动作的契约；重置数据及能否再次执行由实现决定。";
            }

            if (key == "AbstractAction")
            {
                return "恢复 NotStart 和非暂停状态，然后调用派生类 OnReset；不清除 IsDeinitialized 或回收对象。";
            }

            if (key == "ConditionSequence")
            {
                return "释放已有分支序列，清空分支引用，恢复 NotStart 和非暂停状态；保留条件及配置回调。";
            }

            if (key == "TaskAction")
            {
                return "使此前 Task 观察失效，恢复 NotStart 和非暂停状态；不取消或 Dispose Task。";
            }

            if (key == "UniRxAction")
            {
                return "恢复 NotStart 和非暂停状态，释放旧 subscription 并隔离旧通知；保留 factory，供 Repeat 下一轮重新取得 source，不清除 IsDeinitialized。";
            }

            if (key == "CoroutineAction")
            {
                return "停止本动作拥有的协程，恢复 NotStart 和非暂停状态。";
            }

            if (key == "Repeat" || key == "Sequence" || key == "Parallel")
            {
                return "清零组合执行进度并重置内部子动作，恢复 NotStart 和非暂停状态；不是清空子动作集合。";
            }

            return "恢复 NotStart 和非暂停状态，并重置本类型执行进度；不清除 IsDeinitialized，也不替代重新 Allocate。";
        }

        /// <summary>取得已核对类型的构造入口说明。</summary>
        /// <param name="type">声明构造器的类型。</param>
        private static string ConstructorSummary(Type type)
        {
            switch (Key(type))
            {
                case "ActionKit":
                    return "构造模块实例；直接构造不会执行 Initialize 或 OnRegisterPatch。通常使用模块静态入口。";
                case "AbstractAction":
                    return "供派生类构造动作；不会替代 Allocate 的标识分配和重置流程。";
                case "ActionController":
                    return "构造尚未关联动作的控制器；正常启动路径使用 Allocate 配合执行器管理生命周期。";
                case "ScreenTransitionFade":
                    return "构造屏幕淡变动作，初始颜色黑色、透明度端点均为 0、时长 1 秒；没有分配池租用标识。";
                case "ScreenTransitionInOut":
                    return "构造尚未绑定进入/退出子动作的包装对象，使用带子动作参数的 Allocate 配置。";
                case "Lerp":
                    return "构造尚未配置端点及回调的插值动作；正常工厂使用 Allocate 初始化本次执行。";
                case "EaseUtility":
                    return "构造无额外初始化行为的缓动工具对象；计算函数为 static，无需构造实例。";
                case "ActionKitCurrentScene":
                    return "Unity 组件的构造入口；组件应由 Unity 挂载到 GameObject，不通过普通 new 启动宿主。";
                default:
                    return null;
            }
        }

        /// <summary>取得字段、属性或枚举成员的说明，不读取其实例值。</summary>
        /// <param name="member">待描述的存储成员元数据。</param>
        private static string StorageSummary(MemberInfo member)
        {
            var key = Key(member.DeclaringType);
            var name = member.Name;
            if (member.DeclaringType.IsEnum)
            {
                if (key == "ActionStatus")
                {
                    if (name == "NotStart")
                    {
                        return "尚未启动的现代动作状态，下一次 Execute 调用 OnStart。";
                    }

                    if (name == "Started")
                    {
                        return "已启动的现代动作状态，非暂停时后续 Execute 调用 OnExecute。";
                    }

                    return "现代动作正常完成标记，Execute 发现此状态时调用 OnFinish；不等于 IsDeinitialized。";
                }
                if (key == "ActionUpdateModes")
                {
                    return name == "ScaledDeltaTime" ? "使用 Time.deltaTime 推进现代动作。" : "使用 Time.unscaledDeltaTime 推进现代动作，不暂停或改写外部异步时钟。";
                }

                if (key == "EaseType")
                {
                    return name == "Count" ? "曲线数量哨兵，不是可执行的缓动曲线。" : "表示" + Curve(name) + "；枚举本身不执行插值。";
                }
            }
            if (Modern(member.DeclaringType))
            {
                if (key == "IAction")
                {
                    if (name == "ActionId")
                    {
                        return "动作标识的读写契约，供控制器匹配；具体生成与赋值行为由实现决定。";
                    }

                    if (name == "Status")
                    {
                        return "TStatus 状态的读写契约，是否存在额外存取行为由实现决定；标准 IAction 使用 ActionStatus。";
                    }

                    if (name == "IsPaused")
                    {
                        return "暂停标记的读写契约，暂停影响范围由执行器及具体动作实现决定。";
                    }

                    if (name == "IsDeinitialized")
                    {
                        return "动作释放标记的读写契约，赋值与 Deinit 的清理关系由实现决定。";
                    }
                }
                switch (name)
                {
                    case "ActionId":
                        return "本次动作使用的标识，供控制器/执行器匹配；直接修改可能破坏租用校验，不是取消接口。";
                    case "Status":
                        return "保存现代动作当前执行状态；仅赋值不会立即调用生命周期钩子或完成回调。";
                    case "IsPaused":
                        return key == "DOTweenAction" ? "保存动作暂停状态并转发到仍 owned 的 Tween：true 只暂停正在播放者，false 只恢复由此动作暂停的 Tween；外部 Pause 本身不表示成功。" : key == "UniRxAction" ? "保存动作推进暂停标记；不暂停 source，主线程及通过上下文投递的通知仍可标记完成或取消。" : "保存动作暂停标记；标准 Execute 在 Started 阶段跳过推进，不能暂停 Task、Unity 协程或全局 frameCount。";
                    case "IsDeinitialized":
                        return "保存本次动作已释放标记；直接赋值不执行 Deinit 清理或池回收。";
                }
            }
            switch (key)
            {
                case "ActionKit":
                    switch (name)
                    {
                        case "IdGenerator":
                            return "动作工厂使用的全局递增标识计数器；Shutdown 不重置它。直接修改会影响后续标识。";
                        case "IsInitialized":
                            return "读取模块初始化及注册补丁已成功完成的标记，读取本身不触发 Initialize。";
                        case "OnRegisterPatch":
                            return "可替换的模块注册补丁委托，在每次新模块实例初始化的 Init 之后执行；Shutdown 不清空它。";
                        case "Interface":
                            return "取得当前 ActionKit 模块实例；运行时代码读取会调用 Initialize，只返回模块自身，不提供跨模块服务定位。帮助页仅检查元数据。";
                        case "Architecture":
                            return "派生模块可见的当前 ActionKit 实例引用，是模块生命周期状态，不是通用服务容器。";
                        case "OnUpdate":
                            return "取得全局宿主 Unity Update 消息的本地事件，运行时 getter 可能创建宿主。";
                        case "OnFixedUpdate":
                            return "取得全局宿主 Unity FixedUpdate 消息的本地事件，不等于为动作切换固定时间执行器。";
                        case "OnLateUpdate":
                            return "取得全局宿主 Unity LateUpdate 消息的本地事件。";
                        case "OnGUI":
                            return "取得全局宿主 Unity OnGUI 消息的本地事件；不是仅每帧一次的动作推进契约。";
                        case "OnApplicationQuit":
                            return "取得全局宿主收到应用退出消息时触发的本地事件。";
                        case "OnApplicationFocus":
                            return "取得应用焦点消息事件，bool 原样传递 Unity 的 hasFocus，不是动作暂停状态。";
                        case "OnApplicationPause":
                            return "取得应用暂停消息事件，bool 原样传递 Unity 的 pauseStatus，不会自动暂停全部动作。";
                    }
                    break;
                case "IActionController":
                case "ActionController":
                    if (key == "IActionController")
                    {
                        return "控制器 " + name + " 的读写契约；Action/ActionId 表示关联动作与标识，具体存取及控制副作用由实现决定。";
                    }

                    switch (name)
                    {
                        case "ActionId":
                            return "保存控制器所关联的动作标识，用于与 Action.ActionId 校验，不是新标识生成器。";
                        case "Action":
                            return "保存关联的 IAction 引用；赋值本身不启动、取消或释放该动作。";
                        case "UpdateMode":
                            return "选择执行器使用的 dt 模式；直接设置不会更改 Task 或协程的时钟。";
                        case "IsPaused":
                            return key == "ActionController" ? "读取/设置标识匹配的关联动作 IsPaused；无有效关联时读取 false，设置不生效。" : "控制器暂停状态的读写契约；实际影响范围由实现决定。";
                    }
                    break;
                case "ICustomApi":
                    if (name == "Data")
                    {
                        return "读取或设置自定义动作的 TData；标准实现释放时清空它，不 Dispose 用户数据。";
                    }
                    break;
                case "Lerp":
                    switch (name)
                    {
                        case "A":
                            return "插值起点，启动时通过 OnLerp 发送。";
                        case "B":
                            return "插值终点，达到 Duration 时通过 OnLerp 发送。";
                        case "Duration":
                            return "插值持续秒数，使用执行器传入的 dt 累计。";
                        case "OnLerp":
                            return "接收当前插值数值的回调，不是读取进度的 getter。";
                        case "OnLerpFinish":
                            return "到达插值终点时的回调；取消不作为成功到达终点。";
                    }
                    break;
                case "ActionKitCurrentScene":
                    return name == "SceneComponent" ? "取得当前场景共享宿主，运行时 getter 会按需创建 GameObject；帮助页不调用它。" : "保存当前场景宿主引用，不是场景资源加载器。";
            }
            return null;
        }

        /// <summary>将缓动名称拆为曲线族与缓入缓出阶段的展示文本。</summary>
        /// <param name="name">缓动方法名或枚举成员名。</param>
        private static string Curve(string name)
        {
            if (name == "Linear")
            {
                return "线性曲线";
            }

            var phase = name.StartsWith("InOut", StringComparison.Ordinal) || name.StartsWith("Inout", StringComparison.Ordinal) ? "先缓入再缓出" : name.StartsWith("Out", StringComparison.Ordinal) ? "缓出" : "缓入";
            var family = name.EndsWith("Back", StringComparison.Ordinal) ? "回摆" : name.EndsWith("Bounce", StringComparison.Ordinal) ? "弹跳" :
                name.EndsWith("Circle", StringComparison.Ordinal) ? "圆形" : name.EndsWith("Cubic", StringComparison.Ordinal) ? "三次多项式" :
                name.EndsWith("Elastic", StringComparison.Ordinal) ? "弹性振荡" : name.EndsWith("Expo", StringComparison.Ordinal) ? "指数" :
                name.EndsWith("Quad", StringComparison.Ordinal) ? "二次多项式" : name.EndsWith("Quart", StringComparison.Ordinal) ? "四次多项式" :
                name.EndsWith("Quint", StringComparison.Ordinal) ? "五次多项式" : "正弦";
            return family + "曲线（" + phase + "）";
        }

        /// <summary>为已收录成员补充生命周期、所有权和实现限制说明。</summary>
        /// <param name="member">待描述的成员元数据。</param>
        private static string Remarks(MemberInfo member)
        {
            if (member is Type)
            {
                return null;
            }

            var key = Key(member.DeclaringType);
            var name = member.Name;
            switch (key)
            {
                case "ActionKit":
                    if (name == "Shutdown")
                    {
                        return "执行回调内请求关闭会延后到执行作用域退出再完成清理，不应立即重新初始化。保留用户 GameObject 和无关协程；清理错误可见。IdGenerator 与 OnRegisterPatch 不重置。";
                    }

                    if (name == "Initialize" || name == "InitArchitecture" || name == "Interface")
                    {
                        return "OnRegisterPatch 在每次新模块实例初始化时执行一次，初始化失败会清空实例。初始化补丁内禁止 Shutdown；不是 System/Model/Utility 注册容器。";
                    }

                    if (name == "Custom")
                    {
                        return "customSetting 在工厂调用期间同步执行；动作尚未启动。回调设置器替换此前委托，OnFinish 不负责取消清理。";
                    }

                    if (name.StartsWith("On", StringComparison.Ordinal) && member is PropertyInfo)
                    {
                        return "运行时代码读取事件 getter 可能创建全局宿主；帮助页从不调用 getter。显式管理注销句柄的生命周期，这些是模块范围的 Unity 消息事件。";
                    }

                    break;
                case "IActionExtensions":
                    if (name == "Execute")
                    {
                        return "推进前后复核 IsDeinitialized 和分配 ID；支持重置代次的动作还复核该代次，旧调用栈不再写入复租或重置后的动作。同一租约在回调中取消后又到达 Finished 时仍返回 true，但不调用结束钩子；执行器复核取消后也不会调用成功回调。此方法不负责回收，返回值不能替代取消状态检查。";
                    }

                    if (name == "Start" || name == "StartCurrentScene" || name == "StartGlobal")
                    {
                        return "同步完成的动作可能在 Start 返回前就释放控制器。动作与控制器都是池化原始引用，结束后不要作为长期句柄保留。同一有效动作不能重复启动。对象上的宿主销毁会取消；停用调用方单独的组件不等于取消，GameObject 不活跃会停止自动 Update。";
                    }

                    if (name == "Pause" || name == "Resume")
                    {
                        return "设置具体动作的 IsPaused；不停止 Task 或 Unity 协程，也不冻结全局 Time.frameCount。DOTweenAction 还转发 Tween 暂停，Resume 仅恢复由动作暂停者。动作/控制器回收后不要继续操作旧引用。";
                    }

                    break;
                case "ActionController":
                case "IActionController":
                    return "ActionId/Action 属于关联执行信息，不应随意篡改。Recycle 是基础设施回收而不是普通取消；标准实现的池化引用在结束后可能被复用。接口本身不承诺其他实现采用同一资源策略。";
                case "IActionControllerExtensions":
                    return "只选择执行器 dt，不改变 WaitForSeconds、Task.Delay、Unity 全局帧计数或 DOTween 自己的时钟。";
                case "IActionExecutor":
                    return "这是调度契约，不保证所有实现使用 Update、主线程或相同异常策略。";
                case "MonoUpdateActionExecutorExtension":
                    return "controller 必须已关联实际要执行的动作且 ActionId 匹配；该方法不会替调用方设置关联。参数 action 是返回值来源，实际调度读取 controller.Action。";
                case "TaskAction":
                case "TaskExtension":
                    return "任务失败或取消不会触发执行成功回调。动作取消只使旧观察失效，不取消或 Dispose 外部 Task；暂停不暂停任务。ToAction 包装同一个 Task，重复执行不重新创建任务。";
                case "DOTweenAction":
                case "ActionKitDOTweenExtension":
                    var dotween = "需要 YLFramework.Actions.DOTween 和实际 DG.Tweening.Tween 所在的 DOTween.dll；当前安装版本 1.2.825。" +
                        "只传独立根 Tween 或 DG.Tweening.Sequence 本身，不传 nested Sequence 的内部子 Tween；库无公开 isSequenced 查询且忽略子 Tween 独立控制。" +
                        "所有动作和 Tween 操作在 Unity 主线程。ToAction/OnStart 要求原始 UnitySynchronizationContext，校验完整类型名、UnityEngine 程序集身份、sealed 类型及初始化时记录的主线程 ID；" +
                        "不支持自定义同步 Post 或人工 pump。非法上下文在 factory/Pause 前抛错，不调用 factory、不 Pause Tween。" +
                        "SetUpdate(Manual/independent) 由 Tween 自行选择，IgnoreTimeScale 不改变 Tween clock。" +
                        "首个有效完成（含 Complete()/Kill(true) 触发完成）成功，外部 Pause 等待，Kill(false) 取消。" +
                        "onComplete/onKill 只 += 自身 callback，不覆盖原 source callbacks；onKill marker 丢失或被替换则保守取消并放弃 Kill，不保证物理清理。" +
                        "公开 API 没有唯一租期身份，不能完全区分已 kill 后复租和用户覆写 marker；onPause 不清理，不能当 ownership marker。" +
                        "前置用户 callback 抛错时 safeMode 对整个 multicast 捕获可能跳过 adapter 并漏掉完成或取消信号，不承诺异常隔离。" +
                        "Deinit/Reset 立即改变逻辑状态，物理 Kill(false) 通过捕获上下文 Post 延后；待清理期间保留 pending marker 阻止重复认领，" +
                        "执行时复核实际主线程、active、原 marker 及其他 owner。Shutdown 不保证同步物理 Kill；Domain Reload 或退出前 Post 未执行时可能没有物理清理。";
                    if (name == "Allocate" || name == "DOTween" || name == "OnStart")
                    {
                        return dotween +
                        " 工厂模式 getter 每次 OnStart 恰一次，必须返回非空、未 kill 且无活动/待清理 ownership marker 的根 Tween，返回对象的执行与取消责任交给动作。" +
                        "getter/校验/Restart 抛错时取消并向调用方抛出；清理也抛错则保留 AggregateException。Repeat 应由 getter 每轮创建新 Tween。" +
                        "ToAction 模式不调用 getter 重新认领；预绑定已结束时启动取消。";
                    }

                    if (name == "ToAction")
                    {
                        return dotween + " 在 Pause 前预绑定原生命周期一次；启动前已 kill/复租时不认领新生命周期。" +
                        "Reset 结束该绑定，再 Start 会取消；不为旧 Tween 创建新实例，Repeat 请使用每轮创建新 Tween 的工厂。";
                    }

                    return dotween + " 延后 Kill(false) 只作用于仍 owned 的 Tween，包含 paused；OnFinish 仅 detach hooks 不 Kill，" +
                        "autoKill=false 完成后由 source owner 管理该 Tween 后续生命周期。Reset 内部 version 仅保护旧 callback/调用栈重入，不是公共 API；池化动作回收后不可继续操作旧引用。";
                case "UniRxAction":
                case "ActionKitUniRxExtension":
                    var observable = "内建于 YLFramework.Runtime，仅依赖 BCL IObservable<T>，不要求第三方 UniRx SDK。" +
                        "所有动作生命周期调用必须在 Unity 主线程，只有 source 通知可跨线程；后台通知使用启动时捕获的 SynchronizationContext Post 回拥有线程，" +
                        "没有上下文则等待 OnExecute。Pause 不暂停 source。首个 OnNext 成功，空 OnCompleted 取消，OnError 记录异常后取消；不 Dispose source 本体。";
                    if (name == "Allocate" || name == "UniRx")
                    {
                        return observable + " observableGetter 不可为 null，在每次 OnStart 调用；Repeat 需要新 source 时由 factory 创建。";
                    }

                    if (name == "ToAction")
                    {
                        return observable + " self 不可为 null，重复执行只是重订阅同一 source，不创建新 source。";
                    }

                    if (name == "OnStart")
                    {
                        return observable + " factory 或 Subscribe 抛错、返回 null 时会取消并向调用方抛出；若清理也抛错则保留为 AggregateException。" +
                        "同步 Subscribe 返回前发生 Deinit 或 Reset，迟到的 IDisposable 仍只释放一次。";
                    }

                    if (name == "OnFinish" || name == "Reset" || name == "Deinit")
                    {
                        return observable +
                        " OnFinish 与 Reset 释放 subscription 但保留 factory，只有 Deinit 清空 factory；旧通知不影响重置或复用后的动作。" +
                        "Dispose 错误向调用方传播，Deinit 仍安排回池；OnError 原异常通过 Debug.LogException 报告，不作为成功结果。";
                    }

                    return observable + " Finish 只标记状态，执行器调用 OnFinish 才释放 subscription，随后 Deinit 清空 factory 并回池；结束后不可继续使用池化引用。";
                case "CoroutineAction":
                case "CoroutineExtension":
                    return "动作取消停止其专属协程句柄，不停止宿主的所有协程。IsPaused 不暂停 Unity 协程；嵌套 IEnumerator 的异常仍可能由 Unity 报告，不能承诺所有异常都转为动作完成。已有 IEnumerator 不能为每轮提供新枚举器。";
                case "ConditionSequence":
                case "ConditionSequenceExtension":
                    return "条件只在启动时检查一次，不等待它变真。分支设置回调当时创建子序列；分支取消会取消父动作，正常完成与取消回调不同。";
                case "Repeat":
                case "RepeatExtension":
                case "IRepeat":
                    return "标准现代 Repeat：-1 和 0 无限重复，正数为轮数，其他负数保留不推进边界。轮间重置内层序列，下次推进开始下一轮。";
                case "Parallel":
                case "ParallelExtension":
                case "IParallel":
                    return "并行是同帧推进，不是多线程。当前空组不自动完成；子动作取消会取消组，不触发成功通知。";
                case "Sequence":
                case "SequenceExtension":
                    return "可以在同一步越过即时子动作，后继以 Execute(0) 启动；并非每个子动作固定等待一帧。子动作取消会取消父组合和剩余子动作。";
                case "Delay":
                case "DelayExtension":
                    return "时间工厂在 OnStart 求值一次，不是每帧求值。现代延时先检查累计值再加入 dt，不保证墙钟精确时刻。";
                case "DelayFrame":
                case "DelayFrameExtension":
                    return "暂停不冻结全局帧计数，恢复后可能立即达到阈值。非正数也要在启动后的执行阶段检查。";
                case "ICustomApi":
                case "Custom":
                case "CustomExtension":
                    return "标准实现中 Data 与委托在 Deinit 清空，不 Dispose Data。回调设置器替换此前回调；OnExecute 返回不代表动作完成，需要 Finish。OnFinish 只用于正常完成。";
                case "ScreenTransitionFade":
                    return "共享 Resources/ActionKit/ScreenTransitionCanvas 的同一 Image，重叠过渡相互覆盖，取消不恢复透明度。直接池 Allocate 可能保留上次配置；优先使用 FadeIn/FadeOut 工厂。持续时间及资源视觉效果应在实际场景验证。";
                case "ScreenTransitionInOut":
                case "ActionKit.ScreenTransition":
                    return "FadeInOut 的包装父动作不随全局子序列完成而 Finish，父 Deinit/Pause/IgnoreTimeScale 不传播到该序列；不能把它当作完成/取消安全的序列子动作。需要所有权一致时组合单独 FadeIn 与 FadeOut。";
                case "AudioSourceShortCutExtensions":
                    return "动作取消不会调用 AudioSource.Stop，也不释放 AudioClip。音源销毁时此轮询不自动 Finish；控制器暂停只停止轮询，不暂停声音。";
                case "EaseUtility":
                    if (name == "Linear")
                    {
                        return "使用 Mathf.Lerp，pos 会按该 API 限制到 0 至 1；其他缓动函数不能套用此裁剪规则。";
                    }

                    if (name.EndsWith("Bounce", StringComparison.Ordinal))
                    {
                        return "保留原 Bounce 公式：InBounce 使用 end - pos 作为 distance，不能保证常规端点；InOutBounce 当前不转交 bounciness。此处不将历史公式改写成另一种曲线。";
                    }

                    if (name.EndsWith("Circle", StringComparison.Ordinal))
                    {
                        return "圆形曲线含平方根，超出其定义域可能得到 NaN；此函数不统一裁剪 pos。";
                    }

                    if (name.EndsWith("Expo", StringComparison.Ordinal))
                    {
                        return "指数曲线没有额外的精确端点特判，因此 pos 为 0 或 1 不必等于 start/end。";
                    }

                    if (name.EndsWith("Elastic", StringComparison.Ordinal))
                    {
                        return "弹性公式保留原周期/相位及接近终点的特判，elasticity 为 0 等输入可能得到 NaN；duration 是计算参数而不是异步计时器。";
                    }

                    return "保留当前曲线公式，不统一裁剪 pos，也不承诺数值始终位于 start/end 之间。Back 曲线可越过端点。";
            }
            if (member.DeclaringType.IsInterface)
            {
                return null;
            }

            if (Modern(member.DeclaringType))
            {
                return "现代动作释放/回池后不要保留原引用继续操作。Reset、Finish 与 Deinit 职责不同，不应直接修改状态字段替代生命周期调用。";
            }

            return null;
        }

        /// <summary>结合声明类型、方法名和扩展参数位置选择参数说明。</summary>
        /// <param name="member">声明该参数的成员。</param>
        /// <param name="parameter">待描述的参数元数据。</param>
        private static string ParameterDescription(MemberInfo member, ParameterInfo parameter)
        {
            var key = Key(member.DeclaringType);
            var name = parameter.Name;
            var methodName = member.Name;
            var method = member as MethodBase;
            if (parameter.Position == 0 && method != null && method.IsDefined(typeof(System.Runtime.CompilerServices.ExtensionAttribute), false))
            {
                switch (key)
                {
                    case "IActionExtensions":
                        return methodName == "Pause" || methodName == "Resume" ? "要暂停或恢复的现代动作控制器，仅关联标识匹配时生效。" : "要标记完成、手动推进或启动的现代 IAction。";
                    case "IActionControllerExtensions":
                        return "需要改为未缩放 dt 的现代控制器，返回仍是此控制器。";
                    case "IActionExecutorExtensions":
                        return "调用此推进辅助方法的执行器；当前方法主要使用 controller 的关联动作。";
                    case "MonoUpdateActionExecutorExtension":
                        return "承载 Update 执行器的 MonoBehaviour，实际组件添加到其 GameObject。";
                    case "CoroutineExtension":
                        if (methodName == "ToAction")
                        {
                            return "要包装的已有 IEnumerator；重复执行仍引用同一个枚举器，不重新创建。";
                        }
                        break;
                    case "TaskExtension":
                        if (methodName == "ToAction")
                        {
                            return "要观察的已有 Task，动作不取得取消/Dispose 所有权，重复执行不创建新 Task。";
                        }
                        break;
                    case "ActionKitUniRxExtension":
                        return methodName == "ToAction" ? "要订阅的非空 BCL IObservable<T> source；动作只取得 subscription 的释放责任，不 Dispose source 本体。" : "要追加 Observable 子动作的非空 ISequence；返回同一外层组合，不立即启动订阅。";
                    case "ActionKitDOTweenExtension":
                        return methodName == "ToAction" ? "要包装的非空、未 kill 且无活动/待清理 ownership marker 的独立根 Tween 或 DG.Tweening.Sequence 本身；合法 Unity 上下文下在 Pause 前预绑定原生命周期，将执行与取消责任交给动作。仅绑定一次，Reset 后不可重新认领，不是只观察它。" : "要追加 Tween 所有权动作的非空 ISequence；返回同一外层组合，不在此取得或启动 Tween。";
                    case "AudioSourceShortCutExtensions":
                        return "动作启动时调用 Play、执行时轮询 isPlaying 的 UnityEngine.AudioSource。";
                }
                if (ModernFactory(key))
                {
                    return "要追加子动作的现代 ISequence，返回的是外层组合而不是新子动作。";
                }
            }
            if (key == "EaseUtility")
            {
                switch (name)
                {
                    case "start":
                        return "缓动计算的起点数值。";
                    case "end":
                        return "缓动计算的终点数值；保留曲线端点误差及原 Bounce 边界。";
                    case "pos":
                        return "通常使用 0 到 1 的归一化位置，不是秒数；各函数不统一限制范围。";
                    case "bounciness":
                        return methodName == "InOutBounce" ? "保留的弹跳形状参数；当前 InOutBounce 不转交此值，实际使用子函数默认值。" : "Back 回摆或 Bounce 弹跳曲线的形状系数，不是重复次数。";
                    case "elasticity":
                        return "弹性振荡的形状系数，与 duration 相乘构成周期量；0 等边界可能产生 NaN。";
                    case "duration":
                        return "Elastic 计算中参与周期和相位的尺度量，不启动定时器，也不是动作执行时长。";
                }
            }
            if ((key == "UniRxAction" || key == "ActionKitUniRxExtension") && name == "observableGetter")
            {
                return "非空 source 工厂，每次 OnStart 取得 IObservable<T>；返回 source 与 Subscribe 返回的 IDisposable 均不可为 null，工厂保留到 Deinit。";
            }

            if ((key == "DOTweenAction" || key == "ActionKitDOTweenExtension") && name == "tweenGetter")
            {
                return "非空 Func<DG.Tweening.Tween>，工厂模式每次 OnStart 在合法 Unity 主线程上下文下调用恰一次；返回非空、未 kill、无活动/待清理 ownership marker 的独立根 Tween 或 DG.Tweening.Sequence 本身，交给动作执行与取消，不是借用对象盲等。Repeat 应每轮创建新 Tween；getter 保留到 Deinit。非法上下文不会调用该工厂。";
            }

            if (key == "DOTweenAction" && name == "dt")
            {
                return "执行器传入的时间增量，此动作不使用它推进 Tween；实际时钟由 Tween 的 SetUpdate 及 DOTween 更新驱动。";
            }

            if (ModernFactory(key) || Modern(member.DeclaringType) || key == "ICustomApi")
            {
                switch (name)
                {
                    case "seconds":
                    case "delayTime":
                        return "等待的秒数，由执行器 dt 累积；现代延时先检查累计量再加入本次 dt。";
                    case "delayTimeFactory":
                        return "延时秒数工厂，在该动作 OnStart 时求值一次。";
                    case "frameCount":
                        return "从启动帧计数到阈值的 Unity 全局帧数，不是执行钩子调用次数。";
                    case "a":
                        return "线性插值起点，启动时发送给数值回调。";
                    case "b":
                        return "线性插值终点，正常完成时发送给数值回调。";
                    case "duration":
                        return "插值持续秒数，以执行器 dt 累计。";
                    case "onLerp":
                        return "接收当前插值 float 的回调，包含起点和正常到达的终点。";
                    case "onLerpFinish":
                        return "插值到达终点时调用的正常完成回调，不是取消清理钩子。";
                    case "callback":
                        return "启动即时回调动作时调用的委托；ActionKit.Delay 中则在延时阈值达到时调用。";
                    case "onDelayFinish":
                    case "onNextFrame":
                        return "达到时间或全局帧阈值时调用的回调，取消不作为成功等待完成。";
                    case "condition":
                        return methodName == "ConditionSequence" || key == "ConditionSequence" ? "启动时只检查一次的谓词，false 跳过分支，true 配置并执行分支。" : "每次执行阶段检查的谓词，true 时完成等待。";
                    case "onCondition":
                        return "Condition 谓词成立时调用的回调。";
                    case "repeatCount":
                        return "现代重复轮数：-1 和 0 无限重复，正数为轮数，其他负数保留不推进边界。";
                    case "repeatSetting":
                        return "配置新内层 IRepeat 的回调，在追加前执行，不是每轮完成通知。";
                    case "parallelSetting":
                        return "配置新内层并行组的回调，参数为 ISequence，不创建工作线程。";
                    case "sequenceSetting":
                        return key == "ConditionSequenceExtension" || key == "ConditionSequence" ? "条件成立的启动阶段配置新分支序列的回调，不在工厂调用时执行。" : "配置新内层序列的回调，在追加到外层前执行。";
                    case "customSetting":
                    case "onCustomSetting":
                        return "工厂配置期间同步调用的 ICustomApi<TData> 配置回调，不是动作运行回调。";
                    case "coroutineGetter":
                        return "动作启动时取得 IEnumerator 的工厂，重复执行需要返回新的可用枚举器。";
                    case "taskGetter":
                        return "动作启动时提供 Task 的工厂；动作只观察它，不取得其取消/Dispose 所有权。";
                    case "action":
                        return key == "ISequence" ? "要交给组合的现代子动作，具体所有权和清理由实现决定。" : "加入组合并由组合生命周期管理的现代子动作。";
                    case "dt":
                        return "此次动作推进收到的时间增量；标准执行器按 UpdateMode 选择缩放或未缩放秒数。";
                    case "onStart":
                        return "替换标准自定义动作的启动回调。";
                    case "onExecute":
                        return "替换接收 dt 的自定义推进回调，需要显式 Finish 完成动作。";
                    case "onFinish":
                        return "替换自定义动作正常完成回调，不是取消清理回调。";
                    case "color":
                        return "要在过渡启动时应用到共享屏幕 Image 的颜色。";
                    case "alpha":
                        return methodName == "FromAlpha" ? "过渡启动时的透明度，不在此修改图像。" : "过渡目标透明度，不在此修改图像。";
                    case "transitionIn":
                        return "包装动作使用的进入 IAction，由另建的全局序列执行。";
                    case "transitionOut":
                        return "包装动作使用的退出 IAction，由另建的全局序列执行。";
                    case "onSetIn":
                    case "onSetOut":
                        return "包装动作启动时配置对应子动作的回调，替换之前配置。";
                    case "onInFinish":
                        return "全局子序列进入动作之后、退出动作之前的回调，不代表包装父动作完成。";
                }
            }
            if (key == "IActionExtensions" || key == "IActionExecutor" || key == "IActionExecutorExtensions" || key == "MonoUpdateActionExecutorExtension")
            {
                if (name == "monoBehaviour")
                {
                    return "动作所在 GameObject 的宿主 MonoBehaviour，使用其对象上的 Update 执行器。";
                }

                if (name == "controller")
                {
                    return "与 action 及其 ActionId 关联的现代控制器；实际调度读取 controller.Action。";
                }

                if (name == "action")
                {
                    return "作为返回值传回的动作引用；实际调度使用 controller.Action，应保持一致。";
                }

                if (name == "dt")
                {
                    return "此次手动推进传给动作的时间增量，调用方负责选择实际单位和时钟。";
                }

                if (name == "onFinish")
                {
                    return "仅正常完成时调用的通知，带参数重载接收当前控制器；取消/失效不触发成功通知。";
                }
            }
            return null;
        }

        /// <summary>为有返回值的方法选择结果说明；其他成员返回 null。</summary>
        /// <param name="member">待描述的成员元数据。</param>
        private static string ReturnDescription(MemberInfo member)
        {
            if (!(member is MethodInfo method) || method.ReturnType == typeof(void))
            {
                return null;
            }

            var prefix = "返回 " + ActionKitApiHelpContent.FormatType(method.ReturnType) + "：";
            var key = Key(member.DeclaringType);
            var name = member.Name;
            if (key == "ActionKitUniRxExtension")
            {
                return prefix + (name == "ToAction" ?
                "尚未订阅的池化 IAction，包装同一 source；成功等待首个值，取消不触发成功通知。" : "追加完成后的同一 ISequence，可继续配置；不是新子动作，也未启动。");
            }

            if (key == "ActionKitDOTweenExtension")
            {
                return prefix + (name == "ToAction" ?
                "预绑定原 Tween 生命周期并已立即 Pause 的池化 IAction，尚未启动；仅复用该绑定，Reset 后再 Start 取消，不认领复租后的对象，也不是无所有权的等待器。" :
                "追加完成后的同一 ISequence，可继续配置；不是子 Tween 或新子动作，也尚未启动。");
            }

            if (key == "DOTweenAction" && name == "Allocate")
            {
                return prefix +
                "保存 getter 的本次池化动作，尚未取得 Tween；启动时接管 getter 返回对象的执行与取消责任，回收后不可继续操作。";
            }

            if (method.ReturnType == typeof(bool))
            {
                if (key == "IActionExtensions" && name == "Execute")
                {
                    return prefix + "true 表示同一分配租约到达完成路径，包含回调中取消后再标记 Finished 的终止结果，不一定表示成功；false 包含尚未完成、未到 Finished 的取消或租约/重置代次变化。调度器仍需检查 IsDeinitialized 和租约有效性。";
                }

                if (key == "IActionExecutorExtensions")
                {
                    return prefix + "true 表示此次跟踪可结束，包含失效或取消，不单指正常成功。";
                }
            }
            if (ModernFactory(key))
            {
                return prefix + (key == "ActionKit" ? "已配置但未启动的动作/组合对象；返回接口与声明一致。" : name == "ToAction" ? "包装现有异步对象、尚未启动的动作。" : "追加完成后的外层组合，不是新建的子动作。");
            }

            if (Modern(member.DeclaringType) && name == "Allocate")
            {
                return prefix + "配置好的本次池化动作租用，尚未启动，结束后不能继续使用此原始引用。";
            }

            if (Modern(member.DeclaringType) && name == "Append")
            {
                return prefix + (key == "ISequence" ? "追加操作提供的组合接口；标准组合实现返回自身。" : "当前组合本身，可继续追加。");
            }

            if (key == "IActionExtensions")
            {
                return prefix + "本次执行的池化控制器引用；同步完成时可能在方法返回前已释放，不能作为永久有效的引用继续使用。";
            }

            if (key == "ActionController" && name == "Allocate")
            {
                return prefix + "本次池化控制器，尚需关联 Action 和 ActionId。";
            }

            if (key == "IActionControllerExtensions")
            {
                return prefix + "修改后的同一控制器。";
            }

            if (key == "MonoUpdateActionExecutorExtension")
            {
                return prefix + "传入的 action 引用，不是控制器或执行器。";
            }

            if (key == "ICustomApi")
            {
                return prefix + "当前自定义动作配置接口。";
            }

            if (key == "EaseUtility")
            {
                return prefix + "按当前曲线计算的数值，不统一保证位于起终点区间或精确端点。";
            }

            if (key == "ActionKit.ScreenTransition")
            {
                return prefix + "已配置但尚未启动的屏幕过渡动作。";
            }

            if (key == "ScreenTransitionFade" || key == "ScreenTransitionInOut")
            {
                return prefix + "配置后的当前过渡对象。";
            }

            if (key == "AudioSourceShortCutExtensions")
            {
                return prefix + "尚未启动的音源播放/停止轮询动作。";
            }

            return null;
        }
    }
}
