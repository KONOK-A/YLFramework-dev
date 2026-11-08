# 协程与异步请求（Async）

> [文档首页](../README.md) · [安装](../Installation.md) · [公共计时](Timing.md) · [逐帧更新](Update.md)

`YLFramework.Async` 为普通 C# 调用者提供跨场景协程宿主、原生协程观察，以及可供 `yield return` 等待的请求基类。三者不合并生产任务、等待和资源释放的所有权，也不自动将工作移到后台线程。

业务 asmdef 引用 `YLFramework.Runtime` 即可，无需 Root、Module 或手动挂载宿主。源码位于 [`Runtime/Foundation/Async/`](../../Runtime/Foundation/Async/)。

## 选择入口

| 需求 | 入口 | 责任 |
| --- | --- | --- |
| 没有 MonoBehaviour 的调用者启动协程 | `CoroutineStarter.Start(enumerator)` | 返回的 `Coroutine` 由调用方保存，需要时交给同一入口的 `Stop` |
| 观察一个已有 Coroutine | `CoroutineObservation.Observe(producer, callback)` | 仅拥有观察 wrapper，`Dispose` 不停止 producer |
| 定义可等待的请求对象 | `CustomAsyncOperation<T>` | 派生类自行启动、推进、提交结果并实现实际取消 |
| 等待秒数、帧数或周期回调 | [Timing](Timing.md) | 使用精确 Timer 句柄，不必创建自定义协程 |
| 组合条件、插值和多个步骤 | [Action](Action.md) | 由动作执行器管理组合与回收 |

## 启动与观察

以下组件在启用时启动跨场景协程并观察结束，停用时分别结束观察和生产协程。它主动选择“跟随组件停用取消”，不是 Starter 自动绑定了调用方。

```csharp
using System.Collections;
using UnityEngine;
using YLFramework.Async;

public sealed class CoroutineExample : MonoBehaviour
{
    private Coroutine _producer;
    private CoroutineObservation _observation;

    private void OnEnable()
    {
        _producer = CoroutineStarter.Start(Produce());
        if (_producer == null)
        {
            Debug.LogWarning("没有取得可用的生产协程句柄");
            return;
        }

        _observation = CoroutineObservation.Observe(
            _producer, () => Debug.Log("原生协程等待结束"));
        if (!_observation.IsStarted)
        {
            Debug.LogWarning("观察未启动，不能据此判断生产任务是否结束");
        }
    }

    private IEnumerator Produce()
    {
        yield return new WaitForSecondsRealtime(0.5f);
        Debug.Log("生产协程到达末尾");
    }

    private void OnDisable()
    {
        _observation?.Dispose();
        _observation = null;
        CoroutineStarter.Stop(_producer);
        _producer = null;
    }
}
```

正常运行时先输出生产协程的日志，再输出观察回调。日志表示本例控制流到达相应位置，不是通用的业务成功协议。

## 协程宿主与停止

- `Start` 仅在有效 Play 会话的 Unity 主线程使用，首次启动惰性创建独立 DDOL 宿主 `YLFramework.Foundation.CoroutineStarter`，不依附调用方场景对象。
- 启动保留 Unity 的同步首步行为，迭代器可在 `Start` 返回前执行到首次 yield，甚至已经结束或使宿主失效。返回 null 只表示没有可用句柄，不保证用户代码从未执行。
- 非 Play、应用退出或宿主不可运行时返回 null；启动时抛出的异常不会转成统一请求状态。
- `Stop(coroutine)` 幂等，只路由到静态 `Start` 记录的原宿主。null、无记录、旧会话或已失效宿主均为空操作，不创建替代宿主，也不停止其他 MonoBehaviour 的协程。
- 场景切换不会停止 Starter 的工作。外部销毁或使其 GameObject 失活会结束相应协程，不恢复旧工作；单独禁用 MonoBehaviour 不等于停止 Unity 协程。
- 新 Play 会话会结束旧宿主并切断旧句柄路由，禁用 Domain Reload 时也适用。退出过程中拒绝新工作，不应由业务直接停用或销毁共享宿主。
- 原生停止不保证迭代器的 `finally` 被执行，也不意味着外部请求、Task、租约或业务资源已取消或释放。资源应有独立且可确定执行的清理入口。

