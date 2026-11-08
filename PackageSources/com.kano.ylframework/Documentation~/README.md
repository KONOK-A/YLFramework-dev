# YLFramework 使用手册

YLFramework 为 Unity C# 项目提供按需组合的基础机制、业务与表现系统、资源服务及编辑器工具。框架仍在开发中，文档以当前源码为准。

## 从这里开始

| 目标 | 阅读路径 |
| --- | --- |
| 将框架接入自己的工程 | [安装指南](Installation.md) → [快速开始](QuickStart.md) |
| 使用事件、池、计时等轻量工具 | 下方“基础机制”，无需先启动整个框架 |
| 组织业务与窗口 | [UI / Module](Modules/UI.md) |
| 加载资源、场景及命名音频 | [Resource](Modules/Resource.md) → [Audio 资源](Modules/Audio/Resources.md) |
| 选择独立能力包 | 下方“选装能力”，完整手册随所属包提供 |
| 理解或扩展框架 | [架构](Architecture.md) · [代码规范](CodingStyle.md) · [Editor 扩展](Modules/Editor.md) |

## 主包能力

### 基础机制

| 能力 | 文档 |
| --- | --- |
| 实例事件、EventBus、订阅与 Unity 监听 | [事件](Modules/Events.md) |
| 普通对象、集合与 GameObject 池 | [对象池](Modules/Pool.md) |
| 秒、帧、日期投递与作用域 | [计时](Modules/Timing.md) |
| 三相位消息注册与注销 | [Update 桥接](Modules/Update.md) |
| 协程启动、观察与请求等待 | [Async](Modules/Async.md) |
| 构造与成员注入、容器 | [依赖注入](Modules/IOC.md) |
| C#、MonoBehaviour、Prefab 与 SO 单例 | [单例](Modules/Singleton.md) |
| 属性和集合变更绑定 | [数据绑定](Modules/Binding.md) |
| 序列化字典与 Inspector | [序列化](Modules/Serialization.md) |
| 类型元数据、Inspector 与编译器使用诊断 | [Attributes](Modules/Attributes.md) |
| 类型查找与缓存 | [AssemblyTool](Modules/AssemblyTool.md) |

### 业务与系统

| 能力 | 文档 |
| --- | --- |
| Module / MVC、View / Panel、UI 根与输入 | [UI](Modules/UI.md) |
| 资产、实例、场景与图片加载 | [Resource 接入](Modules/Resource.md) |
| 配置、图集、工作台与代码生成 | [Resource 作者工具](Modules/Resource/Authoring.md) |
| 部署、更新、加密与平台限制 | [Resource 交付](Modules/Resource/Delivery.md) |
| 播放、Options、句柄与关闭 | [Audio](Modules/Audio.md) |
| 命名 Provider、素材库与资源租约 | [Audio 资源](Modules/Audio/Resources.md) |
| 淡变、Mixer、组件与播放策略 | [Audio 进阶](Modules/Audio/Advanced.md) |
| 插值、组合动作、Observable 与 DOTween 适配 | [Action](Modules/Action.md) |
| HTTP 请求、取消与响应 | [HTTP](Modules/Http.md) |
| 日志输出与会话 | [Logging](Modules/Logging.md) |

### 通用工具与 Editor

| 能力 | 文档 |
| --- | --- |
| 代码优先轻量状态机 | [FSM](Modules/FSM.md) |
| 网格、表与索引 | [Grid](Modules/Grid.md) · [Table](Modules/Table.md) |
| JSON 树与 Unity 值转换 | [Json](Modules/Json.md) |
| 压缩与安全解压 | [ZipTool](Modules/ZipTool.md) |
| Unity / System / Math 扩展 | [Extensions](Modules/Extensions.md) |
| 取色窗口、Tips 与 UI 查询 | [UI 控件](Modules/UI.md#资源tips-与控件) |
| 资产编辑、TreeView、设置与生成 | [Editor](Modules/Editor.md) |
| 帮助提供方、窗口与 XML 索引 | [Help](Modules/Help.md) |

## 选装能力

下列链接指向源码仓库当前文档；安装后的对应包也包含自己的 `README.md` 和 `Documentation~/README.md`。完整说明不依赖 UPM 缓存目录之间的相对位置。

| 能力包 | 用途与完整手册 |
| --- | --- |
| `com.kano.ylframework.data-assets` | [ScriptableObject 配置根、登记与作者工具](https://github.com/KONOK-A/YLFramework-dev/blob/main/PackageSources/com.kano.ylframework.data-assets/Documentation~/README.md) |
| `com.kano.ylframework.localization` | [语言根、回退与文本/图片显示](https://github.com/KONOK-A/YLFramework-dev/blob/main/PackageSources/com.kano.ylframework.localization/Documentation~/README.md) |
| `com.kano.ylframework.save` | [独立存档、槽位与恢复](https://github.com/KONOK-A/YLFramework-dev/blob/main/PackageSources/com.kano.ylframework.save/Documentation~/README.md) |
| `com.kano.ylframework.state-machine` | [配置式状态机、图与状态脚本](https://github.com/KONOK-A/YLFramework-dev/blob/main/PackageSources/com.kano.ylframework.state-machine/Documentation~/README.md) |
| `com.kano.ylframework.movement2d` | [二维角色与移动平台](https://github.com/KONOK-A/YLFramework-dev/blob/main/PackageSources/com.kano.ylframework.movement2d/Documentation~/README.md) |
| `com.kano.ylframework.overlap` | [2D/3D 检测、Cast、Detector 与预览](https://github.com/KONOK-A/YLFramework-dev/blob/main/PackageSources/com.kano.ylframework.overlap/Documentation~/README.md) |
| `com.kano.ylframework.gameplay` | [Skill、Buff、Mission、Equipment](https://github.com/KONOK-A/YLFramework-dev/blob/main/PackageSources/com.kano.ylframework.gameplay/Documentation~/README.md) |
| `com.kano.ylframework.reddot` | [红点聚合、已读状态与诊断](https://github.com/KONOK-A/YLFramework-dev/blob/main/PackageSources/com.kano.ylframework.reddot/Documentation~/README.md) |
| `com.kano.ylframework.excel` | [Excel 工作簿作者流程与 Gameplay 适配](https://github.com/KONOK-A/YLFramework-dev/blob/main/PackageSources/com.kano.ylframework.excel/Documentation~/README.md) |
| `com.kano.ylframework.hybridclr` | [HybridCLR 构建、补丁与运行时](https://github.com/KONOK-A/YLFramework-dev/blob/main/PackageSources/com.kano.ylframework.hybridclr/Documentation~/README.md) |

包选择、依赖和条件适配见[安装指南](Installation.md#选择能力包)。轻量 FSM 属于主包，配置式 StateMachine 属于选装包，两者使用不同执行内核。

## 阅读与使用约定

- 每个模块说明自己的初始化、线程、取消和释放责任。`YLFrameworkRoot` 是常驻宿主与默认 UI 入口，不会自动启动所有系统。
- 同包文档使用相对链接；标示为源码仓库的跨包链接展示当前主线内容，不代表未来某个安装版本的固定快照。
- `Documentation~` 不进入 AssetDatabase，请按实际包磁盘路径打开；Editor 的 `YLFramework/帮助` 提供模块指南与可取得的 API/XML 索引，详见 [Help](Modules/Help.md)。
- 来源与许可见 [LICENSE](../LICENSE.md)。源码开发流程和测试工具见源码仓库的[贡献指南](https://github.com/KONOK-A/YLFramework-dev/blob/main/CONTRIBUTING.md)，它们不随产品包安装。
