# Save 本地存档

`com.kano.ylframework.save` 提供存档槽、对象 / 主模型 / 全局设置、单文件原子 JSON 写入和显式备份恢复。`SaveStore` 是业务入口，`SaveManager` 管理 Unity 默认路径与默认 Store 的会话生命周期。

## 依赖与接入

本包可以独立安装，不要求主包或其他选装包；由 UPM 解析 `com.unity.nuget.newtonsoft-json`。业务 asmdef 引用 `YLFramework.Save`。完整前置与独立用法都在[本包使用手册](Documentation~/README.md)，无需安装主包才能阅读或运行示例。

统一源码接入步骤见[公共安装指南](https://github.com/KONOK-A/YLFramework-dev/blob/main/PackageSources/com.kano.ylframework/Documentation~/Installation.md)，框架基础示例见[快速开始](https://github.com/KONOK-A/YLFramework-dev/blob/main/PackageSources/com.kano.ylframework/Documentation~/QuickStart.md)。这些链接指向源码仓库当前文档，不是 Save 的额外安装依赖。

## 开始使用

在 Unity 主线程借用 `SaveManager.Store`，先 `CreateSaveItem()`，再用显式文件名与槽 ID 调用 `SaveObject` / `ReadObject<T>`。默认 Store 不能由借用者 `Dispose()`；自己创建的 `new SaveStore(rootPath)` 则由创建者释放。

存档只支持索引格式 v2。采用备份值不等于磁盘已修复；写入前按手册显式恢复，不用默认数据覆盖损坏档。并发、取消与恢复边界见[详细说明](Documentation~/README.md)。

## 许可证

本包采用 [MIT 许可证](LICENSE.md)。