## 观察边界

[`CoroutineObservation`](../../Runtime/Foundation/Async/CoroutineObservation.cs) 只包装原生 `yield return producer`。

- producer 与 callback 都不能为 null，否则抛 `ArgumentNullException`。
- `IsStarted` 记录是否取得观察句柄，不是实时存活状态；观察启动后宿主仍可能失效。
- Unity 对同一 Coroutine 的原生等待有限制，已有 waiter 时观察可能无法启动，必须检查 `IsStarted`。观察器不建立多播或轮询状态系统。
- callback 只表示原生等待结束，不证明业务成功、未取消或资源已释放。producer 被停止或其 owner 销毁时，不保证等待恢复或 callback 执行。
- 正常结束先标记观察已结束并释放自身引用，再同步调用 callback；回调异常不在观察器内隔离。
- `Dispose()` 幂等，只停止自己的 wrapper 并释放 callback，不停止 producer。需要取消生产任务时，由其原 owner 另行处理。

## 自定义可等待请求

[`CustomAsyncOperation<T>`](../../Runtime/Foundation/Async/CustomAsyncOperation.cs) 继承 Unity `CustomYieldInstruction`，派生类通常将自身作为类型参数。下面定义一个由外部生产者显式交付结果的请求，不会自行启动协程或线程：

```csharp
using YLFramework.Async;

public sealed class NumberRequest : CustomAsyncOperation<NumberRequest>
{
    public int Value { get; private set; }

    public void Succeed(int value)
    {
        if (IsDone) return;
        Value = value;
        Complete();
    }

    public void Fail(string error)
    {
        if (IsDone) return;
        Complete(error);
    }
}
```

协程方法中可等待已经交付的请求；真实生产者则在工作结束时调用 `Succeed` 或 `Fail`：

```csharp
var request = new NumberRequest();
request.AddCompleteEvent(result => UnityEngine.Debug.Log(result.Value));
request.Succeed(42); // 本次调用中同步输出 42。
yield return request; // 已结束，不再等待；不会自动抛出 Error。
```

## 完成、订阅与取消

- `IsDone` 只表示等待结束，`keepWaiting` 为其反值。`Progress` 由派生类写入，基类不限制 0..1；通过完成入口结束时设为 1。成功判定以具体请求合同为准。
- 未结束时 `Error` 固定为“正在检测中...”；结束后保留完成入口传入的值，包括 null 与空字符串。
- `Complete(error)` 先存 Error，再提交结束状态并同步通知，最后调用 `OnCompleted()`。重复调用仍会更新 Error 和进度，但不重发通知。
- `CompleteWithoutNotify(error)` 跳过现有完成订阅，但仍调用 `OnCompleted()`；不清订阅，迟到的 `AddCompleteEvent` 仍立即执行。重复调用只更新结果和进度。
- 直接将 protected `IsCompleted` 设为 true 不会去重，通常应使用完成方法。`OnCompleted` 不是必然执行的 finally，前序订阅异常会阻断它。
- `AddCompleteEvent` 重复注册会分别保存；请求已结束时立即同步调用本次新增委托。先保存订阅再调用，迟到回调抛错也不会自动移除它。
- 保存同一委托并配对调用 `RemoveCompleteEvent`；一次只移除一个匹配。`RemoveAllCompleteEvent` 清全部完成订阅，不能用来清理其他 owner 的请求。
- 常规通知按委托顺序 fail-fast，回调中的增删不撤回已经捕获的委托调用链；重入 `Complete` 不重发，但仍可能改写 Error。所有通知在调用线程执行，不自动切回 Unity 主线程。
- 基类 `Abort()` 以 `request abort!` 完成等待，再调用 `OnAbort()`；即使已结束也会改写 Error 并再次调用 `OnAbort`。完成链抛错时不进入 `OnAbort`，基类也不会停止真实生产者。
- 需要稳定终态、取消令牌或可靠资源收尾的派生请求必须自行定义这些契约，不能仅依赖基类 Abort。[HTTP](Http.md)、[Resource](Resource.md) 和 [Audio](Audio.md) 各自拥有不同的结果与取消责任。
