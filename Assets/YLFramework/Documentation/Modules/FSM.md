# 代码状态机（FSM）

> [文档首页](../README.md) · [架构总览](../Architecture.md)

## 定位

代码优先、轻量、方便在普通业务代码中快速使用的有限状态机，适用于玩家 / 敌人行为状态、UI 流程、技能阶段、交互状态、任务流程阶段与小型玩法逻辑。

本模块**不提供可视化 FSM Editor**，也**不以大型状态图制作为目标**：没有 GraphView、节点编辑器、状态机资产、序列化状态图、Hierarchical FSM、State Stack、异步 / 线程安全等能力。需要这些能力时应使用专门的状态机或行为树方案。

YL 的配置/可视化状态机位于 `Runtime/Systems/StateMachine/` 与 `Editor/Systems/StateMachine/`，namespace 为 `YLFramework.StateMachine` / `YLFramework.Editor.StateMachine`；轻量 FSM 归 Utilities，不与之合并。外部状态机也可独立共存；本模块不替代或依赖上述配置式状态机或其他外部实现，也不需要 Module、统一状态机 Manager 或公共 Root。调用方自行驱动并管理回收，Module 关闭不会自动停止外部 FSM。

## 位置

- 运行时：[`Runtime/Utilities/FSM/`](../../Runtime/Utilities/FSM/)
  - `Abstractions/`：`IState`、`IFsmOwner`、`MachineState`
  - `Core/`：`Fsm<TStateId>`
  - `States/`：`CustomState`、`AbstractState<TStateId>`
- namespace：`YLFramework.FSM`；程序集：`YLFramework.Runtime`。有 asmdef 的使用者引用 Runtime 即可，不需要引用 `YLFramework.Core.Runtime` 或外部状态机程序集。

## 快速使用

使用 `using YLFramework.FSM;`。以下为业务方法内的调用片段，`State` 枚举放在类型声明中；提供宿主时，其类型应实现空标记接口 `IFsmOwner`，不需要宿主可省略 `Init`。

```csharp
enum State { Idle, Move, Attack }

var fsm = Fsm<State>.Allocate();
fsm.Init(null);                                     // 可选；仅实现 IFsmOwner 的宿主才能传 this

fsm.State(State.Idle)
   .OnEnter(() => { })
   .OnUpdate(() => { })
   .OnExit(() => { });
fsm.State(State.Move).OnUpdate(() => { });
fsm.State(State.Attack).OnEnter(() => { });

fsm.SetUp(State.Idle);                              // 启动
fsm.Update();                                       // 由业务每帧驱动
fsm.ChangeState(State.Move);                        // 切换
fsm.Pause();                                        // 暂停（当前状态 Exit）
fsm.Continue();                                     // 恢复（重新 Enter）
fsm.RecycleToCache();                               // 回收，之后不再使用该实例
```

## 核心 API

- 注册与启动：`Init(owner)`、`AddState(id, state)`、`State(id)`（不存在时自动创建 `CustomState`）、`SetUp(startStateId)`。
- 驱动：`Update()` / `LateUpdate()` / `FixedUpdate()` / `OnGUI()`，仅在 Running 时执行。
- 切换：`ChangeState(id, reenterCurrentState = false)`（带重入保护）、`Pause()`、`Continue()`、`Continue(stateId)`。
- 查询：`MachineState`、`IsRunning`、`IsPaused`、`CurrentStateId` / `CurrentState`、`PreviousStateId` / `PreviousState`、`HasState(id)`、`TryGetState(id, out state)`、`StateCount`。
- 计时：`FrameCountOfCurrentState`、`SecondsOfCurrentState`。
- 池化：`Allocate()` / `RecycleToCache()`（实现 `IOfPool` / `IPoolType`）。

## 生命周期

`Allocate` → `Init(owner)`（可选）→ `AddState` / `State(id)` → `SetUp(startStateId)` → 每帧驱动 → `Pause` / `Continue` → `RecycleToCache`。

- 只有 `NotStart` 状态可 `SetUp`；起始状态必须已注册，且**不检查**起始状态的 `Condition`。
- `SetUp` 的 `Enter` 成功后才提交 Running；`Enter` 抛异常时回滚为干净的 `NotStart` 并原样抛出，可重试。
- 启动后再调用 `SetUp` 返回 false；`Stop` 后也不能重新 `SetUp`，只能 `Continue` / `Continue(stateId)`。
- 正常回收后实例回到对象池，所有状态、宿主、回调和计时被清空；再次 `Allocate` 得到的实例必须重新注册状态。退出回调失败的隔离边界见下节 Pool 生命周期。

