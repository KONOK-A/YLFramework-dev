# Overlap 使用手册

> [包入口](../README.md)

Overlap 提供同步、调用方缓冲的二维/三维 Query 与 Cast，以及按完整观测提交 Enter / Stay / Exit 的持续检测组件。它不负责移动、伤害、遮挡、可见性或业务 FOV，也不要求 Root、Module 或全局更新服务。

## 前置与程序集

安装主包与 `com.kano.ylframework.overlap`；Unity 内置 physics、physics2d 由本包直接声明。最小自有包组合是主包与 Overlap，不要求 Movement2D、Gameplay、RedDots 或 Save。

| 用途 | namespace | asmdef 引用 |
| --- | --- | --- |
| Query、几何、Detector | `YLFramework.Overlap` | `YLFramework.Overlap` |
| 自定义 Inspector / Gizmos | `YLFramework.Editor.Overlap` | `YLFramework.Overlap.Editor` |

Runtime 依赖主包 `YLFramework.Runtime`，Editor 依赖本包 Runtime 与主包 `YLFramework.Editor`。本地 UPM 与 Git 自有依赖来源见[安装指南](https://github.com/KONOK-A/YLFramework-dev/blob/main/PackageSources/com.kano.ylframework/Documentation~/Installation.md)，该链接指向源码仓库当前文档。

## 选择入口

| 任务 | 入口与下一步 |
| --- | --- |
| 一次性重叠或扫掠 | [Query 与 Cast](Queries.md)：世界形状、Filter、物理场景、调用方数组、完整性 |
| 二维扇形、环形扇区或圆环 | [二维曲面几何](Geometry2D.md)：解析窄相、Workspace、支持目标与数值预算 |
| 三维挤出扇区、圆环或有限圆锥 | [三维曲面几何](Geometry3D.md)：保守凸分解、私有物理场景、目标支持与释放 |
| 连续观测与 Inspector 配置 | [持续检测与作者工具](Detectors.md)：局部 Union、Area、采样、事件、预览 |

## 最小操作路径

一次查询先分配调用方数组，再构造世界空间 `OverlapShape2D/3D` 和明确的 Filter，最后调用 `OverlapQuery2D/3D.Query`。只读数组 `[0, result.Count)`，同时检查 `Status`，不要把 `Count == 0` 单独解释为完整未命中。

持续检测在自己的 GameObject 上添加 `OverlapDetector2D/3D`，Inspector 配置局部 Union、Filter 和容量。默认启用时开始检测，但采样模式是 Manual，须显式 `Sample()` 或选择自动采样模式。停止使用 `Stop()`，再次运行使用 `StartDetection()`。

## 核心合同

- Query 和 Cast 是 Unity 主线程同步操作，借用物理场景和命中对象，不转移其所有权。
- 原生缓冲恰好填满也属于 `MayBeTruncated`。后续过滤后即使 Count 为零，仍不能证明完整。
- Detector 只有在所有子项和去重后的 Union 都完整时才提交差分；截断或无效保留 `LastComplete`，不发出虚假的离开。
- 二维全局禁用 Trigger 查询时，要求包含 Trigger 的查询返回 `TriggerQueriesDisabled`，不翻转全局开关；三维显式选择 Ignore 或 Collide，拒绝 UseGlobal。
- 曲面形状需要 owner 自有 Workspace。二维 Sector 是解析几何，三维 Sector/Cone 是有限预算的保守生成凸近似，两者不具有相同精度合同，也都不支持 Cast。
- 三维曲面 Workspace 只能在 Play Mode 构造，拥有一个私有 PhysicsScene；Dispose 发起异步场景释放，不表示原生内存瞬时归零。

## 数据与验证边界

组件、Filter 的忽略对象、场景与 Prefab 引用属于使用工程，Workspace 仅释放自己创建的存储和辅助对象。卸载本包不删除用户组件、脚本或项目数据，须先处理其 API、asmdef 和持久事件引用。

源码中的排序复杂度、固定预算或复用设计不等于已测得零分配或某个机器上的耗时。作者交互、真实物理、序列化回调和实际 Player 性能须分别验证，不用模拟物理或不可用的分配计数器代替原生证据。
