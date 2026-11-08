# 三维扇区、圆环与圆锥

> [手册首页](README.md) · [Query 合同](Queries.md) · [持续检测](Detectors.md)

三维 Sector / Cone 的窄相使用有限预算、保守生成的凸近似，不是解析曲面相交、native 逐位等价或闭合接触/相切保证。需要理想曲面与精度带之内外严格区分的业务，必须理解以下边界。

## 世界形状

`OverlapShape3D.Sector(center, rotation, outerRadius, angleDegrees, height, innerRadius = 0)` 表示局部 XZ 填充扇区，+Z 是中间方向，+Y 是以中心对称的厚度方向。

- 外半径与 height 为有限正数，`0 <= InnerRadius < Radius`，`0 < AngleDegrees <= 360`。
- 360 度表示无径向接缝的圆盘或圆环，不用一条缝封住洞。
- `AnnularSector` / `Ring` 是 Sector kind 的命名工厂；Sector 和 Ring 提供 identity rotation 重载。

`OverlapShape3D.Cone(apex, rotation, height, baseRadius)` 是沿 +Z 的有限闭底圆锥，Center 是 apex，Radius 是闭底半径。`ConeFromAngle` 只把 `(0, 180)` 内完整开口角换算为 `baseRadius = height * tan(angleDegrees / 2)`，不是无限锥或球面扇区。

这些都是纯世界数值，不是 Transform、Mesh、Collider 或业务 FOV/遮挡测试。它们只支持 Query，Cast 在原生调用前返回空 `UnsupportedCast`。

## Workspace 与 Play Mode

Query 最后一个参数显式传入 `OverlapQueryWorkspace3D`。以下是主线程、Play Mode 方法体内的单次形式，center、rotation、physicsScene、filter、callerResults 均由调用方提供：

```csharp
using var workspace = new OverlapQueryWorkspace3D(
    requestedBoundaryError: 0.01f,
    maxSegments: 128,
    maxParts: 128,
    maxHierarchyDepth: 64);
var region = OverlapShape3D.Sector(
    center, rotation, 5f, 120f, 2f, innerRadius: 1f);
var result = OverlapQuery3D.Query(
    physicsScene, region, filter, callerResults, workspace);
```

重复查询应由单一 owner 在循环外创建并复用。Play Mode 内不传 Workspace 返回 `WorkspaceRequired`；曲面查询在 Play Mode 外返回 `RequiresPlayMode`。Workspace 构造在 Play Mode 外直接抛出，不为 Editor 静默创建场景。原生 Sphere/Box/Capsule Query/Cast 不受此曲面目标范围替代。

## 凸近似与精度预算

Sector 每个角单元不超过 90 度。内半径为零时用三角柱，否则用四边形柱；圆环不 cook 成一个填洞的凸包。单元角 d 下，外端点半径为 `R * sec(d/2)`，内端点半径为 r：

- 理想外侧超出量：`R * (sec(d/2) - 1)`。
- 理想洞内侵入量：`r * (1 - cos(d/2))`。

Cone 使用闭合、外切的正 N 边棱锥，理想径向超出量为 `R * (sec(pi/N) - 1)`。N 不超过 128 时最多 254 个三角形，更大 N 拆成 N 个各含四个三角面的四面体；每个生成部件均为可读凸网格且不超过 255 个三角形。

`requestedBoundaryError` 是正的世界单位生成细分预算，不是 Collider 接触容差。它不约束 float 舍入、Unity cooking、native penetration、broad phase 或总引擎误差。近似带内、理想圆形区域外的目标可能命中；不会通过额外球体或膨胀 native tolerance 补造精确零距离接触。更小近似带需要调用方支付分段与部件预算。

分段、部件或层级预算不足返回 `GeometryBudgetExceeded`，不悄悄降低精度。float 折叠或不可 cook 的部件返回 `CookingFailed`。`BakeMesh` 没有返回值，Workspace 还使用同一私有世界内的内部穿透正控制验证生成部件，但这不是全部 cooking/边界误差的证明。

### Broad Phase 的保守边界

候选 Box 覆盖扩展部件与完整厚度/长度。Cone 使用以 apex 为中心的世界 AABB，包含旋转后的对称局部包络 `[-R,R] x [-R,R] x [-H,H]`。

double 行和界向外舍入成 float 完整尺寸，再认证实际传入原生查询的 float 半尺寸仍覆盖该界；不可表示时在 cooking 前返回 `InvalidGeometry`。保留精确 apex 避免 float 中点平移缩小候选域。

该 Box 有意不紧致，会纳入 apex 后方或旋转锥外更多候选，增加复制、ComputePenetration 工作、raw 饱和或不支持目标的准入概率；Filter、窄相和精度预算不变。此认证只针对生成位姿/包络，不是总 native cooking/float 接触误差认证。距离排序仍以 apex 为中心。

## 支持的目标

窄相只接受具有有限、非退化几何，且完整 Transform 祖先可被忠实复制的 `BoxCollider`、`SphereCollider`、`CapsuleCollider`。允许无 body、kinematic、dynamic body，但 attached Rigidbody 必须 `interpolation == RigidbodyInterpolation.None`。插值/外推渲染位姿不是已认证的物理快照位姿，返回 `UnsupportedGeometry`。

