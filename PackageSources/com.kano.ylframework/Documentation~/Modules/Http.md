# HTTP 请求

> [文档首页](../README.md) · [架构](../Architecture.md) · [资源](Resource.md)

`YLFramework.Http` 提供 Unity 主线程上的轻量 HTTP 请求。它独占每笔传输，由 UnityWebRequest 的原生 completed 单独驱动完成，不创建或依赖协程宿主；不管理认证、重试、下载缓存或全局任务队列，资源发布与更新使用 Resource 自己的流程。

## 入口与用法

源码为 [`HttpManager`](../../Runtime/Systems/Http/HttpManager.cs) 和 [`SendWebRequest`](../../Runtime/Systems/Http/SendWebRequest.cs)，均属于 `YLFramework.Runtime`，无需挂载组件。

| 入口 | 请求内容 |
| --- | --- |
| `Get(url, param, headers)` | 追加 query，保留原 query / fragment；键值分别编码，数值使用 InvariantCulture |
| `PostForm(url, param, headers)` | UTF-8 URL 编码表单，null 字段值为空字符串 |
| `PostBody(url, dictionary, headers)` | Json.NET 序列化字典；null 字典为 JSON null |
| `PostBody(url, json, headers)` | 原样提交非 null JSON 字符串，不验证 JSON 内容 |
| `Put(url, param, headers)`、`Delete(url, param, headers)` | UTF-8 JSON 字典请求体 |

地址必须是绝对 HTTP / HTTPS URI；参数字典和 headers 可以传 null。默认原生 timeout 为 5 秒，headers 可覆盖初始 Content-Type。入口立即尝试启动，构造、参数或配置失败通过返回请求表达。

下面是有效 Play 会话的主线程协程片段，`url` 由应用提供：

```csharp
using YLFramework.Http;

var request = HttpManager.Get(url, null, null);
yield return request;

if (request.Status == HttpRequestStatus.Succeeded)
{
    UnityEngine.Debug.Log(request.Text);
}
else
{
    UnityEngine.Debug.LogWarning($"{request.Status}: {request.Error}");
}
```

在 `async Task` 方法中可等待同一请求，不需要另一套 GetAsync/PostAsync：

```csharp
var request = await HttpManager.Get(url, null, null).AsTask();
if (request.Status == HttpRequestStatus.Succeeded)
{
    UnityEngine.Debug.Log(request.Text);
}
else
{
    UnityEngine.Debug.LogWarning($"{request.Status}: {request.Error}");
}
```

await 不会把发送移到后台，也不表示业务成功或自动取消；多个消费者可以等待同一个请求。不要以 `.Wait()` / `.Result` 阻塞主线程，生命周期取消仍由请求 owner 显式 Abort。

## 结果与取消

- `IsDone` 只表示等待结束。`Status` 区分 Pending、Succeeded、Failed、Canceled、Invalidated，终态不再改写。
- 完成后读取 `ResponseCode`、`ResponseHeaders`、`Text`、`Data`、`NativeResult` 和 `Error`；这些是脱离原生传输的快照，协议失败也保留可读取的响应正文。`Exception` 保存构造、配置、发送或快照处理的异常。
- `ResponseHeaders` 为独立、只读且按 OrdinalIgnoreCase 查询的快照；`Data` 已脱离 Handler，但返回数组本身允许调用者修改。轻量请求同时物化 Text/Data，不用于有界内存的大文件或资源续传。
- `NativeResult` 在尚未取得原生快照时保持默认值，不能代替 Status；`ElapsedSeconds` 为创建到提交终态的未缩放耗时，进行中不实时更新。
- `Progress` 本轮只表达等待中 0、结束后 1，不是实时上传/下载进度。
- `Abort()` 必须在创建请求的接受线程调用，跨线程调用会抛异常；它幂等，只停止本请求拥有的传输，不撤销服务器已执行的副作用。终态后在接受线程调用不改变结果。
- 执行会话退出或启动门禁关闭时，未完成请求进入 Invalidated 并释放自有传输和 Handler；构造、配置或发送异常进入 Failed。协程宿主失活/销毁不影响 HTTP，`Module.Close` 也不等于关闭全局 HTTP。
- 普通完成通知和 Task 完成前先释放原生资源；观察或通知异常不反写 HTTP 终态，也不阻止资源收尾。Task 返回已结束请求，失败、取消或失效仍须检查 Status。

## 发送前与完成观察

`HttpManager.PreWebRequest` / `PostWebRequest` 是静态事件，使用 `+=` / `-=` 管理订阅；HTTP 会话重置会清空订阅，新 Play 需要重新注册。它们不是按请求隔离的配置链，并发请求的不同配置需由应用明确区分；本轮不引入新的配置体系。

| 事件 | 可做的事与所有权 |
| --- | --- |
| `PreWebRequest` | 同步配置原生请求，如 timeout 或 Handler；异常使本请求失败。初始 Handler 和回调返回时附着的 Handler 由 HTTP 释放；同一回调内创建后又自行替换掉的中间 Handler 由配置者释放 |
| `PostWebRequest` | 观察原生完成，包括协议或网络错误；第二参数为未缩放耗时秒数。取消、构造/配置失败和 Invalidated 不触发；原生对象仅在回调内借读，不得持有、重发或 Dispose |

完成观察时响应快照和 `Status` 已提交，但基类 `IsDone` 与完成通知要等释放后才更新。应用完成逻辑应通过请求的完成订阅、协程或 AsTask 等待读取快照。默认 5 秒是原生 timeout，并不是应用自建的总请求预算。

## 完成订阅

请求继承 [`CustomAsyncOperation<T>`](Async.md#自定义可等待请求)，可用协程、AsTask 等待，也可用同步完成订阅。

- `AddCompleteEvent` 在请求已结束时立即同步调用新增订阅；异常会传给注册者。常规通知按注册顺序执行，不逐个隔离回调异常。需要解绑时保存同一委托并配对调用 `RemoveCompleteEvent`。
- 完成时自动断开保存的订阅，迟到订阅仅即时执行而不再保存；尚未完成时局部 owner 退出只清自己的委托，不调用 `RemoveAllCompleteEvent` 清其他消费者。移除普通订阅不会移除 Task 等待。
- `IsDone` 不代表成功，以本请求的 `Status` 与响应快照为准。HTTP 自行保证稳定终态与独占传输取消，不把基类 Abort 当作统一取消协议。
- HTTP 自管终态与独占传输；View/Panel、Audio、Resource 的取消、租约和共享生产者遵循各自合同，不由这个基类统一接管。

`PostBody` 有字典和字符串两个重载；直接传 null 时须显式指定类型，例如 `(string)null` 或 `(Dictionary<string, object>)null`。字符串 null 是参数错误，字典 null 则发送 JSON null。

HTTP 不接管文件日志；应用需要时显式启用 [Logging](Logging.md)。
