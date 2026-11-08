# 检测、移动与资产状态机

> [文档首页](../README.md) · [架构](../Architecture.md) · [轻量 FSM](FSM.md) · [Editor 扩展](Editor.md)

Overlap、CharacterController2D、MoveablePlatform 与配置式 StateMachine 是独立使用的能力，不要求 Root、Module、旧宿主扩展或输入包。它们使用自己的组件与资产，不自动重绑其他框架的 Scene、Prefab 或状态类型。

## Overlap

安装 [com.kano.ylframework.overlap](../../../com.kano.ylframework.overlap/README.md) 及主包，Unity内置physics/physics2d由包直接声明。最小自有闭包仅main + overlap，不要求movement2d、Gameplay、RedDots或Save。消费方asmdef显式引用`YLFramework.Overlap`；直接使用Inspector/Gizmos类型的Editor程序集另引用`YLFramework.Overlap.Editor`。

namespace 为 `YLFramework.Overlap`，当前实现位于 `Runtime/Queries` 与 `Runtime/Detection`，配套 Inspector 为 `Editor/OverlapDetectorInspector.cs`，Editor namespace 为 `YLFramework.Editor.Overlap`。主包不提供这些类型。旧 `Utilities/Overlap` 组件、旧 Inspector、可变 hit 列表和 XY-only 3D 协议已退役，旧 GUID 不映射到新类型；历史迁移 Prefab/Catalog/YAML/meta/hash 仅保留原件，不作为当前可冷导入的承诺。

- `OverlapQuery2D/3D` 使用调用方缓冲、显式 Filter 与物理场景执行 Query/Cast；Workspace 为显式 owner 自有并释放，不是全局 Manager。
- `OverlapDetector2D/3D` 使用局部 Union、Manual/Update/FixedUpdate/UnscaledUpdate、实例有界容量和完整结果差分。`UnscaledUpdate` 在 `timeScale=0` 仍可采样，`FixedUpdate` 不冒充继续运行。
- **只有完整观测才能提交差异**。`MayBeTruncated` / `Invalidated` 保留 `LastComplete`；提交在回调前完成，按旧离开、新进入、原有停留通知。所有容量、generation 与 ownerScene 约束同样适用于 Area。
- `OverlapContact<T>.Point` 是相对 Detector 位置取得的代表 Collider 原生最近点快照，2D Z=0，3D 保留 Z；Enter/Stay 刷新，Destroyed 等 Exit 使用最后提交点。它不是法线、穿透深度或连续接触解算。
- `EnteredEvent`、`StayedEvent`、`ExitedEvent` 是同一 Contact 合同的序列化 UnityEvent；Exit 额外传 `OverlapExitReason`。停止、禁用或重配置会取消旧周期剩余通知，保留原退出顺序。
- 2D `SetAreaPositions(Vector3[])` / `SetAreaTransforms(Transform[])` 选择本地位置或实时世界节点，相邻对转换成世界 XY AABB，不自动闭合。缺失 Transform 只跳过原相邻对，不跨缺口连接；空链为完整空结果，非有限节点或零宽/高框明确 `Invalidated`，不提交假 Exit。

具体签名、退出原因、容量与几何限制见 [Overlap 包说明](../../../com.kano.ylframework.overlap/README.md)。当前作者及 Player 输入由正式 producer 生成新 GUID，并验证真实持久 Contact 回调；不手工重写或重采冻结快照。

两个 Detector 的 Inspector 可持久编辑几何、采样、容量和 Filter，支持 Undo/Prefab override；2D 输入选择器同时支持 Area 的位置/Transform 数组，校验、Gizmos 和按相邻对预览复用 Runtime 转换，不另建 Geometry/Manager。Union 的中心/尺寸 Handle 不隐式修改 Area 节点。仅单选实例可显式或主动开启限频预览，重绘不查询；Cast 方向/距离为明确选择的世界值，不是对象隐含运动。实际 `Count/RawCount/status` 与借用的 `LastComplete` 分列，partial/invalid 不冒充完整当前集。2D 全局禁 Trigger 的请求明确诊断、不翻全局开关；3D 默认 Ignore，不使用 UseGlobal。共享 Prefab asset 不由预览覆盖，不自动保存用户场景。

