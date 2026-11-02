# HTTP 请求

> [文档首页](../README.md) · [架构](../Architecture.md) · [资源](Resource.md)

`YLFramework.Http` 提供 Unity 主线程上的轻量 HTTP 请求。它独占每笔传输，不管理认证、重试、下载缓存或全局任务队列；资源发布与更新使用 Resource 自己的流程。

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

## 结果与取消

- `IsDone` 只表示等待结束。`Status` 区分 Pending、Succeeded、Failed、Canceled、Invalidated，终态不再改写。
- 完成后读取 `ResponseCode`、`ResponseHeaders`、`Text`、`Data`、`NativeResult` 和 `Error`；这些是脱离原生传输的快照，协议失败也保留可读取的响应正文。`Exception` 保存构造、配置、发送或快照处理的异常。
- `Abort()` 幂等，只停止本请求拥有的传输，不撤销服务器已执行的副作用。完成后调用不改变 HTTP 结果。
- 执行会话退出、启动拒绝或协程宿主不可用时，未完成请求进入相应失败或失效终态并释放自有传输和 Handler。`Module.Close` 不等于关闭全局 HTTP。
- 完成通知前先释放原生资源；观察或通知异常不反写 HTTP 终态，也不阻止资源收尾。

## 发送前与完成观察

`HttpManager.PreWebRequest` / `PostWebRequest` 是静态事件，使用 `+=` / `-=` 管理订阅；HTTP 会话重置会清空订阅。

| 事件 | 可做的事与所有权 |
| --- | --- |
| `PreWebRequest` | 同步配置原生请求，如 timeout 或 Handler；异常使本请求失败。初始 Handler 和回调返回时附着的 Handler 由 HTTP 释放；同一回调内创建后又自行替换掉的中间 Handler 由配置者释放 |
| `PostWebRequest` | 观察原生完成，包括协议或网络错误；第二参数为未缩放耗时秒数。取消、构造失败不触发；原生对象仅在回调内借读，不得持有、重发或 Dispose |

完成观察时响应快照和 `Status` 已提交，但基类 `IsDone` 与完成通知要等释放后才更新。应用完成逻辑应通过请求的完成订阅或协程等待读取快照。

## 与异步基类的区别

[`CustomAsyncOperation<T>`](../../Runtime/Foundation/Async/CustomAsyncOperation.cs) 只提供 `IsDone`、`Progress`、`Error`、协程等待与同步完成通知，不自动启动工作或切换线程。

- `AddCompleteEvent` 在请求已结束时立即同步调用新增订阅；异常仍会传给注册者。常规通知先调用旧 `Completed`，再调用新订阅；基类不逐个隔离回调异常。
- 派生类使用 `Complete` 或 `CompleteWithoutNotify`。基类重复完成仍可更新 Error，基类 Abort 不天然拥有稳定终态或实际生产者取消权。
- HTTP 自管终态与独占传输；View/Panel、Audio、Resource 的取消、租约和共享生产者遵循各自合同，不由这个基类统一接管。

HTTP 不接管文件日志；应用需要时显式启用 [Logging](Domain.md#logging)。
