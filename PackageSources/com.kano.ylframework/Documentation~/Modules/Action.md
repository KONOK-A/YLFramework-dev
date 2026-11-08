# 动作编排（ActionKit）

> [文档首页](../README.md) · [安装](../Installation.md) · [架构总览](../Architecture.md) · [音频](Audio.md) · [公共计时](Timing.md)

本页导航：[最小用法](#最小用法) · [核心概念](#核心概念) · [运行流程](#运行流程) · [命名执行](#命名执行) · [扩展方式](#扩展方式) · [DOTween 适配](#dotween-适配) · [生命周期与资源](#生命周期与资源) · [屏幕过渡](#屏幕过渡)

## 模块定位

ActionKit 是 Unity 动作编排模块：把回调、等待、条件、插值等步骤组合成可启动的动作链（Sequence / Parallel / Repeat / Delay / Lerp / Coroutine / Task 等），并绑定到对象、当前场景或跨场景全局宿主执行。

- 主要能力：动作创建与组合、逐帧推进、暂停/取消、池化复用、屏幕过渡、全局 Unity 消息转发、内建 BCL Observable，以及可选 DOTween 扩展。
- 运行时位于 `Runtime/Systems/Action/`，namespace 为 `YLFramework.Actions`，入口为 `ActionKit`，属于 `YLFramework.Runtime`。
- 不负责业务资源管线、依赖注入或统一对象生命周期；状态机使用独立的 [FSM 模块](FSM.md)。动作统一使用 `IAction` 契约。
- API 浏览：`YLFramework/Action/帮助`，由 [Help 核心](Help.md) 承载。

## 最小用法

完成[主包安装](../Installation.md)后，业务 asmdef 引用 `YLFramework.Runtime`。将以下组件挂到场景 GameObject，进入 Play 后等待并输出日志；无需 Root、Module 或额外的 Action 包。

```csharp
using UnityEngine;
using YLFramework.Actions;

public sealed class ActionExample : MonoBehaviour
{
    private ActionHandle _run;

    private void OnEnable()
    {
        _run = ActionKit.Sequence()
            .Delay(1f)
            .Callback(() => Debug.Log("动作序列结束"))
            .StartNamed(this, "opening");
    }

    private void OnDisable() => _run.Cancel();
}
```

示例主动选择组件停用即取消。普通 `Start(owner)` 只绑定 owner 所在 GameObject 的执行器，单独禁用组件不会自动停止动作；需要跨场景工作时显式选择全局范围并自行清理。

## 目录与依赖

所有路径相对主包根（安装后为 `Packages/com.kano.ylframework/`）；DOTween保留独立条件程序集，但不单独发行：

| 目录 | 职责 | 程序集 |
|---|---|---|
| [`Runtime/Systems/Action/`](../../Runtime/Systems/Action/) | Core、Actions、Execution、Internal、ScreenTransition、Extensions | `YLFramework.Runtime` |
| [`Runtime/Systems/Action/Observable/`](../../Runtime/Systems/Action/Observable/) | 内建 BCL `IObservable<T>` 适配，无 UniRx 包依赖 | 同上 |
| [`Runtime/Systems/Action/Resources/`](../../Runtime/Systems/Action/Resources/) | 屏幕过渡 Prefab | 同上 |
| [`Editor/Systems/Action/`](../../Editor/Systems/Action/) | 资源创建工具与 Help 提供方 | `YLFramework.Editor` |
| [`Runtime/Systems/Action/Integration/DOTween/`](../../Runtime/Systems/Action/Integration/DOTween/) | 可选`Tween`自有适配源码，不含SDK | `YLFramework.Actions.DOTween` |

Runtime 内复用 ZipTool 与 `UnityEngine.UI`；普通 Editor 引用 Runtime、UI，Help 是同一 Editor 主体内的功能分区。二者不静态引用 DOTween 适配或供应商 DLL。Runtime / Editor 主体的 `autoReferenced` 为 true，ZipTool / Help 不单独生成产品 DLL。完整程序集契约见 [架构总览](../Architecture.md)。

### DOTween 前置

DOTween 适配使用 `YLFramework.Integrations.DOTween`，消费方 asmdef 显式引用 `YLFramework.Actions.DOTween`。该条件程序集引用 Runtime 与 `DOTween.dll`，要求使用工程定义 `DOTWEEN`。应用独立安装 DOTween Standard `1.2.825` 并完成 Setup，保留完整 Runtime Modules 与原 readme；例如将 SDK 放在包外 `Assets/Plugins/Demigiant/DOTween/`。

主包仅带自有适配源码，不携带或下载 SDK，无独立 `.dotween` 包。SDK 来源、归档/程序集摘要见 [Dependencies.json](../../Runtime/Systems/Action/Integration/DOTween/Dependencies.json)，自有适配的 MIT 与来源署名见 [集成许可](../../Runtime/Systems/Action/Integration/DOTween/LICENSE.md)。Standard、Pro 与修改版遵循各自供应商条款，不能互相当作可替代前置；包升级/卸载不管理应用独立安装的 SDK、宏或用户数据。

无宏时不编译适配，普通 Action 可独立使用；同时具备合格 SDK 与宏时才启用。定义宏但缺 SDK 是前置错误，应补齐 DLL，不要将基础 Action 可用误判为 DOTween 适配可用。

## 核心概念

- `ActionKit`（partial class）：静态工厂入口 + 7 个全局消息事件（`OnUpdate` / `OnFixedUpdate` / `OnLateUpdate` / `OnGUI` / `OnApplicationQuit` / `OnApplicationPause` / `OnApplicationFocus`）。
- `IAction` / `AbstractAction<T>`：`ActionStatus` 状态机与 `OnStart` / `OnExecute` / `OnFinish` / `OnDeinit` 契约；自定义动作继承 `AbstractAction<T>`。
- `IActionController` / `ActionController`：匿名 `Start` 返回的池化控制器，含 `UpdateMode` 与 `IgnoreTimeScale` 扩展；取消即 `Deinit()`，回收后不可继续操作原始引用。
- `ActionHandle`：命名根执行返回的只读值类型安全句柄，每次操作验证动作标识、控制器代次和名字登记；失效后不影响后来同名或池复用的任务。
- 执行器：`IActionExecutor` 契约 + internal `MonoUpdateActionExecutor`（每个宿主 GameObject 一个实例，由 Unity Update 驱动）。
- 组合节点：`Sequence`、`Parallel`、`Repeat`、`Condition`、`ConditionSequence`、`Lerp`、`Delay`、`DelayFrame`、`Callback`、`Coroutine`、`Task`、`Custom<TData>`。
- 启动范围：`Start(owner)` / `StartCurrentScene()` / `StartGlobal()`，以及 `Pause` / `Resume`；扩展所在实际类型为 `IActionExtensions`。
- 事件与注销：7 个全局消息返回 `YLFramework.Events` 的 `EasyEvent` / `EasyEvent<bool>`；订阅、注销句柄与生命周期触发器统一使用 [Events 模块](Events.md)。
- ScreenTransition：`ActionKit.ScreenTransition.FadeIn` / `FadeOut` + internal 共享画布 `ScreenTransitionCanvas`；`FadeInOut(onInFinish)` 返回普通 `ISequence`，不创建隐藏全局序列。

## 源码入口

按阅读顺序：

1. [`Core/IAction.cs`](../../Runtime/Systems/Action/Core/IAction.cs)：动作契约与池化基类。
2. [`Core/ActionKit.cs`](../../Runtime/Systems/Action/Core/ActionKit.cs)：工厂与全局消息；生命周期见同目录 `ActionKitLifecycle.cs`。
3. [`Extensions/ActionStartExtensions.cs`](../../Runtime/Systems/Action/Extensions/ActionStartExtensions.cs)、[`ActionNamedExtensions.cs`](../../Runtime/Systems/Action/Extensions/ActionNamedExtensions.cs)：匿名与命名执行。
4. [`Execution/MonoUpdateActionExecutor.cs`](../../Runtime/Systems/Action/Execution/MonoUpdateActionExecutor.cs)、[`ActionHandle.cs`](../../Runtime/Systems/Action/Execution/ActionHandle.cs)：推进、回收与租约。
5. [`Actions/Sequence.cs`](../../Runtime/Systems/Action/Actions/Sequence.cs)：组合实现范式；Delay / Task 同目录。
6. [`Editor/Systems/Action/Help/ActionKitHelpProvider.cs`](../../Editor/Systems/Action/Help/ActionKitHelpProvider.cs)：注册与内容组装。

## 运行流程

```text
创建：ActionKit.Sequence()/Delay(seconds, callback) → Xxx.Allocate() → ActionPool<T>.Allocate()（取得 ActionId，未启动）
启动：action.Start(owner) → ActionKit.Initialize() → ActionController.Allocate() → owner.ExecuteByUpdate(...) → Step → OnStart
推进：Unity Update → MonoUpdateActionExecutor.Tick(Time.deltaTime, Time.unscaledDeltaTime) → IAction.Execute(dt) → OnExecute(dt)
完成：Execute 检测到 Finished → OnFinish 并返回 true → 执行器调用外部 onFinish 回调 → Deinit → ActionQueue
      → ActionRecycleDriver.Update → Drain → ActionPool<T>.Recycle
取消：controller.Deinit() 或宿主销毁 → 动作 Deinit 入回收队列，不触发完成回调；控制器立即失效
```

行为要点：

- 禁用 GameObject 会暂停该执行器的更新；销毁 GameObject 会取消它持有的动作；禁用传入的组件不影响执行器。
- 即时动作（如 `Callback`）可能在 `Start` 返回前完成并回收，返回的控制器此时已无效；`Condition` 不在首次 OnStart 检查条件。
- 首次推进只调用 `OnStart`，不调用 `OnExecute`；在钩子中标记 Finished 可在同一次 `Execute` 内触发 `OnFinish`。`Finish()` 本身只改状态，不立即通知或回收。手动 `Execute` 不负责回收，对 Finished 再次执行还会再次调用 `OnFinish`。
- `Delay` 先检查已有累计时间，再加本次 dt，因此跨过时长后在下一次推进检测完成；它不采用 Timer 的到期算法。
- 执行或完成通知抛错会结束该任务并清理，不视作成功；清理错误保留为附加诊断或聚合异常。不要假定同一执行器本帧其余任务仍会继续推进。

### 常用节点语义

- `Sequence` 按加入顺序推进，即时完成的子动作可在同次推进中连续执行；`Parallel` 在同次推进中依次更新子动作，并非多线程并行，空 Parallel 不自动完成。
- `Repeat(repeatCount)` 的正数为总轮数，-1 和 0 表示无限重复，其他负数不会推进也不自动完成；不要把它与 Timer 的 count 合同混用。
- `DelayFrame` / `NextFrame` 读取 Unity `Time.frameCount`，不按 `Execute` 次数计帧；非正帧数在启动后的首次执行完成，不转换为秒数。
- `Lerp` 按执行器选择的 dt 推进，启动和后续更新回调均可交付插值结果。`Finish()` 只标记状态，手动驱动者仍负责通知时序和 Deinit。

## 命名执行

在 MonoBehaviour 的方法内启动；`this` 只提供所在 GameObject 的执行作用域。

```csharp
using UnityEngine;
using YLFramework.Actions;

var handle = ActionKit.Sequence()
    .Delay(1f)
    .Callback(() => Debug.Log("流程结束"))
    .StartNamed(this, "opening");
handle.SetUpdateMode(ActionUpdateModes.UnscaledDeltaTime);
handle.Pause();
handle.Resume();

if (ActionKit.TryGetNamed(this, "opening", out var current))
{
    current.Cancel(); // 不触发成功完成通知。
}

ActionKit.Delay(2f, null).ReplaceNamed(this, "opening");
```

- 启动：`StartNamed(owner, flag)`、`StartNamedCurrentScene(flag)`、`StartNamedGlobal(flag)`；每种都有 `Action<IActionController>` 或无参数 `Action` 正常完成通知重载。
- 替换：`ReplaceNamed(owner, flag)`、`ReplaceNamedCurrentScene(flag)`、`ReplaceNamedGlobal(flag)`。先验证新动作和参数，再取消旧同名任务；新启动或旧清理失败不会复活旧任务。清理重入新建同名任务时，外层替换会拒绝覆盖新登记。不能用仍被执行器持有的同一动作实例替换自己。
- 查询与控制：`ActionKit.TryGetNamed(owner, flag, out handle)`、`ContainsNamed`、`CancelNamed`、`PauseNamed`、`ResumeNamed`；场景和全局版本分别加 `CurrentScene`、`Global` 后缀且不接收 owner。只读现有宿主缓存和执行器，不创建对象或初始化模块。合法名字不存在或已失效时返回 `false`。
- 名字采用 `StringComparer.Ordinal`，区分大小写，所有命名入口拒绝 null/空白名字。默认有效重名抛出 `InvalidOperationException`，不消费新动作，也不扰动旧任务。匿名 `Start` 不创建名字字典或命名包装对象。
- 作用域为组件所属 GameObject，而非单个组件；其他对象、场景/全局执行器及 Timer 可以使用相同名字，索引彼此独立。沿用现有场景宿主行为，不因 activeScene 切换自动取消或新建作用域。
- 首次同步推进前登记；同步完成不留名。根完成通知中可以用新动作启动同名执行，旧收尾不删新登记；子节点回调和 Repeat 每轮回调仍属于活跃根执行，不提前释放名字。
- `ActionHandle.IsActive`、`Pause()`、`Resume()`、`Cancel()`、`SetUpdateMode(mode)` 每次核验原租约。默认值、正常完成、取消、异常、宿主销毁、Shutdown 后返回 `false`，控制器/动作池复用不使旧句柄重新有效；ActionId 为 0 也可有效。活跃动作 Reset 仅重置进度，不改变句柄租约。
- 对象停用只暂停自动 Update，组件停用不停止执行器；对象销毁取消所属任务。命名接口只增加查询和控制身份，不改变 Delay、Lerp、Repeat 的时钟与重复算法。

全局任务不依附调用方对象；需要调用方退出时取消，应显式保存安全句柄并清理：

```csharp
private ActionHandle _background;
private void Start() => _background = ActionKit.Delay(10f, null).StartNamedGlobal("background");
private void OnDisable() => _background.Cancel();
```

## 扩展方式

- 自定义动作：继承 `AbstractAction<T>`；或用 `ActionKit.Custom` / `Custom<TData>`、`ActionKit.Task`、`ActionKit.Coroutine` 包装逻辑。
- 组合扩展：`ISequence.Append` 与各 `XxxExtension`（`SequenceExtension`、`ParallelExtension`、`RepeatExtension`、`DelayExtension`、`LerpExtension`、`CustomExtension`、`TaskExtension` 等）。
- 执行器：`IActionExecutor` 是 public 接口并提供 `IActionExecutorExtensions.UpdateAction`，但 `Start` 路径固定使用 internal 的 `MonoUpdateActionExecutor`，当前没有公开注入点。
- 订阅生命周期：使用 `YLFramework.Events.UnregisterExtension` 与 `RegisterListExtension` 绑定注销句柄；`ActionKit.Shutdown` 只清空模块自有事件，不处置用户触发器或句柄。
- `Task` 仅观察外部任务，暂停/取消动作不会暂停、取消或 Dispose 该 Task；任务自身取消或失败会清理动作而非正常完成，失败会记录异常。应在 Unity 主线程启动，以便异步续体回到 Unity 同步上下文。
- `Coroutine` 在 Action 全局宿主启动自己的协程；取消或 Reset 会停止该协程，暂停动作不会暂停它。包装已有枚举器的 `ToAction()` 不会在 Reset 时使枚举器回到起点。

### 等待 Observable 首值

内建 `UniRxAction<T>.Allocate(factory)`、`IObservable<T>.ToAction()` 与 `ISequence.UniRx(factory)` 使用 BCL `IObservable<T>`，不要求 UniRx 包。名字是当前 API 名称，不代表依赖外部响应式库。

- 启动时才订阅，首个 `OnNext` 标记正常完成，但动作不返回这个值；无值的 `OnCompleted` 取消，`OnError` 记录异常并取消。
- 正常完成、取消或 Reset 只释放本次 subscription，不 Dispose source，也不保证终止其外部生产者。暂停不暂停 source，通知仍可到达。
- 在 Unity 主线程启动以捕获同步上下文；异线程通知投递回来，没有上下文时由启动线程的 OnExecute 消费。
- Subscribe 返回前即可同步通知或取消，迟到句柄仍会释放；释放异常可见，清理失败的动作丢弃，不以脏对象重新入池。

## DOTween 适配

完成 [DOTween 前置](#dotween-前置)后，使用工厂为每次启动创建新的独占根 Tween。以下组件仅适用于已启用适配程序集的工程：

```csharp
using DG.Tweening;
using UnityEngine;
using YLFramework.Actions;
using YLFramework.Integrations.DOTween;

public sealed class TweenActionExample : MonoBehaviour
{
    private ActionHandle _run;

    private void OnEnable()
    {
        _run = ActionKit.Sequence()
            .DOTween(() => transform.DOMoveX(3f, 0.5f).SetUpdate(true))
            .Callback(() => Debug.Log("Tween 正常完成"))
            .StartNamed(this, "move");
    }

    private void OnDisable() => _run.Cancel();
}
```

| 入口 | 取得 Tween 的时机与用途 |
| --- | --- |
| `DOTweenAction.Allocate(Func<Tween>)` | 动作启动时调用工厂，可作为独立根或 Append 子动作 |
| `sequence.DOTween(Func<Tween>)` | 将同样的工厂动作加入序列，返回原 `ISequence` |
| `tween.ToAction()` | 立即预绑定并 Pause 已有 Tween，首次动作启动时从头播放 |

- 只能接管独立根 Tween 或 `DG.Tweening.Sequence` 本身，不支持其内部子 Tween。null、已经 Kill 或被另一个 Action 执行占有的 Tween 会被拒绝。
- 绑定与启动要求真实 Unity 主线程和原始 `UnitySynchronizationContext`；错误线程/上下文在调用工厂或 Pause 前抛错，不支持自定义上下文或人工 pump。
- 播放由 DOTween 驱动，动作的 dt、`IgnoreTimeScale`、`SetUpdateMode` 不自动改变 Tween 时钟。需要非缩放播放时像示例一样显式配置 Tween。
- 动作 Pause 只暂停仍由自己拥有且正在播放的 Tween；Resume 只恢复动作自己发起的暂停，不接管外部 Pause。
- Tween 正常完成才算动作正常完成；未完成的 `Kill(false)` 或持有标记丢失会取消动作，不触发成功通知。
- 工厂动作 Reset 保留工厂，下次启动必须交付新 Tween，不能复用已 Kill 的实例。`ToAction` 在首次启动前的 Reset 保留预绑定，首次启动后的 Reset 不重新认领原生命周期，重启会取消。
- Deinit 立即使动作失效，Reset 立即回到 NotStart；物理 `Kill(false)` 通过 Unity 上下文 Post 延后，并在主线程复核 ownership。待清理期间标记阻止其他动作重认领，不应由业务改写回调或抢用该 Tween。
- 正常 OnFinish 仅解除本方 hooks，不 Kill Tween；自动 Kill 等后续行为由 Tween 自身设置决定。Shutdown 不保证同步物理 Kill，Domain Reload、Post 未执行或 ownership 无法证实时也不保证物理清理。
- onComplete/onKill 是 multicast，DOTween safeMode 不等于每个 delegate 都隔离异常。前序回调抛错或外部覆盖 hooks 可能使信号丢失，适配不承诺识别所有外部改写的完成/取消顺序。

## 生命周期与资源

- 模块操作与 Unity 宿主仅在 Unity 主线程使用，不提供线程安全。`Initialize()` 是标准初始化入口，幂等懒初始化；`Start` 或全局宿主访问会自动初始化。`Shutdown()` 取消模块管理的动作、释放自有宿主与事件；Play 会话重置清理前一轮残留，不恢复旧动作。
- 全局 `OnUpdate` / `OnFixedUpdate` / `OnLateUpdate` 的自动通知复用 `YLFramework.Update.UpdateManager`。每个初始化成功且启用的全局宿主持有三个精确订阅，组件 disabled 或对象 inactive 时断开，恢复后重新连接；借用 inactive 宿主不会被自动激活。连接轮次与执行会话检查拒绝退订后仍留在公共快照中的旧回调。
- 七个属性返回模块自有 `EasyEvent` / `EasyEvent<bool>`；Action 监听采用 fail-fast，抛异常会停止该消息的后续监听，公共源仍继续其他公共消费者并报告异常。借用事件不应自行 Dispose/归池；本方监听用 token 退订，Shutdown 不清理公共源的其他订阅。
- 初始化补丁中取得宿主不会提前连接，成功提交后刷新已有宿主；初始化失败期间不自动广播。失败会清除本轮模块实例并传播异常，但不提供完整的补丁事务回滚。初始化配置期间调用 Shutdown 会抛错；执行回调内 Shutdown 先停止后续回调，最外层执行退出后再释放。Shutdown/重建终结旧消息代次，旧 token 不进入新宿主。
- GUI/Focus/Pause/Quit 与 Action 协程仍由全局宿主处理；Quit 通知前设置共享会话退出屏障，监听中 Shutdown 后无法重建宿主或启动 Foundation 协程。需要注销的事件或句柄应提前保存，不在退出后重新访问创建式属性。
- 对象执行器与延迟回收驱动仍独立推进；全局消息、执行器和回收之间不保证执行全序。公共宿主被外部销毁或停用时不自动恢复既有工作；共享更新源不构成性能提升结论。
- 对象执行器、当前场景宿主、跨场景全局宿主保留独立生命周期；不能用 TimerScope 清理 Action，也不将 Action Delay 改成 Timer 包装。
- internal `ActionPool<T>` 无缓存数量上限，模块外不可配置，组合 YL 池并以 LIFO 复用；回收队列、回收标记与租约由 Action 管理。同一动作不能同时交给两个执行器；归还后不能继续使用原始动作或控制器引用。
- [`ScreenTransitionCanvas.prefab`](../../Runtime/Systems/Action/Resources/ActionKit/ScreenTransitionCanvas.prefab) 是主包内正式资产，Resources key 为 `ActionKit/ScreenTransitionCanvas`，不依赖导入样例。消费方不运行产品资源重建命令、不写入只读包目录；自定义资源由使用工程明确持有。不新增 ResourceSystem 或外部资源后端的 Loader。

## 屏幕过渡

`FadeInOut(onInFinish = null)` 顺序拥有默认一秒 `FadeIn`、同步中段回调和默认一秒 `FadeOut`。调用方选择 `Start(owner)`、场景或全局宿主；父序列统一等待、暂停、取消和释放子动作，回调异常沿所选执行器的普通序列错误路径处理。需要自定义颜色、时长或步骤时，显式组合独立 FadeIn/FadeOut。

```csharp
// MonoBehaviour 方法中；完成通知表示动作时序，不代表已经验证视觉覆盖。
ActionKit.ScreenTransition.FadeInOut(() => Debug.Log("遮罩已淡入")).Start(this);
ActionKit.Sequence().Append(ActionKit.ScreenTransition.FadeIn().Duration(0.3f))
    .Callback(() => Debug.Log("处理中段逻辑"))
    .Append(ActionKit.ScreenTransition.FadeOut().Duration(0.3f)).Start(this);
```

所有过渡共享一个 Image，重叠执行会相互覆盖；取消不会复原显示状态。Fade 的 `Reset` 保留本轮配置用于重复执行，回收后恢复黑色、透明度 0 到 0、一秒时长和零进度，归还后的引用不能再操作。当前 Prefab 的布局包含零缩放且没有 GraphicRaycaster，不能仅凭动作完成就认定遮罩可见或能够阻挡输入；应用需确认实际 Canvas 布局与交互效果。

## 编辑器帮助

`YLFramework/Action/帮助` 与公共 `YLFramework/帮助` 提供分类 API、关联指南和完整示例，浏览文档不会执行示例。API 范围包含内建 Observable 和当前可用的 DOTween 适配，不展示整个共享 Runtime。

成员说明读取 Runtime 编译产生的 XML；DOTween 使用自己的程序集 XML。它们是本机构建产物，不随包交付，缺失时帮助只显示真实声明。安装与包内容见[安装说明](../Installation.md)，帮助窗口使用见 [Help](Help.md)。