仓库入口为 `Daily/Overlap`、`Daily/OverlapEditor`、`Daily/OverlapMovement` 与 `Special/OverlapPlayer`；当前 Behavior producer/持久消费者另由 `Special/BehaviorPlayer` 复用既有双进程 Development Mono 验收。旧 managed guard 删除后不保留无当前目标的 `Quick/Overlap`，不以 stub 代替实际 Unity 物理；旧主工程 PID/SessionState/外部接收根及 Batch 接收入口已退役，不将其历史结果当当前 Player 通过。

## CharacterController2D

安装 [com.kano.ylframework.movement2d](../../../com.kano.ylframework.movement2d/README.md) 及主包/physics2d。namespace 与 Runtime 程序集均为 `YLFramework.Character2D`，包内实现位于 `Runtime/Utilities/CharacterController2D`；消费方 asmdef 只需引用实际使用的 Runtime，不需要引用 Platform2D。

1. 在同一 Rigidbody2D 下准备非 Trigger 的 Box / Circle / CapsuleCollider2D，允许多个 attached Collider；先配置再启用。
2. 在调用方 FixedUpdate 使用 `Move(Vector2 delta)` 或 `MoveHorizontal(speed)`，按需要调用 `Jump` / `JumpStart` / `JumpEnd`。接触检查仍在 Update，不另外安装输入系统或第二套更新驱动。
3. 运行时改变 Collider 结构后调用 `RefreshColliders()`。缺失、禁用或改成 Trigger 的 Collider 会停止驱动、归还重力并清旧输入与接触；恢复合法结构后再显式刷新。

组件提供跳跃 / 长按中断、多段跳、自动旋转、检测器、事件与泛型包装。停用归还接管前重力，重新启用重新取得 Collider；值先提交再通知，回调中可读取一致状态。

Box / Capsule 按本地 size、绝对缩放与独立角度查询，不重复旋转世界 AABB。实际斜坡、层、负缩放、物理步长和多个对象组合仍应在使用场景中验证，不能由源码无分配推导 Profiler 结果。

## MoveablePlatform

同属 movement2d 包，namespace 与 Runtime 程序集均为 `YLFramework.Platform2D`，包括 Moveable、MoveablePlatform2D 与 PlatformCatcher2D，不依赖 CharacterController2D。公共作者工具属于 `YLFramework.Movement2D.Editor`；原脚本 GUID、Prefab 字段和持久 UnityEvent 保留，不在卸载时删除用户内容。

- 路径为世界坐标 Positions 或初始化时取得的 Transform 快照，按快照总距离插值。支持 PingPong / Loop / Once、delay、scaled / unscaled FixedUpdate、Run / Pause / Stop 与 UnityEvent。
- 先配置再启用或 Run。`PositionType` / `UpdateMode` 选择模式，节点变更通过正式方法重新初始化；空路径、失效 Transform 或 NaN 会给出节点诊断，单点 / 全重合路径在 delay 后定位并停止。
- Pause 只暂停已启动路径，不把 Stop 后的对象隐式启动。回调中停止、改路径或禁用会阻止旧调用栈继续写位置。
- 载人使用当前 Contacts 快照，每个 Rigidbody2D 只搬运一次，排除平台自身与 Trigger；多个 Area / Collider 不重复位移。`RefreshContacts()` 用于显式刷新。
- 路径列表与 Scene 手柄由 Editor 提供，写入经过 SerializedProperty 以保留 Undo / Prefab；不要在运行时修改共享配置来同步表现。

## 两种状态机

