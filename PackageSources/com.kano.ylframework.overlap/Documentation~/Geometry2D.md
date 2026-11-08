# 二维扇形与圆环

> [手册首页](README.md) · [Query 合同](Queries.md) · [持续检测](Detectors.md)

二维 Sector 是世界 XY 平面内的填充区域，通过解析窄相处理真实导出形状，不是把圆弧切成多边形，也不是遮挡测试、斩击动画或连续扫掠。

## 定义与 Workspace

`OverlapShape2D.Sector(center, outerRadius, angleDegrees, innerRadius = 0, rotationDegrees = 0)`：

- 前向为 +X 绕 Z 旋转 `rotationDegrees`，`angleDegrees` 是开口角，不是旋转角。
- `OuterRadius > 0`，`0 <= InnerRadius < OuterRadius`，`0 < AngleDegrees <= 360`，全部有限。
- 内半径为零包含顶点；360 度没有径向边，分别表示圆盘或完整圆环。
- `Angle` 表示 Z 旋转，`RotationDegrees` 是其明确命名的属性；不要把它当 aperture。

Query 最后一个参数必须提供可复用的 `OverlapQueryWorkspace2D`。以下是主线程方法体内的单次调用形式，center、scene、filter、callerResults 均由调用方明确提供：

```csharp
using var workspace = new OverlapQueryWorkspace2D(
    maxShapesPerCollider: 256,
    maxVerticesPerShape: 4096,
    geometryTolerance: 0.00001f);
var region = OverlapShape2D.Sector(
    center, 5f, 120f, innerRadius: 1f, rotationDegrees: 30f);
var result = OverlapQuery2D.Query(scene, region, filter, callerResults, workspace);
```

需要重复查询时，应在循环外创建并由单一 owner 持有，owner 结束时 Dispose，而不是每次查询创建。它是普通 `sealed IDisposable`，不是组件、全局缓存或共享服务；只保留底层几何存储和数值顶点，不保留 Collider、body、场景或目标几何缓存。

已释放或重入使用在触及输出前抛出，查询中 Dispose 也拒绝。不带 Workspace 的 Sector Query 返回空 `Invalidated / WorkspaceRequired`。Sector Cast 返回空 `Invalidated / UnsupportedCast`，不执行原生 Cast。

## 候选与解析窄相

broad phase 在目标物理场景执行原生外圆查询，半径按 Workspace 的世界单位 tolerance 向外扩展并向外舍入为 float。原生候选满容量始终意味着 MayBeTruncated，即使所有候选在洞内、被忽略或被刚体分组消除。

每次查询对每个允许候选重新调用 `Collider2D.GetShapes(group, shapeIndex, 1)`，遍历 Polygon / Composite 的全部形状。不使用 hash 缓存、场景扫描、辅助 Collider 或全局物理设置。

| 导出形状 | 窄相解释 |
| --- | --- |
| Circle | 点加原生 radius |
| Capsule | 线段加 radius |
| 凸 Polygon | 填充凸多边形加 radius |
| Edges | 每条线段加 radius，不取填充凸包 |

顶点仅使用返回的 `localToWorldMatrix`：body-local 几何取得 body 位姿，无刚体的静态几何已经是世界坐标。不再次叠加 Collider Transform、offset 或 scale。

计算处理真实内外圆弧、有限径向边、线段与圆弧的相交/相切以及两个方向的包含关系，不多边形化圆弧。中间计算使用 double，但输入仍是 Unity 导出的 float 几何；接受距离不大于 `shape.radius + geometryTolerance`。这是浮点解析策略，不是与 Unity native narrow phase 逐位相等。

原生 broad phase 与 Filter 仍决定候选准入。容差无法恢复引擎未返回的候选或未同步的 Transform，API 不执行项目级 SyncTransforms。

## 大坐标与数值认证

body 平移减 Sector 中心先于旋转局部顶点计算，以保留共同大世界平移下的小 body-local 偏移。圆交点和点到线段距离使用规范化线坐标/法向距离，不把舍入后的巨大线段参数再乘回端点。

残余求和误差以及保守线/根/区间舍入界，与配置 tolerance 的一部分比较，每项受检操作使用 1/16。无法认证的严重消去返回 `InvalidGeometry`，不扩大几何容差，也不编造 Complete 命中或未命中。这不保证每个有限坐标组合、每个引擎极端坐标查询都能被认证。

## 支持范围与有限预算

支持 CircleCollider2D、CapsuleCollider2D、BoxCollider2D、PolygonCollider2D、EdgeCollider2D，以及 CompositeCollider2D 的 Polygons / Outlines，包括引擎为非均匀缩放目标生成的几何。这是目标几何范围，不等于 Detector 局部查询形状可以任意非均匀缩放。

Workspace 预分配一个 native shape 槽和配置顶点存储。在提取前检查 `Collider2D.shapeCount <= maxShapesPerCollider`，并用可信的每形状保守顶点上界检查 `maxVerticesPerShape`：

| 目标类型 | 提取前的顶点上界 |
| --- | --- |
| Circle | 1 |
| Capsule | 2 |
| Box / Polygon / Composite Polygons | `Physics2D.MaxPolygonShapeVertices` |
| Edge | `EdgeCollider2D.pointCount` |
| Composite Outlines | `CompositeCollider2D.pointCount + 1`，闭链可能重复首点 |

任一上界超预算即返回空 `Invalidated / GeometryBudgetExceeded`，即使某一个凸子形状实际上能放入也不绕过检查。集合 capacity 不是软限制；默认 4096 顶点也是有限预算，不代表无限精度或零分配测量。

TilemapCollider2D、CustomCollider2D 与未知类型缺少可信提取前上界，明确返回 `UnsupportedGeometry`。缺失、畸形、非有限导出几何或 native 读取失败返回 `InvalidGeometry`，不会静默成为完整未命中。忽略/root 过滤先执行，因此明确忽略的未知 Collider 不必提取。

失败清空全部输出，保留已观测 RawCount，不允许 Detector 提交新接触。CPU 工作受每个候选的 native shape/顶点预算约束；是否达到稳态零分配仍需实际测量。

## Detector 与预览

二维 Detector 的局部 Sector 遵循圆形的均匀 XY、无 shear 转换，镜像前向也参与转换；内外半径、aperture 与旋转分别序列化。含 Sector 时惰性创建 Detector 自有 Workspace，纯 primitive Union 不创建 shape group。

`ConfigureGeometryWorkspace(256, 4096, 0.00001f)` 配置有限预算与容差。改预算、Stop、禁用、销毁或场景退役会释放自有存储，下一次运行样本再创建；无效或超预算保留 LastComplete，不产生假离开。

Inspector 预览使用独立 Workspace，同样遵守预算、容差和二维全局 Trigger 前置。raw 列表是原生外圆候选，橙色也可能表示位于扇形外或圆环洞内。线框使用真实内外弧和有限径向边：圆盘省略内弧，完整圆环省略径向边。Sector Cast 明确不可用，不绘制虚构扫掠线框。完整作者流程见[持续检测与作者工具](Detectors.md)。
