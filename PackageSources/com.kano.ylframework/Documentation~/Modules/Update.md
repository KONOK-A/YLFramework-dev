# 逐帧更新（Update）

> [文档首页](../README.md) · [安装](../Installation.md) · [事件](Events.md) · [协程与异步请求](Async.md)

`YLFramework.Update.UpdateManager` 将 Unity 的 `Update`、`FixedUpdate`、`LateUpdate` 转发为三个独立订阅通道，供普通 C# 对象接收更新。不需要 Root、Module 或在场景中手动放置组件。

源码为 [`Runtime/Foundation/Update/UpdateManager.cs`](../../Runtime/Foundation/Update/UpdateManager.cs)，属于 `YLFramework.Runtime`。带 asmdef 的业务程序集显式引用 Runtime，所有登记和清理均在 Unity 主线程进行。

## 最小用法

每次启用建立本轮订阅，停用时先结束本轮，再精确退订。额外的轮次检查用于拒绝已经进入本次分发快照的迟到回调。

```csharp
using UnityEngine;
using YLFramework.Events;
using YLFramework.Update;

public sealed class UpdateExample : MonoBehaviour
{
    private IUnregister _subscription;
    private int _generation;
    private float _elapsed;

    private void OnEnable()
    {
        int generation = ++_generation;
        _subscription = UpdateManager.AddUpdateEvent(() =>
        {
            if (this == null || !isActiveAndEnabled || generation != _generation)
            {
                return;
            }

            _elapsed += Time.deltaTime;
        });
    }

    private void OnDisable()
    {
        ++_generation;
        _subscription?.Unregister();
        _subscription = null;
    }
}
```

示例只累计启用期间的渲染帧增量。UpdateManager 的回调本身没有 deltaTime 参数，按任务需要读取 `Time.deltaTime`、`Time.unscaledDeltaTime` 或固定步长；需要到期调度时使用 [Timing](Timing.md)，不自行统计回调次数模拟所有时钟。

## 注册与退订

| 相位 | 注册 | 按委托退订 |
| --- | --- | --- |
| Update | `AddUpdateEvent(Action)` | `RemoveUpdateEvent(Action)` |
| FixedUpdate | `AddFixedUpdateEvent(Action)` | `RemoveFixedUpdateEvent(Action)` |
| LateUpdate | `AddLateUpdateEvent(Action)` | `RemoveLateUpdateEvent(Action)` |

- 每次注册返回独立的 `IUnregister`，重复委托会重复调用；返回句柄只取消对应注册，推荐保存句柄而不是重新构造 lambda。
- `Remove*Event` 删除最后一个相等的普通委托子序列，不会移除全部重复注册。它只访问既有宿主，不查找或创建替代对象。
- null 回调、非有效 Play 会话或应用退出期间返回空句柄，不创建宿主。空句柄可安全 `Unregister`，但不能据此判断注册已生效。
- 三个相位互相独立，只保证同相位内的注册顺序。不承诺与其他 MonoBehaviour、Action 执行器、计时或协程之间的执行全序。

## 分发、异常与重入

- 通道使用 [EventChannel](Events.md) 的快照机制。回调中新注册的监听不加入当前快照，普通退订不撤回已进入快照的回调，嵌套事件读取新状态。
- 因此 owner 停用、释放或重新启用时，除了精确退订，还应按任务风险检查自身存活与订阅轮次。仅检查当前启用状态不能识别“停用后同帧重新启用”的旧回调。
- 每个相位使用 `TriggerAll`，某个监听抛错后仍尝试其他监听，结束后在 Unity 边界统一 `Debug.LogException`。它不回滚监听已经产生的业务副作用。
- 调用方结束只取消自己的 token，不清空公共源。UpdateManager 不提供公共 `Tick`、`Clear`、`Shutdown` 或跨线程投递接口。

## 宿主与会话

- 首次有效注册惰性创建独立 DDOL GameObject `YLFramework.Foundation.UpdateManager`，跨场景保留，不依附注册者对象。
- 最后一个监听退订后保留空闲宿主到会话结束，不按监听数反复创建和销毁。
- 业务不应停用、销毁或自行挂载共享宿主。停用宿主会停止对应 Unity 消息；外部销毁则结束全部旧通道和旧 token，后续新注册不恢复旧工作。
- 新 Play 会话会关闭旧通道、停用并销毁旧宿主，禁用 Domain Reload 时也会重置。应用退出时关闭执行门禁，不在退出回调中重新创建更新工作。
- 订阅者生命周期不由公共宿主管理。普通 C# owner 可用 `SubscriptionScope` 收集本方 token，并在自己的退出入口 `Dispose()`；场景或对象边界可使用 Events 的生命周期注销扩展。
