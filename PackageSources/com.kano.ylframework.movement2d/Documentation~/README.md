# 二维移动使用手册

> [包入口](../README.md)

本包包含独立的 Rigidbody2D 角色控制器和路径平台。它们不要求 Root、Module、输入包、Overlap 或状态机；输入读取和业务生命周期由使用工程决定。

## 前置与程序集

安装主包、`com.kano.ylframework.movement2d` 及包声明的 Unity 内置 physics2d 模块。自有包来源配置见[安装指南](https://github.com/KONOK-A/YLFramework-dev/blob/main/PackageSources/com.kano.ylframework/Documentation~/Installation.md)，该链接指向源码仓库当前文档。

| 能力 | namespace / Runtime asmdef |
| --- | --- |
| 角色移动与接触检测 | `YLFramework.Character2D` |
| 路径、平台、乘客检测 | `YLFramework.Platform2D` |
| 两者的作者工具 | `YLFramework.Movement2D.Editor` 程序集 |

两个 Runtime 各自引用主包 `YLFramework.Runtime`，不互相引用。消费者只引用实际使用的 Runtime；Editor 程序集依赖两个 Runtime 和主包 `YLFramework.Editor`。

## 角色最小接入

1. 在自己的角色上准备 Rigidbody2D，同一刚体下至少有一个已启用的非 Trigger `BoxCollider2D`、`CircleCollider2D` 或 `CapsuleCollider2D`。允许多个附属 Collider，四向检测可复用同一个 Collider。
2. 添加 `YLFramework.Character2D.CharacterController2D`，先配置重力、地面/墙面/顶部检测和跳跃次数，再启用。
3. 在调用方 `FixedUpdate` 调用 `Move(Vector2 delta)` 或 `MoveHorizontal(float speed)`。接触检查由组件的 `Update` 完成，不需要另一套更新驱动。
4. 如需跳跃，开启 `ChecksGround`，根据业务调用 `Jump`，或配对调用 `JumpStart` 与 `JumpEnd`。

下面脚本放在使用工程，Inspector 显式指定角色控制器。启用后沿水平方向提交移动，不读取任何输入设备。

```csharp
using UnityEngine;
using YLFramework.Character2D;

public sealed class HorizontalMotion : MonoBehaviour
{
    [SerializeField] private CharacterController2D motor;
    [SerializeField] private float speed = 2f;

    private void FixedUpdate()
    {
        motor.MoveHorizontal(speed);
    }
}
```

这段代码展示提交位移的责任；它不承诺两个组件的 FixedUpdate 调用顺序。`Move` 是累加待提交位移，不是立即移动或设置速度。

## 移动、跳跃与接触

| API / 数据 | 含义 |
| --- | --- |
| `Move(Vector2 delta)` | 累加世界坐标位移，不是速度；停用或 Collider 无效时忽略 |
| `MoveHorizontal(float speed)` | 速度乘 `Time.fixedDeltaTime`；开启地面检测且落地时沿地面切线移动 |
| `Jump(float speed)` | 设置竖直初速度，清除已排队位移和观测速度；受地面检测与 `maxJumpCount` 限制 |
| `JumpStart(speed, jumpAbortSpeedReduction)` | 发起可提前松开的跳跃，负额外减速度按零处理 |
| `JumpEnd()` | 标记松开，后续 FixedUpdate 在仍上升时施加额外减速，不直接清零竖直速度 |
| `ResetSpeedVertical()` | 只归零组件维护的竖直速度，不清空排队位移 |
| `IsGrounded` / `IsTouchingWall` / `IsTouchingCeiling` | 最近接触检测的状态 |
| `GroundInfo` / `WallInfo` / `TopInfo` | 对应检测命中信息 |
| `Velocity` | 此时可观察的刚体位置差除以固定步长，不把请求位移伪装为已完成速度 |

组件在 FixedUpdate 中积分竖直速度、提交排队移动，再处理松开跳跃的额外减速；移动通过 `Rigidbody2D.MovePosition` 提交。`Update` 刷新检测，自动旋转使用已经取得的地面信息。

`onGroundValueChange`、`onTouchWallValueChange`、`onTopValueChange`、`onVelocityValueChange` 和 `onJumpCountValueChange` 可配置 UnityEvent。值先提交再通知，回调可读取一致状态；回调中禁用等操作会使旧处理流程失效。

### Collider 与重力

运行时增加、移除或替换 Collider 后显式调用 `RefreshColliders()`。组件按世界包围盒极值选择四向检测 Collider；缺失、禁用或变成 Trigger 的结构会停止驱动、归还接管前的刚体重力，并清空旧输入、跳跃与接触。恢复合法结构后仍须显式刷新。

地面检测开启时，空中由组件积分重力并暂时将刚体 `gravityScale` 置零；已有合格下方接触且下降时不再追加下落位移。禁用归还启用时记录的重力并清理状态，重新启用重新获取 Collider 和位置起点。不要由另一套控制器同时接管这些值。

Box / Capsule 查询使用本地 size、绝对缩放和独立角度，不重复旋转世界 AABB。斜坡、层碰撞矩阵、负缩放、固定步长及多个物体组合仍须在实际使用场景中验证。

## 世界路径

`Moveable` 位于 `YLFramework.Platform2D`，默认直接设置 Transform 世界位置。Inspector 中先配置 `PositionType`、节点、速度、延迟和运行模式；默认 `runOnAwake=true`，因此自动运行所需数据必须在 Awake 前准备好。需要运行时组装时，先关闭自动运行，再显式配置并 `Run()`。

- `PositionType.Positions` 使用世界坐标；`PositionType.Transforms` 在初始化时读取节点的世界位置快照，不持续追随节点。
- 沿相邻节点总距离插值，支持 `MoveType.PingPong`、`Loop`、`Once`。速度必须有限且大于零，Delay 必须有限且非负。
- `UpdateMode.Normal` 使用 `Time.fixedDeltaTime`，`UnscaledTime` 使用 `Time.fixedUnscaledDeltaTime`，但两者都由 FixedUpdate 驱动。UnscaledTime 不意味着 `timeScale=0` 时引擎仍发送 FixedUpdate。
- 空路径、失效 Transform、NaN/Infinity 或总长度溢出会给出诊断；单点或全重合路径在延迟后定位并停止。
- `Positions` / `Transforms` 是可写路径源，不是运行快照。直接修改列表、节点 Transform 或 `PositionType` 不自动重建当前快照。使用 `AddPosition`、`RemovePosition`、`ClearPosition` 重建，或先 `Stop()` 再 `Run()`。
- `Distance` / `Time` 按当前路径源估算，不一定等于已经建立的运行快照；Time 不含启动延迟。

## 运行、暂停与事件

| 操作 | 结果 |
| --- | --- |
| `Run()` | 未运行时重建快照并从零计时；对暂停路径调用也会重新开始，运行中调用不重启 |
| `Pause(true)` / `Pause(false)` | 暂停/恢复已经启动的路径，保留进度和延迟；不能把 Stop 后的对象隐式启动 |
| `Stop()` | 清理运行状态和计时，不将对象移回起点；重复 Stop 不再通知 |
| 禁用组件 | 不再收到 FixedUpdate，但不自动 Stop 或重置计时；池归还若要求重新开始，应显式 Stop |

`onStartRun` 表示成功建立路径并标记运行，不表示首个位置已提交。`onMove` 参数是目标世界位置，通知在真正提交前发生；回调中停止、重建路径、禁用或销毁会阻止旧调用栈继续写位置。`onPause` 参数是是否暂停，`onStop` 在清理状态后通知。

## 移动平台与乘客

`MoveablePlatform2D : Moveable` 要求同物体有 `PlatformCatcher2D`。检测器的 `areas` 是局部 `from` / `to` 线段数组，各线段转换为世界坐标后执行 Linecast，不是 Overlap 的 Area 框。`Contacts` 是最近检测列表；`RefreshContacts()` 用于显式刷新。平台移动使用该列表的快照，不在每个乘客搬运时重新检测。

- 平台先提交自己的世界位置，再把本次 XY 位移直接加到乘客 `Rigidbody2D.position`。
- 排除 Trigger、平台自身及其层级内刚体；同一个刚体即使被多个 Area 或 Collider 命中，也只搬运一次。
- 无刚体的 Collider 不作为可携带乘客。平台不依赖 CharacterController2D，也不为乘客额外执行障碍 Cast。
- 如需严格控制检测与移动的先后，使用工程应明确刷新时机；最近检测快照不等于此刻重新观测的接触。
- 乘客 Linecast 使用默认 PhysicsScene2D，每条线段的缓冲上限为 10 个命中，不保证覆盖所有乘客。自动检测在 Update 中按接触变化和限频刷新执行，与平台 FixedUpdate 不保证同步；停用检测器会清空候选。

## 编辑与所有权

本包 Editor 提供角色与平台 Inspector、路径列表、Scene 手柄和 Gizmos。路径编辑通过 SerializedProperty 保留 Undo 与 Prefab override，不以修改共享配置来同步运行时表现。

场景、Prefab、路径节点和持久 UnityEvent 由使用工程持有。卸载本包不会清除它们，卸载前须处理组件、API、asmdef 与持久绑定引用。框架不接管应用输入、其他移动系统或对象池的业务重置。