所有 MeshCollider，包括 convex、TerrainCollider、CharacterController 和未知类型均为 `UnsupportedGeometry`。不从 render/shared mesh 表面编造已 cook 凸包。

应选择业务目标/hurtbox LayerMask，不要查询全部环境再期待不支持对象被静默忽略。明确忽略的候选不复制；其他允许候选即使中心或 bounds 看似在理想区域外、或圆环洞内，也可能使整个查询无效。

## 私有物理世界与目标复制

每个 Workspace 拥有一个 additive local PhysicsScene、私有凸部件、三个 primitive proxy，以及惰性构建的纯 Transform 链。ComputePenetration 的两个 Collider 在同一个私有世界中 active/enabled，不依赖 inactive helper 或跨物理世界穿透行为。

不会在目标世界启用 helper，不实例化、移动或销毁原目标 GameObject，不克隆它的脚本或 Rigidbody。

每个允许候选、每次查询都会重新读取 primitive 参数和完整祖先 local TRS，包含 Collider 自身，深度不超过 `MaxHierarchyDepth`。用私有 Transform 重建链；非有限、奇异或世界矩阵不一致时拒绝。不以 `Abs(lossyScale)` 替代真实层级，也不缓存目标旧几何/位姿。

该复制协议保留旋转、镜像、非均匀缩放及 compound body 的目标层级几何；它不放宽局部查询形状的均匀缩放约束。源物理 broad phase 位姿同步仍由调用方负责。

私有世界无 Rigidbody/脚本且从不 Simulate；API 不调用全局 SyncTransforms、SetActiveScene 或物理设置 setter。不创建全局 Manager/Pool、不扫描场景，也不在无 Workspace 的重载中暗建工作区。

## 缓存、诊断与释放

Workspace 只缓存一个生成区域，以 kind/尺寸和不可变精度配置为 key，不包含 pose。连续只改位姿可复用部件；改变或交替尺寸会重建自有几何。替换失败使缓存失效，不能把旧部件与新 broad radius 配对。这不承诺交替 Union 免费 recook、逐候选启用免费或零 GC。

| 诊断 | 含义 |
| --- | --- |
| `SegmentCount` / `RequiredPartCount` / `PartCount` | 当前生成区域的分段、所需与实际部件数 |
| `GeneratedBoundaryError` / `GeometryReason` | 生成细分误差与生成原因，不是总引擎误差 |
| `CookCount` / `GeometryBuildCount` | bake 尝试与几何构建计数 |
| `TargetCopyCount` | 目标复制计数 |
| `CreatedObjectCount` | 累计创建数，不是瞬时原生内存 |
| `CookingError` | 失败部件、float 退化、native 内部正控制或 cook 异常/参数诊断 |
| 查询 `InvalidationReason` | 与生成诊断分开，说明本次快照为何失效 |

native 错误日志不被抑制。Part budget 约束当前区域，不约束 Destroy 的帧末原生内存积压。

`Begin` 拒绝重入和已 Dispose 使用；查询中不能 Dispose。查询结束清除借用的祖先引用并禁用 proxy。Dispose 幂等、立即拒绝后续查询、禁用自有 Collider，排队销毁自有对象/网格，再对自己拥有的精确场景发起 `UnloadSceneAsync`；不销毁借用网格。

`ReleaseOperation` 暴露异步释放完成，不能将 Dispose 返回解释为原生内存/场景瞬间释放。每个 Workspace 会增加一个场景及相应 SceneManager/资源活动，不承诺零 SceneManager 副作用。

原生 raw 饱和在全部候选被窄相排除后仍为 MayBeTruncated。允许目标未知、预算或 cooking 失败时清空输出、保留已观测 RawCount、使快照失效，Detector 不提交接触。分组、排序和返回身份都在窄相后使用原候选，而不是 proxy。

## 局部配置与 Editor

Detector 的 Sector/Cone 局部子项要求 owner 的均匀、无 shear XYZ 映射。均匀镜像利用区域 X 反射对称性，以等价 proper rotation 保留变换后的 forward/up；非均匀 owner 不解释为椭圆锥/扇区。这是查询配置限制，不是目标层级复制限制。Box 的非均匀正交转换与 Capsule 球帽中心协议另按各自规则处理。

`ConfigureGeometryWorkspace(0.01f, 128, 128, 64)` 设置误差/分段/部件/层级预算。Detector 惰性拥有 Workspace；Stop、禁用、销毁、场景退役和预算替换都会 Dispose，池重启创建新私有场景。当前几何计数描述最后缓存子项，不是全部 Union 的历史总和。

Inspector 保存真实内外半径、aperture、厚度、apex/长度/底半径与 orientation。尺寸 Slider 分别报告变化，未编辑维度保留精确 float 与 override，包括薄环、低于 .001 度开口和 360 度圆环；非法内外半径冲突被拒绝，不夹紧无关字段。

中心/尺寸 Handle 与 Runtime 转换一致，使用 SerializedObject、Undo 和 Prefab override。线框标注理想边界与请求的细分包络，不表示精确 cooked surface。Play Mode 外只可编辑 Gizmos/Handle，曲面预览按钮以 RequiresPlayMode 禁用，不分配物理场景；Play Mode 预览拥有独立 Workspace，在取消选择、关闭 Inspector 或退出 Play 时释放。Runtime 不引用 UnityEditor。