## 状态切换

`ChangeState(newStateId, reenterCurrentState)` 的校验与顺序：

1. 校验：必须 Running、非切换中、目标已注册、`Condition()` 通过（目标与当前相同且 `reenterCurrentState == false` 时直接返回 false）。
2. `旧状态.Exit()`。
3. 更新 `CurrentState` / `PreviousStateId` / `CurrentStateId`。
4. 触发 `OnStateChanged(previous, next)`。
5. 重置 `FrameCountOfCurrentState = 1`、`SecondsOfCurrentState = 0`。
6. `新状态.Enter()`。

- `Condition` 的真实语义是“该状态当前是否允许被切入”，FSM 仅在一状态作为切换目标时询问；返回 false 时不会触发任何 Exit / Enter 回调。`Condition` 求值期间禁止重入切换 / 暂停 / 回收。
- 在 `Condition` / `Exit` / `OnStateChanged` / `Enter` 回调内再次调用 `ChangeState` 或 `Pause` 会被拒绝（返回 false），避免生命周期递归与 Exit/Enter 不配对。
- `Continue(stateId)` 是从暂停恢复并切换：当前状态已在 Pause 时 Exit，不会重复 Exit，流程为条件校验 → 更新 ID → `OnStateChanged` → 重置计时 → `Enter`；异常策略与 `ChangeState` 完全一致。

### 切换中的异常语义

切换按“提交点”划分，异常不会让状态机卡在切换中（`_isTransitioning` 一定恢复），也不会回滚已经执行的 `Exit`：

| 抛出位置 | 提交情况 | 异常后语义 |
| --- | --- | --- |
| `Condition` | 未提交 | Current / Previous / 计时不变，可重试 |
| `Exit`（旧状态） | 未提交 | Current / Previous / 计时不变，`MachineState` 保持 Running，可重试 |
| `OnStateChanged` | 已提交，且仍会重置计时并执行 `Enter` | Current = 新状态、Previous = 旧状态、计时已重置、新状态已 Enter；异常继续向上传播 |
| `Enter`（新状态） | 已提交 | Current = 新状态、Previous = 旧状态、计时已重置、`MachineState` 保持 Running；可用 `ChangeState(新状态, true)` 重进 |

- 单个回调的异常向调用方传播，切换标记会恢复；异常不自动撤销用户回调已经造成的外部副作用。
- `Exit` 已经发生，因此不会自动回滚并重新 `Enter` 旧状态（避免引入事务式回滚）。
- `OnStateChanged` 与随后 `Enter` 都抛异常时，`Enter` 位于 `finally`，最终传播的是 `Enter` 异常，不聚合前一个异常。

## Pause / Continue

- `Pause()`：仅 Running 时生效，对当前状态调用 `Exit`；**`Exit` 成功后才提交 `Stop`**。`Exit` 抛异常时保持 Running、Current 与计时不变，可重试。切换回调内调用会被拒绝。
- 暂停期间 `Update` / `LateUpdate` / `FixedUpdate` / `OnGUI` 全部不执行，`ChangeState` 不生效，状态计时停止累加。
- `Continue()`：重新 `Enter` 暂停前的当前状态，**计时延续不重置**；**`Enter` 成功后才提交 Running**，失败保持 `Stop`、计时不变，可重试，也不会触发 `OnStateChanged`。
- `Continue(stateId)`：条件通过时切换到目标状态，**计时重置**，并触发 `OnStateChanged`；`Enter` 抛异常时切换已提交并保持 Running（与 `ChangeState` 一致）。

## 状态计时

- `FrameCountOfCurrentState`：SetUp / 切换 / `Continue(stateId)` 后为 1；常规帧每次 `Update` 在状态自身 `Update` 之后 +1，因此回调内首帧读到 1。
- `SecondsOfCurrentState`：仅在 `Update` 中累加 `Time.deltaTime`；FixedUpdate / LateUpdate / OnGUI 不累计，暂停期间不累计。
- `Update` 仅在回调结束后仍为 Running、且 `CurrentState` 与进入时为同一实例时累计本帧。该判断不追踪切换次数：同状态重进、切走又切回同一实例，或同次回调内 Pause 后 Continue，结束时满足条件仍会累计。
- `RecycleToCache` 后重置为 frame = 1、seconds = 0。

