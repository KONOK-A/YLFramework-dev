# YLFramework 二维移动

`com.kano.ylframework.movement2d` 提供 Rigidbody2D 角色移动、跳跃与接触检测，以及折线路径、移动平台和乘客检测。角色与平台可以分别使用，不自动安装输入系统、Overlap 或状态机。

## 依赖与接入

- 依赖主包 `com.kano.ylframework` 与 Unity 内置 `com.unity.modules.physics2d`。
- 当前从源码使用本地 UPM 包，须同时为主包与本包提供明确来源；Git 接入也不能仅靠包版本声明取得兄弟包。
- 角色消费者引用 `YLFramework.Character2D`，路径与平台消费者引用 `YLFramework.Platform2D`，两套 Runtime 不互相引用。
- 自定义作者工具按需引用 `YLFramework.Movement2D.Editor`。
- 安装步骤见[主包安装指南](https://github.com/KONOK-A/YLFramework-dev/blob/main/PackageSources/com.kano.ylframework/Documentation~/Installation.md)，框架入门见[快速开始](https://github.com/KONOK-A/YLFramework-dev/blob/main/PackageSources/com.kano.ylframework/Documentation~/QuickStart.md)。这些跨包链接指向源码仓库当前文档。

## 开始使用

角色先准备 Rigidbody2D 和附属的非 Trigger Box、Circle 或 CapsuleCollider2D，再配置 `CharacterController2D`，在调用方 `FixedUpdate` 中提交移动。平台使用 `MoveablePlatform2D` 和 `PlatformCatcher2D`，先配置世界路径，再运行。

## 手册

[使用手册](Documentation~/README.md) 说明移动时序、跳跃、Collider 刷新、路径快照、暂停与停止、平台载人和编辑器操作。平台直接向乘客刚体施加世界位移，不额外执行角色式障碍投射。

## 许可证

[MIT](LICENSE.md)。
