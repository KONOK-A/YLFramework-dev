# YLFramework

面向 Unity 的开源 C# 游戏开发框架，提供基础工具、业务模块、UI、资源管理及常用游戏系统。

可以只使用事件、对象池或计时器，也可以组合 Module、UI 和资源系统组织项目。配套的编辑器工具用于管理配置、构建资源和查阅 API。

[使用文档](Assets/YLFramework/Documentation/README.md) · [快速开始](#快速开始) · [问题反馈](https://github.com/KONOK-A/YLFramework-dev/issues)

## 主要功能

| 能力 | 内容 |
| --- | --- |
| 基础工具 | 事件、对象池、计时器、依赖注入、数据绑定、常用数据结构 |
| 业务与 UI | Module / MVC、View / Panel、UI 分层及输入协调 |
| 资源管理 | 资源与场景加载、AssetBundle 构建、版本发布和远程更新 |
| 游戏系统 | 音频、存档、HTTP、本地化、技能、Buff、任务、装备和红点 |
| 动作与状态机 | ActionKit 动作组合、轻量 FSM、配置式状态机 |
| 编辑器工具 | 配置编辑、Inspector 扩展、资源构建和内置帮助 |

DOTween、Excel 和 HybridCLR 以[可选集成](Assets/YLFramework/Documentation/README.md#可选集成)提供，按需要接入。

## 快速开始

### 打开源码工程

准备 Unity **2022.3.62f3** 和 [Git LFS](https://git-lfs.com/)，然后克隆仓库：

```bash
git lfs install
git clone https://github.com/KONOK-A/YLFramework-dev.git YLFrameworkDev
```

使用 Unity Hub 打开 `YLFrameworkDev`，等待依赖解析和资源导入完成。开发场景位于 `Assets/Scenes/YLFrameworkDev.unity`，框架源码位于 `Assets/YLFramework/`。

Unity 版本兼容范围和各模块的平台要求见[安装说明](Assets/YLFramework/Documentation/README.md#安装与分发)。

### 接入已有项目

1. 在源码工程中选择 **YLFramework → Toolkit → 导出框架包**，生成 `.unitypackage`。
2. 在目标项目中安装 UGUI 和 Json.NET（`com.unity.nuget.newtonsoft-json`，基线版本 `3.2.1`），导入框架包。
3. 如果使用 asmdef，运行时代码引用 `YLFramework.Runtime`，编辑器扩展引用 `YLFramework.Editor`。

接入示例见 [Samples](Assets/YLFramework/Samples)。UI、资源和其他系统的具体设置见[使用文档](Assets/YLFramework/Documentation/README.md)。

## 一个动作示例

将下面的组件挂到 GameObject 上，一秒后输出消息：

```csharp
using UnityEngine;
using YLFramework.Actions;

public sealed class HelloActions : MonoBehaviour
{
    private void Start()
    {
        ActionKit.Sequence()
            .Delay(1f)
            .Callback(() => Debug.Log("Hello, YLFramework!"))
            .StartNamed(this, "hello");
    }
}
```

更多用法见 [ActionKit 文档](Assets/YLFramework/Documentation/Modules/Action.md)。

## 文档

- [完整指南与模块索引](Assets/YLFramework/Documentation/README.md)
- [架构设计](Assets/YLFramework/Documentation/Architecture.md)
- [UI 与业务模块](Assets/YLFramework/Documentation/Modules/UI.md)
- [资源管理](Assets/YLFramework/Documentation/Modules/Resource.md)
- [音频系统](Assets/YLFramework/Documentation/Modules/Audio.md)

在 Unity 中也可以通过 **YLFramework → 帮助** 浏览模块指南和 API。

## 许可证

YLFramework 以 [MIT License](LICENSE) 开源。第三方源码署名与其他许可见[版权声明](Assets/YLFramework/LICENSE.md)。