## 回调内的操作边界

| 操作 | 驱动回调（Update / LateUpdate / FixedUpdate / OnGUI） | 切换回调（Condition / Exit / OnStateChanged / Enter） |
| --- | --- | --- |
| `ChangeState` / `Pause` | 允许（同步生效，后续同帧驱动使用最新状态） | 拒绝，返回 false |
| `Init` | 允许（会同步刷新全部状态的 Owner） | 允许 |
| `AddState` | 允许（注册新 ID） | 允许 |
| `SetUp` | 拒绝（非 NotStart） | 拒绝（非 NotStart / 重入） |
| `RecycleToCache` | **拒绝，抛 `InvalidOperationException`** | **拒绝，抛 `InvalidOperationException`** |

- 在 `Update` 回调中回收会导致“对象已回池、旧调用栈继续运行”和计时字段被回写，因此明确拒绝；请先退出回调再回收。
- 回收清理（`DeInit` 回调）期间不允许 `AddState`（返回 false），也不要再操作该 FSM。

## 池化生命周期

`Allocate` → 使用 → `RecycleToCache` → `OnRecycle`（Pause 退出当前状态 → 先清空 FSM 自身状态并摘除全部状态引用 → 逐个 `DeInit` → 完成清理）→ 再次 `Allocate` 复用。

- 状态对象由调用方创建并持有，FSM 只保存引用并调用 `Init` / `DeInit`，不会 Dispose 状态对象。同一个状态实例同一时间只应属于一个 FSM。
- 若某个 `DeInit` 抛异常：其余状态的 `DeInit` 仍会继续执行，只向上抛出第一个异常；FSM 内部状态已清空，且对象池会拒绝把该实例入池，不会以脏状态被复用。
- 若当前状态的 `Exit` 在回收时抛异常，Safe 池隔离该实例且不会重试 / 重新出借；隔离不等于已完成领域清理，状态引用可能仍保留。调用方仍负责状态所持有的外部资源，不把 Pool 会话重置当作 FSM 的成功退出。
- FSM 通过 `SafeObjectPool` 归还自身；直接调用 `OnRecycle` 不是入池入口。

## 主要类型

- `Fsm<TStateId>`：状态机主体（`Core/Fsm.cs`），保留命名空间 `YLFramework.FSM`。
- `IState`：状态契约；`Condition` 的语义见上。
- `CustomState`：委托链式快速状态（未设置的委托不执行，重复设置以后一次为准）。
- `AbstractState<TStateId>`：正式业务状态基类，提供 `StateMachine` / `StateId` / `Owner` 字段，以及 `ChangeState`、`SecondsInState`、`FramesInState`、`GetOwner<T>()` 等 protected 便捷入口。
- `IFsmOwner`：宿主标记接口，允许为 null；不定义行为。
- `MachineState`：`NotStart` / `Running` / `Stop`（Stop 表示已暂停，不是终止）。

## 重要限制

- 不提供运行时 `RemoveState` / `ClearStates`；状态集合在 SetUp 前注册，运行中可继续注册新 ID，但不支持动态移除。
- `PreviousState` 只有一层，不提供历史栈 / Undo / Redo。
- `State(id)` 有注册副作用；仅查询请用 `HasState` / `TryGetState`。若该 ID 已注册的状态不是 `CustomState`，`State(id)` 返回 null。
- 同一实例回收后不可继续使用；`RecycleToCache` 期间不要依赖状态回调。
- 不提供异步 / 线程安全 / 网络同步 / 序列化能力。

## 依赖

- `Pool`：实例复用现有 Runtime `SafeObjectPool` 包装及 Foundation 池内核，沿用 `YLFramework.Pooling` namespace；不依赖 Core Module / MVC，不新增独立缓存或生命周期管理器。

## 测试入口

`Tests/EditMode/Foundation/Pool/PoolContractTests.cs` 的 `FsmRetainsCallbackReturnGateAndFailedExitIsIsolatedRatherThanRetried` 覆盖 FSM 与池的回调回收限制、退出失败隔离及正常复用，不代表 FSM 全部行为覆盖，也不表示当前环境已执行通过。