| 能力 | 使用方式 |
| --- | --- |
| `YLFramework.FSM.Fsm<TStateId>` | 轻量纯代码状态机，由调用方推进与回收，完整用法见 [FSM](FSM.md) |
| `YLFramework.StateMachine` | 运行层、状态脚本、参数、条件组、层级 / 子状态机与图形作者流程 |

两者不共享运行态或自动结束责任，也不把外部状态机的配置、状态脚本或 GUID 自动转换为 YL 资产。

配置式状态机安装 [com.kano.ylframework.state-machine](../../../com.kano.ylframework.state-machine/README.md) 及主包。namespace 保持不变，Runtime 程序集为 `YLFramework.StateMachine`，Editor 作者工具为 `YLFramework.StateMachine.Editor`；轻量 `YLFramework.FSM` 仍随主包交付。自有 asmdef 中的状态脚本须显式引用新的 Runtime 程序集；生成器不会改写消费方 asmdef、覆盖已有脚本或删除卸载后保留的用户配置。

### 创建与驱动

- 通过 `Assets/Create/YLFramework/Asset FSM/FSMController` / `FSMState` 创建正式资产，窗口为 `Window/YLFramework/Asset FSM/StateMachineWindow`。
- 在自有 GameObject 上挂 `FsmController`，配置运行层，或在 Start 前调用 `AddRuntimeFsmController(source, layerName)`；`UserData` 可在启动前注入。
- 状态脚本继承 `YLFramework.StateMachine.FsmState`，提供 public 无参构造，并由使用工程为 IL2CPP 保留实际类型。不要绑定旧包的状态基类。
- 控制器按 Start 初始化，并在 Update / LateUpdate / FixedUpdate 推进。使用 `GetCurrentStateInfo`、Set / Get Bool / Float / Int / Trigger 与 ResetTrigger；跨层同名参数仍按参数类型参与读写。
- 条件组内 AND、组间 OR；AnyState、Trigger 与层级回调遵循配置。空条件链仅在 `autoSwitch=true` 时自动切换；配置和组件只使用当前序列化字段，不再读取旧字段拼写。

### 生命周期与作者边界

- 每个运行层克隆自己的配置，状态和过渡引用克隆；Close 清活动状态、Trigger、参数与记录但保留可重启克隆，移除层或销毁才 Dispose 自有克隆，不销毁源资产。
- 回调修改层列表或重入不能让旧栈继续推进已移除对象；退出异常仍尝试其余清理再传播。循环切换有每帧状态与链路预算，诊断不依赖 scaled Time。
- Editor 脚本绑定使用显式 GUID，不为缺 GUID 的旧 className 记录扫描补身份；用 Add Script / Create And Add 建立当前绑定。运行时 className 构造仍是正式能力，不要求程序化使用者伪造 Editor GUID。
- 内置状态由标记与内置名称共同决定，不按普通 name 猜测旧格式。暂时缺类型、参数或父节点时保留可修复数据，不自动删除或重写所有资产。
- Delete / Undo / Redo、创建、拖拽、参数与条件组编辑只操作当前资产；文本输入中的 Delete 不删除图节点，内置保护节点与运行时数据不可误删。
- New Scripts 拒绝覆盖已有源码；Create And Add 等待实际导入、编译与程序集重载完成后绑定，不假设下一次 delayCall 已可取得类型。
- 更新自己拥有的旧格式资产前，先用仍支持该格式的版本执行正式打开、保存并保留 `.meta` / GUID，再验证当前配置、Prefab 与实际发布消费者。未知或冻结的历史输入不得原地改写；没有维护中的旧资产时，通过正式作者流程创建新身份的当前资产，不手补 YAML 或从类型名猜脚本 GUID。

运行正确性、原生窗口事件、实际物理、序列化持久回调、IL2CPP 裁剪和人工作者交互是不同验证范围。源仓库的具体执行说明在 Behavior 测试夹具指南与统一测试工具中，不以旧验收回执替代当前产品使用验证。
