# 网格容器（Grid）

> [文档首页](../README.md) · [架构总览](../Architecture.md)

## 作用

提供轻量二维网格容器：固定矩形的 `NormalGrid<T>` 与按需存储的稀疏 `DynamicGrid<T>`，适用于棋盘、地图格子等按 (x, y) 存取的数据。

## 位置

- 运行时：[`Runtime/Utilities/Grid/`](../../Runtime/Utilities/Grid/)
- namespace：`YLFramework.Grids`；程序集：`YLFramework.Runtime`。直接 `new` 容器，无需 Module、Manager、组件挂载或公共 Root。
- 保留通用二维数据结构职责，不替代普通字典 / 列表，也不因存在 Foundation 容器而强制改为序列化容器、资源索引或网格服务。

## 用法

```csharp
using YLFramework.Grids;

var board = new NormalGrid<int>(8, 8);
board[2, 3] = 7;
bool inBounds = board.TryGet(2, 3, out var value);
board.Resize(10, 8); // 保留原坐标上的交集数据。

var sparse = new DynamicGrid<int>();
sparse[-2, 3] = 0;
bool exists = sparse.Contains(-2, 3); // true，写入 default 也占一个格子。
```

## 坐标与越界约定

- `NormalGrid<T>`：合法坐标 `0 <= x < Width`、`0 <= y < Height`，不支持负坐标。索引器越界读取返回 `default(T)` 并输出警告日志；越界写入被忽略并输出警告。`TryGet` / `TrySet` / `IsInBounds` 越界时静默返回 `false`，不输出日志。
- `DynamicGrid<T>`：稀疏坐标容器，无尺寸与边界概念，支持任意 `int` 坐标（含负数）；未存储坐标读取返回 `default(T)`。

## NormalGrid<T>

- 构造：`new NormalGrid<T>(width, height)`；负数尺寸或总元素数超出 `int` 上限抛 `ArgumentOutOfRangeException`，允许 0 尺寸。
- 索引访问：`grid[x, y]`。
- `Resize(width, height)`：保留新旧范围交集中的已有数据，新增区域填充 `default(T)`。
- `Resize(width, height, addValueGetter)`：新增区域由 `addValueGetter(x, y)` 按 x 外层、y 内层顺序生成；缩小尺寸时超范围数据被丢弃。
- `Resize` 操作失败（尺寸非法 / getter 抛异常）时，原网格尺寸与数据保持不变。
- `Clear(cleanupItem = null)`：保留 `Width` / `Height`，对每个格子调用一次回调（回调可为 null）后写回 `default(T)`；回调抛异常会中断清空，但尺寸与网格后续可用性不受影响。
- `Fill(value)` / `Fill(valueGetter)`：填充整个网格，遍历顺序为 x 外层、y 内层；`valueGetter` 为 null 抛 `ArgumentNullException`。
- 查询：`Width`、`Height`、`Count`、`IsEmpty`、`IsInBounds(x, y)`、`TryGet(x, y, out value)`、`TrySet(x, y, value)`。
- `ForEach(...)`：遍历顺序固定为 x 外层、y 内层递增（y 变化最快）；捕获进入时的数组和尺寸，回调内 `Resize` 不改变本次遍历范围。这不是值的深拷贝快照。

## DynamicGrid<T>

- 只存储实际写入过的格子，不预分配、无边界概念；显式写入 `default(T)` 的格子仍视为已存在（计入 `Count`，`Contains` / `TryGet` 返回 `true`）。
- `Count`、`IsEmpty`、`Contains(x, y)`、`TryGet(x, y, out value)`。
- `Clear(cleanupItem = null)`：移除全部条目，对每个已存储项调用一次回调；回调抛异常时不会移除任何条目。
- `ForEach(...)`：实时枚举内部字典，顺序不承诺；回调中不要增删坐标，修改限制与异常遵循底层 `Dictionary`。

## 依赖

- `NormalGrid<T>`：System / UnityEngine（仅越界警告用到 `Debug.LogWarning`）；`DynamicGrid<T>`：仅 System。无框架内模块依赖。

## 已知限制

- `NormalGrid<T>` 不支持负坐标与稀疏存储；`DynamicGrid<T>` 无 `Width` / `Height` / `Resize`。
- 两个类型只提供 `ForEach`，未实现 `IEnumerable`；不支持序列化。
- 容器不自动释放格子中的对象。缩小尺寸会丢弃范围外的引用，需要释放资源时由调用方在变更前处理，或显式使用 `Clear(cleanupItem)`。
- 非线程安全；`Fill` / `Clear` 的用户回调不提供事务回滚，也不应在其中修改网格结构。
