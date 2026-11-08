# Query 与 Cast

> [手册首页](README.md) · [二维曲面几何](Geometry2D.md) · [三维曲面几何](Geometry3D.md) · [持续检测](Detectors.md)

Query 观测某个世界区域的重叠对象，Cast 沿明确世界方向扫掠单一形状。所有调用均为主线程同步操作，不创建持续监听，也不替调用方同步或模拟物理世界。

## 最小查询

以下完整方法接受可复用数组，只查询默认二维物理场景的非 Trigger 对象。数组由外部 owner 分配和复用，不应在每帧调用前重新分配。

```csharp
using UnityEngine;
using YLFramework.Overlap;

public static class NearbyQuery
{
    public static OverlapQueryResult2D Query(
        Vector2 center, LayerMask layers, Collider2D[] results)
    {
        var nativeFilter = new ContactFilter2D { useTriggers = false };
        nativeFilter.SetLayerMask(layers);
        var filter = new OverlapFilter2D(nativeFilter, sortByDistance: true);
        var shape = OverlapShape2D.Circle(center, 2f);
        return OverlapQuery2D.Query(shape, filter, results);
    }
}
```

调用后先检查返回的 Status，再消费 `[0, Count)`。`MayBeTruncated` 的结果可以用于明确允许部分结果的业务，但不能提交“未见对象已经离开”的判断。

## 世界形状与缓冲

| 入口 | 支持形状 | 调用方输出 |
| --- | --- | --- |
| `OverlapQuery2D.Query` | Point、Circle、Box、Capsule；Sector 需 Workspace | `Collider2D[]` |
| `OverlapQuery2D.Cast` | Point 为射线，Circle / Box / Capsule 为单形状扫掠 | `RaycastHit2D[]` |
| `OverlapQuery3D.Query` | Sphere、Box、Capsule；Sector / Cone 需 Workspace | `Collider[]` |
| `OverlapQuery3D.Cast` | Sphere / Box / Capsule | `RaycastHit[]` |
| `OverlapQuery3D.Raycast` | 独立 origin、direction、distance | `RaycastHit[]` |

各入口都有显式 `PhysicsScene2D` / `PhysicsScene` 和默认场景重载。Query 接收 `shape, filter, results`；曲面 Query 的 Workspace 是最后一个参数。Cast 接收 `shape, direction, distance, filter, results`，不能省略运动方向或从 Transform 隐式推导。

- Box 的 size 是旋转前完整尺寸，不是 half extents。二维角度是 Z 旋转，三维 Box 接收 orientation。
- 二维 Capsule 使用完整尺寸、`CapsuleDirection2D` 和 Z 旋转，轴长不能短于直径，二者相等时为圆。
- 三维 Capsule 接收两个球帽中心与半径，不是外侧端点；球帽中心重合时为球。
- Point 没有体积；默认二维 shape 是原点 Point，默认三维 shape 因半径为零而非法。
- 三维曲面与二维 Sector 的形状含义、目标范围不同，分别见其几何页。它们的 Cast 在原生调用前返回 `Invalidated / UnsupportedCast`，不是复合扫掠。

数组不能为空或长度为零，API 不扩容也不保留数组。成功或返回查询状态后，Count 以后的槽位清空；几何、方向、距离和参数校验失败则在修改输出前抛出。

非有限几何、非正体积尺寸、不可用方向、负数或非有限 Cast 距离被拒绝；零 Cast 距离合法。深度过滤允许无穷作为开放边界，但不能 NaN 或上下界颠倒。

## Filter 与场景前置

二维用 `OverlapFilter2D(ContactFilter2D, ...)`，由原生 ContactFilter2D 控制层、Trigger、深度和适用的法线策略。可选参数包括 `ignoredCollider`、`ignoredRoot`、`groupByRigidbody`、`sortByDistance`。

三维用 `OverlapFilter3D(layerMask, triggerInteraction, ...)`，显式层掩码，Trigger 默认 `QueryTriggerInteraction.Ignore`，可选 Collide，拒绝 UseGlobal。

