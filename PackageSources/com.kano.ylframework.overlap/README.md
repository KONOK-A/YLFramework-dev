# YLFramework Overlap

`com.kano.ylframework.overlap` 提供调用方缓冲的二维/三维重叠与 Cast，以及支持 Enter / Stay / Exit 的持续检测组件。支持二维扇形/圆环解析查询、三维扇区/圆锥保守凸近似、局部 Union、二维 Area 与 Inspector 预览。

## 依赖与接入

- 依赖主包 `com.kano.ylframework`，直接声明 Unity 内置 physics 和 physics2d；不要求 Movement2D、Gameplay、RedDots 或 Save。
- 当前从源码使用本地 UPM 包，须为主包与本包提供明确来源；Git 接入不会由版本声明自动取得兄弟包。
- Runtime namespace 与 asmdef 均为 `YLFramework.Overlap`。直接使用作者 API 时，Editor asmdef 另引用 `YLFramework.Overlap.Editor`，namespace 为 `YLFramework.Editor.Overlap`。
- 安装步骤见[主包安装指南](https://github.com/KONOK-A/YLFramework-dev/blob/main/PackageSources/com.kano.ylframework/Documentation~/Installation.md)，框架入门见[快速开始](https://github.com/KONOK-A/YLFramework-dev/blob/main/PackageSources/com.kano.ylframework/Documentation~/QuickStart.md)。这些跨包链接指向源码仓库当前文档。

## 开始使用

一次查询用 `OverlapQuery2D/3D`，明确世界形状、Filter、物理场景和调用方数组。连续检测用 `OverlapDetector2D/3D`，配置局部 Composite，再手动采样或选择自动模式。只对 `Complete` 结果作完整集合判断，不能把缓冲截断或无效当作未命中。

## 手册

[使用手册](Documentation~/README.md) 提供按任务组织的完整说明：

- [Query 与 Cast](Documentation~/Queries.md)：世界形状、Filter、容量、排序与失效原因。
- [二维扇形与圆环](Documentation~/Geometry2D.md)：解析几何、容差、目标类型和提取预算。
- [三维扇区与圆锥](Documentation~/Geometry3D.md)：近似精度、私有物理世界、支持目标与释放。
- [持续检测与作者工具](Documentation~/Detectors.md)：采样、完整差分、Contact、Area、Inspector 和预览。

## 许可证

[MIT](LICENSE.md)。
