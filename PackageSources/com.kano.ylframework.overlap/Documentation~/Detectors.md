# 持续检测与作者工具

> [手册首页](README.md) · [Query 合同](Queries.md) · [二维几何](Geometry2D.md) · [三维几何](Geometry3D.md)

`OverlapDetector2D` / `OverlapDetector3D` 在自身所属的真实物理场景中持续观测，并只对完整快照提交接触差分。它们不是 Cast 控制器，也不会驱动物体移动。

## 配置局部 Union

添加对应 Detector，在 Inspector 配置子形状和 Filter；也可用对应 shape 工厂构造局部数值，再赋值 `new OverlapComposite2D(localShapes)` / `new OverlapComposite3D(localShapes)` 给 `Composite`。

例如在已经取得 `OverlapDetector2D detector` 的主线程方法内：

```csharp
detector.Composite = new OverlapComposite2D(
    OverlapShape2D.Circle(Vector2.zero, 1f));
var nativeFilter = new ContactFilter2D { useTriggers = false };
nativeFilter.SetLayerMask(targetLayers);
detector.Filter = new OverlapFilter2D(nativeFilter);
detector.ConfigureCapacity(32, 256, growBuffer: true);
detector.SamplingMode = OverlapSamplingMode.Manual;
detector.StartDetection();
detector.Sample();
```

此处 `targetLayers` 是调用方明确提供的 LayerMask，Detector 必须已启用。与一次性 Query 不同，Composite 中的 shape 使用 owner 的局部坐标，不能直接填世界中心。

配置复制形状值，不包含 Unity 对象引用或运行状态。共享一个配置不共享缓冲、接触或观察者；单子项也走相同 Union/diff 路径，空 Union 合法。忽略 Collider/root 属于组件及场景或 Prefab instance，不属于共享几何值。

### 变换限制

偏移与 Capsule 球帽中心使用完整 local-to-world 矩阵，包含镜像。Box 可接受非均匀缩放，但变换后各轴必须正交。圆形要求均匀 XY（二维）或 XYZ（三维）缩放，二维体积轴必须仍处于 XY 平面。

奇异变换、旋转叠加非均匀缩放产生的 shear、椭圆/椭球圆形均明确无效，不替换成近似 OBB 或保守 bounds。正交/等尺度检查使用相对 float 容差 `1e-5`。这些配置不提供 compound Cast、任意 polygon overlap 或几何布尔运算。

二维 Sector 与三维 Sector/Cone 的映射、Workspace 预算和目标范围分别见几何页。曲面配置不能绕过相应支持域。

## 采样与停止

默认 `OverlapSamplingMode.Manual`，`runOnEnable=true`。开始运行与发生采样是两件事：

| 入口 / 模式 | 行为 |
| --- | --- |
| `Sample()` | 仅在运行且组件启用时显式采样；true 表示完整提交且通知结束时本周期未失效 |
| `StartDetection()` | 重新开始检测，要求组件已启用 |
| `Stop()` | 停止调度和显式采样，直至再次启动 |
| `Update` | 使用 scaled delta time，在 Update 回调采样 |
| `FixedUpdate` | 使用 scaled 固定步长，在 FixedUpdate 回调采样 |
| `UnscaledUpdate` | Update 中用 `Time.unscaledDeltaTime`，timeScale 为零仍可采样 |

Frequency 必须有限且大于零，每个引擎回调至多采样一次，不为漏掉的历史间隔补跑多次。FixedUpdate 不承诺暂停时引擎仍发送回调。Stop、禁用、销毁及场景退役会释放检测器自有曲面 Workspace，池重启后按需重新创建。

`Sample()` 也可能在提交后，因通知回调中的 Stop、重配置或嵌套 Sample 使周期失效而返回 false。不要只凭 false 推断没有提交或通知，应结合 `LastQuery` 和当前运行状态处理。

## 容量与完整提交

默认初始容量 32、最大 256、自动增长关闭。`ConfigureCapacity(initialCapacity, maxCapacity, growBuffer: true)` 显式启用有界增长；增长后重跑整个 Union，不拼接不同尝试的局部结果。

每个子项以及最终唯一 Union 都必须完整放入容量。原生 raw 恰好填满、过滤或分组后变少仍不能提交。`MayBeTruncated` / `Invalidated` 保留上次完整基线，不将不完整观测变成 Exit。

`LastQuery` 报告最终尝试的唯一 Count、各子项累加 RawCount、Status 和原失效原因。无效子项的尝试也计入累计 raw，Count=0，保留原原因、旧基线，不通知接触。非法序列化配置或不支持的 owner 变换返回 Invalidated 并填写 `ConfigurationError`，不会进入原生查询；属性 setter 参数另行验证。

`LastComplete` 是借用的只读视图，不是历史副本，状态变化后应重新取得。正在分发的回调枚举在嵌套 Stop 时仍安全，但不能把保存的 view 当成永久快照。

刚体分组跨所有子项使用真实 attached body，无 body Collider 独立。不同子项命中同一身份时保留第一个子项的代表，不选择全 Union 的全局最近 Collider。接触顺序由子项顺序和各自原生查询顺序决定，不保证跨进程固定。

## 接触与通知顺序

`Entered` / `Stayed` 的载荷是 `OverlapContact<Collider2D>` 或 `OverlapContact<Collider>`；`Exited` 还携带 `OverlapExitReason`。`EnteredEvent` / `StayedEvent` / `ExitedEvent` 是同样载荷的序列化 UnityEvent，可用于 Inspector 持久绑定。

