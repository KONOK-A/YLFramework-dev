# 日志会话（Logging）

> [文档首页](../README.md) · [安装](../Installation.md) · [架构总览](../Architecture.md)

`YLFramework.Logging` 为 Unity 日志提供同步过滤、转发、订阅和可选文件输出。默认不安装处理器、不创建宿主、不写文件；应用显式决定启用时机、日志内容与输出目录。

源码位于 [`Runtime/Systems/Logging/`](../../Runtime/Systems/Logging/)，属于 `YLFramework.Runtime`。业务 asmdef 引用 Runtime，无需 Root 或 Module，也不自动接管其他日志包。

## 最小接入

在有效 Play 会话的 Unity 主线程中启用一次，由应用级 owner 负责关闭。启用后仍使用 Unity `Debug` API 输出日志：

```csharp
using UnityEngine;
using YLFramework.Logging;

public sealed class LoggingExample : MonoBehaviour
{
    private LoggerSession _session;

    private void Start()
    {
        _session = LoggerManager.Enable(); // 默认不写文件。
        _session.OnLogEvent += OnLog;
        Debug.Log("日志会话已启用");
    }

    private void OnLog(LogType type, string message)
    {
        // 在这里更新自己的记录，不再调用 Debug.Log，避免递归日志。
    }

    private void OnDestroy()
    {
        if (_session == null) return;
        _session.OnLogEvent -= OnLog;
        if (ReferenceEquals(LoggerManager.Current, _session))
        {
            LoggerManager.Disable();
        }
        _session = null;
    }
}
```

此例假定组件是唯一启用者。`Enable()` 返回共享的当前会话，不是引用计数租约；局部消费者只订阅/退订，不应关闭由应用其他 owner 启用的会话。

## 文件与过滤配置

业务启动方法内可显式开启文件，并分别选择控制台和文件保留的类型：

```csharp
using System.Collections.Generic;
using UnityEngine;
using YLFramework.Logging;

var session = LoggerManager.Enable(new LoggerOptions
{
    WriteToFile = true,
    FileFilter = new Dictionary<LogType, bool>
    {
        [LogType.Warning] = true,
        [LogType.Error] = true,
        [LogType.Exception] = true
    }
});
Debug.LogWarning("此条日志进入控制台和文件");
string path = session.LogFilePath;
```

- `WriteToFile` 默认 false。Manager 未收到 Directory 时使用 `Application.persistentDataPath/YLFramework/Logs`；可传应用明确拥有且可写的目录。
- `ConsoleFilter` 与 `FileFilter` 独立：null 表示所有已知 LogType 开启，空字典表示全关，非空字典只放行明确为 true 的类型。过滤不影响 `OnLogEvent`。
- 创建会话时复制过滤配置；之后修改原 options 或字典不改变当前会话。重复 `Enable` 返回同一会话并忽略新配置，更换配置先由 owner `Disable`。
- 只有首条通过文件过滤的日志才创建目录和文件，此前 `LogFilePath` 为 null。文件采用独占新建的唯一名称、UTF-8 无 BOM，按 UTC 时间、LogType、正文写入并逐条 Flush。
- 没有自动轮转、大小配额、压缩、上传或旧日志清理。文件输出是同步 IO，不是后台写入队列；路径权限、容量和保留策略由应用管理。
- 日志可能包含账号、令牌或个人信息，写盘和上传前由应用筛选或脱敏。关闭会话不删除已写文件。

## 订阅、线程与异常

- `OnLogEvent` 通过 `+=` / `-=` 配对管理，参数为 `LogType` 与格式化文本，不包含 Unity context。关闭时清空订阅，关闭后新增订阅抛 `ObjectDisposedException`。
- 通知同步运行在产生日志的原线程，并持有会话锁；不保证 Unity 主线程，回调中不能直接操作场景对象，也不能等待另一条会记录日志的线程，以免死锁。
- 同线程重入同一会话的日志被丢弃并累加 `SuppressedRecursiveCount`，包括格式化 `ToString()` 或订阅中再次产生日志的情况。
- 处理顺序为格式化、尝试目标转发、尝试文件输出、按订阅顺序通知。事件使用委托快照，回调中新订阅不进入本轮；普通退订不撤回本轮快照，回调中 Dispose 会停止后续订阅。
- 目标处理器或单个订阅抛异常时记录到 `LastError`，仍继续后续处理。捕获到的格式错误使用标记文本；用户参数 `ToString()` 抛出的非格式异常不保证被隔离。
- 可捕获的文件 IO、权限或路径错误保存到 `LastError`，关闭写入器并停止本会话后续写盘，不自动重试；控制台和事件仍继续。`LastError` 保留最近一次捕获的错误，成功输出不会清空它。
- 普通日志按当前 Culture 格式化，不额外补采调用栈；异常文本使用 `Exception.ToString()`，原异常与 Unity context 继续传给目标处理器。
- 不修改 Unity 日志开关、调用栈设置，也不按编译符号裁剪输出。Unity 没有投递到此 handler 的日志不会被补录。

## 会话与处理器所有权

- `LoggerManager.Enable` 只允许有效 Play 会话，其他时机抛 `InvalidOperationException`；在主线程创建 DDOL 宿主并安装 handler，借用启用前的处理器作为转发目标。
- `IsEnabled` / `Current` 只查询，不创建宿主。`IsEnabled` 表示仍登记会话，不保证外部代码没有替换 Unity 当前 handler；再次 Enable 不强行夺回处理器位置。
- `Disable()`、宿主 OnDisable/OnDestroy/OnApplicationQuit 和 Play 会话重置均释放本方会话。仅当 Unity 当前仍使用本方 handler 时恢复前一个 handler，不覆盖后来由其他代码安装的处理器。
- 不对 Manager 管理的会话直接 `using`/Dispose，应用级 owner 用 `LoggerManager.Disable()` 完成宿主和处理器收尾；局部订阅者只退订。
- 也可直接 `new LoggerSession(destination, options)` 创建独立会话：不会自动安装到 Unity，destination 由外部 owner 保活且不会被会话 Dispose。开启文件输出时必须显式提供非空白目录。
- 独立会话的 `Dispose()` 幂等，只关闭文件和事件，不修改 Unity 当前 handler。已关闭会话若仍被其他处理器串联，会透明转发调用，不重新开启文件或事件。
