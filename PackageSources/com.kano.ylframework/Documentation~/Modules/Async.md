# 协程与异步请求（Async）

> [文档首页](../README.md) · [安装](../Installation.md) · [公共计时](Timing.md) · [逐帧更新](Update.md)

`YLFramework.Async` 为普通 C# 调用者提供跨场景协程宿主、原生协程观察，以及可供 `yield return` 或 `AsTask()` 等待的请求基类。三者不合并生产任务、等待和资源释放的所有权，也不自动将工作移到后台线程。

业务 asmdef 引用 `YLFramework.Runtime` 即可，无需 Root、Module 或手动挂载宿主。源码位于 [`Runtime/Foundation/Async/`](../../Runtime/Foundation/Async/)。

## 选择入口

| 需求 | 入口 | 责任 |
| --- | --- | --- |
| 没有 MonoBehaviour 的调用者启动协程 | `CoroutineStarter.Start(enumerator)` | 返回的 `Coroutine` 由调用方保存，需要时交给同一入口的 `Stop` |
| 观察一个已有 Coroutine | `CoroutineObservation.Observe(producer, callback)` | 仅拥有观察 wrapper，`Dispose` 不停止 producer |
| 定义可等待的请求对象 | `CustomAsyncOperation<T>` | 派生类自行启动、推进、提交结果并实现实际取消 |
| 在 async 方法等待同一请求 | `request.AsTask()` | 按需缓存一个 `Task<T>`，返回请求本身，结果与取消仍遵守请求合同 |
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

- `IsDone` 只表示等待结束，`keepWaiting` 为其反值。等待中的 `Progress` 由派生类写入，基类不限制 0..1；结束后固定为 1，迟到写入不再改变它。成功判定以具体请求合同为准。
- 未结束时 `Error` 固定为“正在检测中...”；结束后保留完成入口传入的值，包括 null 与空字符串。
- `Complete(error)` 仅首次提交 Error、结束状态与进度，再同步通知，最后调用 `OnCompleted()`。后续完成或取消均不改写首次结果；派生类仍应先检查终态，再写自己的领域结果字段。
- `CompleteWithoutNotify(error)` 使用同一完成内核，跳过当时的普通订阅，但仍调用 `OnCompleted()` 并结束 Task。两种完成都会断开保存的订阅引用。
- protected `IsCompleted` 只读；旧派生类的直接赋值须改为调用 `Complete(error)`，不再允许重新打开已结束请求。
- `AddCompleteEvent` 重复注册会分别通知；请求已结束时只立即同步调用本次委托，不再保存它，异常直接传给注册者。
- 保存同一委托并配对调用 `RemoveCompleteEvent`；一次只移除一个匹配。`RemoveAllCompleteEvent` 清全部完成订阅，不能用来清理其他 owner 的请求。
- 常规通知按委托顺序 fail-fast，回调中的增删不撤回原通知快照；重入完成不会重发或改写结果。前序通知抛错仍会执行一次 `OnCompleted()`；首次基类取消还会执行一次 `OnAbort()`。单个异常原样传播，多处异常按发生顺序保存在 `AggregateException` 中。
- 基类 `Abort()` 以 `request abort!` 完成等待；重复或终态后的 Abort 无副作用，不会重复执行 hook。但基类不会停止真实生产者，派生类必须负责真实取消与资源收尾。[HTTP](Http.md)、[Resource](Resource.md) 和 [Audio](Audio.md) 各自拥有不同的结果与取消责任。
- 创建、完成、订阅和取得 Task 由请求 owner 串行使用；基类不保证并发线程安全，通知仍在调用线程同步执行，不自动切回 Unity 主线程。

## 使用 async / await

在 `async Task` 方法中可等待已有请求；真实生产者仍需自行交付结果：

```csharp
var result = await request.AsTask(); // result 与 request 是同一对象。
if (string.IsNullOrEmpty(result.Error))
{
    UnityEngine.Debug.Log(result.Value);
}
else
{
    UnityEngine.Debug.LogWarning(result.Error);
}
```

- 第一次 `AsTask()` 才创建桥，多次调用返回同一 Task，可供多个消费者反复 await；不使用它的请求不分配 Task 桥，不启动后台任务、协程或轮询。
- 普通通知、`OnCompleted` 和首次基类取消的 `OnAbort` 收束后，Task 以请求本身结束。通知中的重入 AsTask、回调抛错、静默完成或 `RemoveAllCompleteEvent` 都不会让 Task 丢失完成信号。
- 业务失败、取消或失效不会自动让桥 Task 变成 Faulted/Canceled；检查派生请求的 Status、Exception、Error 等结果。取消仍由请求 owner 的 Abort 等入口负责，不增加 CancellationToken 的含混映射。
- 桥使用 `RunContinuationsAsynchronously`，但已完成 Task 的 await 仍可能同步继续。未完成等待遵守正常 Task/UnitySynchronizationContext 调度，不承诺每次延后一帧；不得用 `.Wait()` 或 `.Result` 阻塞 Unity 主线程。
- 等待不取得或释放业务资源，通用基类也不保证任意生产者停止后一定提交终态。自定义请求若只停协程而不交付结果，Task 仍可能一直 Pending。
- Core 的 `StartUpModuleRequest` 绑定接受时的 manager 与执行会话；退出、会话重置、实际宿主 GameObject 失活/销毁时，会内部失败收束、回滚模块并归还本次资源请求消费者，之后结束协程/Task 等待。正在执行的业务回调会先返回，再进行回滚。详见 [Module 与 MVC](UI.md#module-与-mvc)。这不是新增的用户取消能力，`Abort` 仍明确拒绝。
- 仅将宿主组件的 `enabled` 设为 false 不停止协程；GameObject 失活不会自动恢复旧工作，显式重新激活后需重新发起启动。绕过请求 owner 任意调用原生 StopCoroutine/StopAllCoroutines 仍不等于受支持的请求取消，不能依赖其执行 finally。HTTP 使用独立的原生完成与会话收束，不依赖此宿主。