| Contact 成员 | 含义 |
| --- | --- |
| `Collider` | 本次选择的原始代表 Collider |
| `Target` | 未分组时 Collider，分组时实际 attached body |
| `Identity` / `ColliderInstanceId` | 缓存的身份与 Collider ID，供销毁后通知使用 |
| `Point` | 相对 Detector 位置取得的代表 Collider 原生最近点，Vector3；二维 Z=0，三维保留 Z |

Point 不是法线、穿透深度或连续接触解算。Enter/Stay 更新 Point，Exit 使用最后已提交 Point，即使目标已经销毁。

完整基线在通知前提交；先发旧离开，再发新 Enter，最后发已有 Stay。新接触不会在进入同一个样本中再收到 Stay。

| `OverlapExitReason` | 适用情况 |
| --- | --- |
| `TargetDestroyed` / `TargetDisabled` | 已知目标被销毁/禁用 |
| `FilterChanged` / `Reconfigured` / `Separated` | 完整观测中的其他离开，依相应变更原因区分 |
| `Stopped` | 显式 Stop |
| `Disabled` | Detector 禁用或池归还 |
| `SceneChanged` | 没有经过禁用的物理场景切换 |

Unity 通常在 owner 销毁或移动到另一 local physics scene 前先禁用组件，不额外编造第二次 owner-destroyed Exit。

### 回调内重入与异常

序列化 UnityEvent 适配先于 C# 观察者执行。一个已经开始的 UnityEvent Invoke 是原子监听批次：其中的持久与 Runtime 监听者针对同一已提交快照完成，不用私有 Unity 反射或第二套事件表模拟逐监听者取消。

Stop/禁用立即改变运行意图，但当前 UnityEvent 批次结束后才清接触并发 Exit，避免旧 Enter/Stay 晚于它自己的 Exit。批次内 stop-then-restart 保留新的运行请求，在旧接触释放后重新观测。

回调中 Stop、禁用、销毁、重配置或嵌套 Sample 会取消旧周期剩余 C# 观察者和后续接触通知，但不取消已经开始的 UnityEvent 批次。生命周期清理仍释放全部已提交接触，即使此前通知中途被打断。

观察者异常 fail-fast 向外传播；已提交状态保持一致，dispatch 标志与缓冲仍完成收尾。业务应按自己的 owner 生命周期精确解除 C# 事件和 Runtime UnityEvent 订阅。

## 二维 Area 输入

`SetAreaPositions(Vector3[])` 复制局部节点链，用完整 owner 矩阵变换；`SetAreaTransforms(Transform[])` 复制节点引用数组，借用节点的实时世界位置，不取得节点生命周期。

每个原相邻对形成世界 XY AABB，链不会自动闭合。缺失 Transform 只跳过它所在的原相邻对，不跨缺口连接；有限的零宽/零高对跳过。空链、单节点或全部退化的链提交完整空结果，并释放原接触。

存在的节点若非有限、变换结果非有限或 bounds 溢出，本轮 Invalidated，不能替换 LastComplete。Area 使用与 Union 相同的容量、分组、generation、ownerScene 和完整提交约束。重新赋值 Composite 切回 Union。

## Inspector 编辑

两个 Detector Inspector 都通过 SerializedObject 编辑局部 Union、采样模式/频率、初始/最大容量、增长与 Filter，支持 Undo 和 Prefab instance override。

- 二维持久保存原生 ContactFilter2D 的层、Trigger、深度/法线策略；三维持久保存层与 Ignore/Collide，以及忽略对象、刚体分组、距离排序。
- 程序化赋值 Filter 立即更新同一 backing state，下一次 Sample 不会恢复旧 Inspector 副本。
- 二维输入选择器可编辑 Union、局部 Area 位置或实时 Transform 数组。Area 校验、选中 Gizmos 和逐相邻对预览复用 Runtime 世界 AABB 转换；Union Handle 不暗改 Area 节点。
- 非法序列化值保持可见并在查询前拒绝。共享 Prefab asset Inspector 只读，需要编辑时进入 Prefab Mode 或选择 instance，保存由用户明确执行，不自动保存场景。
- 多选仅编辑序列化设置；Handle 和物理预览要求单选实例。

选中 Gizmos 复用 Composite 的真实世界转换，不用近似 Transform 数学。子项中心 Handle 使用完整父矩阵，Capsule 两端中心一起移动；不支持的 shear/椭圆有诊断，不画误导线框。

### 运行诊断

Inspector 分列实际缓冲容量、唯一 Count / 累计 RawCount、状态/原因、ConfigurationError 和借用的上次完整接触。首个样本前 Query struct 仍是默认值。

部分或无效结果不把旧基线标为“当前完整”。`RawCount - Count` 不是被过滤的 Collider 数，它还包括 Union 重复与刚体分组。Inspector 不构造全部 contact 历史快照。

### 显式预览

预览独立于 Runtime 采样，不持久化。选择一个 Union 子项或 Area 原相邻对，提供调用方容量和明确的世界 Cast 方向/距离。轨迹只表示该子项 Cast，不是 compound Cast。

只有显式采样按钮或主动开启、限频的选中自动采样才执行物理查询。创建 Inspector、OnGUI 与 Gizmo 重绘不查询。

预览在忽略/分组前后各作一次具有相同原生层/Trigger/深度策略的观测。绿色/橙色借用结果不声称列出了被原生过滤排除的对象；关闭 Inspector 即释放每 Inspector 引用，不创建全局调试缓存、Resource authority 或更新服务。

二维全局 Trigger 抑制仍明确显示为前置，工具不切换它。二维 Sector 的圆弧/候选与三维曲面的 Play Mode、精度包络、独立 Workspace 生命周期见相应几何页。
