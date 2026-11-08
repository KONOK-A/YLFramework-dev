# 快速开始

> [安装指南](Installation.md) · [文档首页](README.md)

本例用一个实例事件完成“注册、发送、退订”，不需要 Root、Module、Resource 或任何选装包。

## 准备

按[安装指南](Installation.md#本地-upm-接入)接入主包并等待 Unity 编译。若脚本位于自己的 asmdef 下，在 Assembly Definition References 中添加 `YLFramework.Runtime`；没有自有 asmdef 时可使用普通 `Assembly-CSharp`。

## 第一个脚本

在使用工程的 `Assets` 自有目录中新建 `EventQuickStart.cs`，内容如下，将组件挂到一个活动 GameObject：

```csharp
using UnityEngine;
using YLFramework.Events;

public sealed class EventQuickStart : MonoBehaviour
{
    private void Start()
    {
        using var channel = new EventChannel<int>();
        var subscription = channel.Register(value => Debug.Log($"收到事件：{value}"));

        channel.Trigger(42);
        subscription.Unregister();
        channel.Trigger(100);
    }
}
```

点击 Play 后，Console 应只出现一次 `收到事件：42`。第二次发送不再通知已退订的回调；方法结束时 `using` 释放通道。

这是同步调用示例。长期存在的订阅应由其业务生命周期持有句柄，在停用、关闭或销毁时退订；全局上下文与场景监听的选择见[事件手册](Modules/Events.md)。不要把局部示例直接改成无法追踪释放责任的静态订阅。

## 下一步

| 想做的事 | 建议阅读 |
| --- | --- |
| 管理多个订阅、使用 EventBus | [Events](Modules/Events.md) |
| 复用对象或安排延时工作 | [Pool](Modules/Pool.md) · [Timing](Modules/Timing.md) |
| 注册逐帧工作或启动协程 | [Update](Modules/Update.md) · [Async](Modules/Async.md) |
| 创建业务与 UI | [UI / Module](Modules/UI.md) |
| 加载资源、实例与场景 | [Resource](Modules/Resource.md) |
| 保存进度或编辑玩法配置 | [选装能力](README.md#选装能力) |

只有所选能力需要时才引入相应系统。[架构](Architecture.md)解释了包、程序集、宿主和生命周期之间的关系。
