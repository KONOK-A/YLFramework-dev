# 检测、移动与资产状态机

> [文档首页](../README.md) · [架构](../Architecture.md) · [轻量 FSM](FSM.md) · [Editor 扩展](Editor.md)

Overlap、CharacterController2D、MoveablePlatform 与配置式 StateMachine 是独立使用的能力，不要求 Root、Module、旧宿主扩展或输入包。它们使用自己的组件与资产，不自动重绑其他框架的 Scene、Prefab 或状态类型。

## Overlap

namespace 为 `YLFramework.Overlap`，实现位于 `Runtime/Utilities/Overlap`，配套绘制与 Inspector 位于 Editor。

- 在自有 GameObject 上添加 Point / Circle / Box / Capsule / Area 2D 或 Box / Sphere 3D 检测组件，明确 LayerMask、形状、频率与 scaled / unscaled 模式。
- 查询排除 Trigger。当前公开结果为 `results`，它是组件复用的可写列表，每次查询清空并逐项填充，不是可长期保留的独立快照；enter 回调中还可能尚未收集完整。调用方不应改写组件列表，需要跨查询保留时自行复制。`OverlapHit<T>.point` 包括 3D 查询也使用 Vector2，不按直觉修改其协议。
- `CheckOverlap()` 可手动调用；同一组件在自身事件中递归查询会拒绝，不影响其他组件查询。
- 禁用立即清空结果，不补发 exit；重新启用后的首次查询重新 enter，保留显式 `IsRunning` 设置。关闭 `IsRunning` 只停止自动检测，不阻止手动查询，也不取消已经进入的查询。
- 容量为 100。Editor / Development 达到容量时，每次启用周期最多提示一次可能截断，不保证每次都发生实际丢失；Release 不走该日志路径。

## CharacterController2D

namespace 为 `YLFramework.Character2D`，实现位于 `Runtime/Utilities/CharacterController2D`。

1. 在同一 Rigidbody2D 下准备非 Trigger 的 Box / Circle / CapsuleCollider2D，允许多个 attached Collider；先配置再启用。
2. 在调用方 FixedUpdate 使用 `Move(Vector2 delta)` 或 `MoveHorizontal(speed)`，按需要调用 `Jump` / `JumpStart` / `JumpEnd`。接触检查仍在 Update，不另外安装输入系统或第二套更新驱动。
3. 运行时改变 Collider 结构后调用 `RefreshColliders()`。缺失、禁用或改成 Trigger 的 Collider 会停止驱动、归还重力并清旧输入与接触；恢复合法结构后再显式刷新。

组件提供跳跃 / 长按中断、多段跳、自动旋转、检测器、事件与泛型包装。停用归还接管前重力，重新启用重新取得 Collider；值先提交再通知，回调中可读取一致状态。

Box / Capsule 按本地 size、绝对缩放与独立角度查询，不重复旋转世界 AABB。实际斜坡、层、负缩放、物理步长和多个对象组合仍应在使用场景中验证，不能由源码无分配推导 Profiler 结果。

## MoveablePlatform

namespace 为 `YLFramework.Platform2D`，包括 Moveable、MoveablePlatform2D 与 PlatformCatcher2D，不依赖 CharacterController2D。

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

### 创建与驱动

- 通过 `Assets/Create/YLFramework/Asset FSM/FSMController` / `FSMState` 创建正式资产，窗口为 `Window/YLFramework/Asset FSM/StateMachineWindow`。
- 在自有 GameObject 上挂 `FsmController`，配置运行层，或在 Start 前调用 `AddRuntimeFsmController(source, layerName)`；`UserData` 可在启动前注入。
- 状态脚本继承 `YLFramework.StateMachine.FsmState`，提供 public 无参构造，并由使用工程为 IL2CPP 保留实际类型。不要绑定旧包的状态基类。
- 控制器按 Start 初始化，并在 Update / LateUpdate / FixedUpdate 推进。使用 `GetCurrentStateInfo`、Set / Get Bool / Float / Int / Trigger 与 ResetTrigger；跨层同名参数仍按参数类型参与读写。
- 条件组内 AND、组间 OR；AnyState、Trigger 与层级回调遵循配置。空条件链仅在 `autoSwitch=true` 时自动切换；旧序列化拼写由当前字段迁移声明处理，不用于新代码。

### 生命周期与作者边界

- 每个运行层克隆自己的配置，状态和过渡引用克隆；Close 清活动状态、Trigger、参数与记录但保留可重启克隆，移除层或销毁才 Dispose 自有克隆，不销毁源资产。
- 回调修改层列表或重入不能让旧栈继续推进已移除对象；退出异常仍尝试其余清理再传播。循环切换有每帧状态与链路预算，诊断不依赖 scaled Time。
- Editor 脚本绑定使用显式 GUID，不为缺 GUID 的旧 className 记录扫描补身份；用 Add Script / Create And Add 建立当前绑定。运行时 className 构造仍是正式能力，不要求程序化使用者伪造 Editor GUID。
- 内置状态由标记与内置名称共同决定，不按普通 name 猜测旧格式。暂时缺类型、参数或父节点时保留可修复数据，不自动删除或重写所有资产。
- Delete / Undo / Redo、创建、拖拽、参数与条件组编辑只操作当前资产；文本输入中的 Delete 不删除图节点，内置保护节点与运行时数据不可误删。
- New Scripts 拒绝覆盖已有源码；Create And Add 等待实际导入、编译与程序集重载完成后绑定，不假设下一次 delayCall 已可取得类型。

运行正确性、原生窗口事件、实际物理、序列化持久回调、IL2CPP 裁剪和人工作者交互是不同验证范围。源仓库的具体执行说明在 Behavior 测试夹具指南与统一测试工具中，不以旧验收回执替代当前产品使用验证。