`ignoredRoot` 排除根本身及所有后代。已销毁的忽略对象按 Unity fake-null 视为不存在，API 不保活它们。忽略过滤在曲面几何提取/复制前执行，被明确忽略的不支持目标不需要进入窄相。

无效或卸载的物理场景返回空 `Invalidated / InvalidScene`。二维若 `Physics2D.queriesHitTriggers=false`，即使 ContactFilter2D 的 `useTriggers=true`，Unity 仍会全局抑制 Trigger；此时返回空 `Invalidated / TriggerQueriesDisabled`，不宣称完整未命中，也不会替项目打开该设置。

二维射线起点位于 Collider 内的行为仍受 `Physics2D.queriesStartInColliders` 控制。三维起始重叠是否产生命中和法线取决于原生形状查询，不人工补造接触或法线。

物理位姿同步由调用方按 Unity 的正常合同负责。API 不执行项目级 `Physics2D.SyncTransforms` / `Physics.SyncTransforms`、Simulate 或物理设置修改；容差也不能找回未同步而被原生 broad phase 漏掉的对象。

## 完整性与失败

`OverlapQueryResult2D/3D` 提供 Count、RawCount、Status、InvalidationReason。

| 状态 | 当前含义 |
| --- | --- |
| `Complete` | 原生候选快照未饱和，所有已纳入候选按相应几何合同成功处理 |
| `MayBeTruncated` | RawCount 达到原生数组容量，包括恰好填满；无法证明没有更多候选 |
| `Invalidated` | 未取得可靠快照；读取原因，不用 Count 推断命中或离开 |

RawCount 是窄相、忽略过滤和刚体分组前的原生容量使用量。即使过滤后 Count 为零，原生饱和也仍是 MayBeTruncated。曲面处理失败清空整份输出，保留已观测 RawCount；Detector 不会提交这些结果。

| `InvalidationReason` | 处理方向 |
| --- | --- |
| `None` | 没有失效原因；仍须读 Status 判断是否截断 |
| `InvalidScene` | 核对场景有效性与生命周期 |
| `TriggerQueriesDisabled` | 由应用决定是否启用二维全局 Trigger 查询或改为排除 Trigger |
| `WorkspaceRequired` | 显式提供对应曲面 Workspace |
| `UnsupportedGeometry` | 选择受支持目标及业务 LayerMask，不静默忽略未知几何 |
| `GeometryBudgetExceeded` | 调整形状/顶点/分段/部件/层级预算或减少输入 |
| `InvalidGeometry` | 检查非有限/退化/导出失败、转换与数值认证问题 |
| `UnsupportedCast` | 改用受支持单形状 Cast，不把离散 Query 当连续碰撞 |
| `RequiresPlayMode` | 三维曲面查询与 Workspace 要求 Play Mode |
| `CookingFailed` | 读取 Workspace 的 CookingError，检查生成形状与 native cooking |

## 分组、排序与成本

`groupByRigidbody` 按实际 attached body 分组，没有 body 的 Collider 保持独立；一次 Query 在同组中保留同一距离度量下最近的代表。`sortByDistance` 对重叠使用形状中心到目标世界 AABB 的平方距离，二维仅 XY、三维 XYZ，不是精确表面距离；Cone 的中心为 apex。Cast 使用原生命中距离。

距离并列使用当前会话内有符号 Collider instance ID。未启用分组和排序时保留原生 Collider 去重顺序；不能承诺跨进程固定顺序，或从已经截断的数组中选出全局最近结果。

可选分组/排序使用原地 heapsort，比较次数 O(n log n)，额外存储 O(1)，范围受调用方数组限制。原生 Sphere/Box/Capsule Query 的此距离度量可处理 Mesh/Terrain，但[三维曲面窄相](Geometry3D.md) 明确不支持这些目标。复杂度说明不代替分配量或耗时测量。
