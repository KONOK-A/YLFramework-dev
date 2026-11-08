# 红点使用手册

> [包入口](../README.md)

红点由业务来源、逐层聚合的路径树和独立 UI 订阅组成。`ReddotManager` 维护当前会话的数值，`ReddotReadState` 保存调用方自己的已读记录，`Reddot` 只控制表现，不取得来源的修改权。

## 前置与程序集

安装主包与 `com.kano.ylframework.reddot`，Runtime 消费方 asmdef 引用 `YLFramework.RedDots`，namespace 同名。Editor 诊断窗口属于 `YLFramework.RedDots.Editor`。核心不依赖 Save、Gameplay、Excel，也不额外要求字体或 UI 包。

所有 Manager、来源凭据、已读状态与绑定的操作均须在 Unity 主线程串行使用。包来源与自有依赖配置见[安装指南](https://github.com/KONOK-A/YLFramework-dev/blob/main/PackageSources/com.kano.ylframework/Documentation~/Installation.md)，该链接指向源码仓库当前文档。

## 最小数值用法

以下同步演示可放在使用工程。进入 Play Mode 后，订阅先打印 3，Batch 结束后打印 4。方法退出即释放订阅与来源；长期业务应把两者保存在各自 owner 的字段中，在退出时释放。

```csharp
using UnityEngine;
using YLFramework.RedDots;

public sealed class ReddotExample : MonoBehaviour
{
    private void Start()
    {
        using var source = ReddotManager.RegisterSource("Mail/Gift", "inbox", 3);
        using var subscription = ReddotManager.Subscribe(
            "Mail", snapshot => Debug.Log(snapshot.Value));

        using (ReddotManager.BeginBatch())
        {
            source.SetValue(4);
            ReddotManager.SetAggregation("Mail", ReddotAggregation.Max);
        }
    }
}
```

`sourceId` 是业务分配的真实标识，不是 UI 实例名。规范路径与 `sourceId` 的组合必须唯一，不能让多个 UI 重复登记同一个业务来源。

## 路径与来源

路径以 `/` 分段，忽略空段和段外空白，规范结果以 `/` 结尾。例如 ` Mail // Gift ` 规范为 `Mail/Gift/`。匹配使用 Ordinal，区分大小写且按完整段匹配，`Mail` 不会匹配 `Mailbox`。父路径能观察后代聚合，子路径不向下继承祖先值。

空路径不是隐藏总根：读取为空值，`ShouldBeDisplayed` 为 false；空路径订阅最多得到一次初始零快照，不能用来观察整棵树。

| 入口 | 来源语义与释放责任 |
| --- | --- |
| `AddPath` / `RemovePath` | 幂等集合，每个路径最多贡献 1；Remove 只移除此集合来源 |
| `RegisterPath` | 每次登记独立贡献 1，返回幂等 `IDisposable`，由登记者释放 |
| `RegisterSource` | 登记唯一数值来源，返回 `ReddotSource`；用 `SetValue` 改值、Dispose 释放 |
| `GetValue` / `GetSnapshot` | 读取当前缓存聚合值，不捕获完整诊断树 |
| `ShouldBeDisplayed` | 当前聚合值是否大于 0 |
| `ReddotPath` | 持有集合、租约或数值来源的路径去重快照，零值数值来源也保留路径 |

数值为非负 `long`。非法值、重复 sourceId 与聚合溢出在发布新值前拒绝；不能用负数抵消其他业务来源。登记时通知失败会回滚本次尚未交付的来源，错误不会被静默吞掉。

## 聚合与订阅

`SetAggregation` 只改变指定节点自己的规则，不递归修改子树。

| `ReddotAggregation` | 节点结果 |
| --- | --- |
| `Sum`，默认 | 本节点所有来源之和，加各直接子节点的聚合值 |
| `Any` | 本节点来源或任一直接子节点有正值时为 1，否则为 0 |
| `Max` | 本节点来源总和与各直接子节点聚合值中的最大值 |

`Subscribe(path, callback, emitInitial: true)` 默认补发当前快照。只在聚合值变化时通知，即使来源发生变化但聚合相同也不通知。回调内的变更或订阅进入下一轮分发，不递归构造另一套全局广播。

`BeginBatch()` 可嵌套，最外层 Scope Dispose 才刷新合并通知；初始快照补发不受 Batch 延迟。Batch 是通知合并，不是跨多次调用的事务，异常退出不会回滚此前合法写入。观察者异常在清理分发状态后向调用方传播，不使分发器永久卡住。

### 重置与 Play 会话

- `Reset()` 清空所有业务来源，使旧来源凭据和 Batch 失效，保留路径订阅与聚合规则，只通知实际变化。
- 新 Play 会话清空来源、订阅与规则，包括禁用 Domain Reload 的情况。旧凭据不能释放新一代来源，不能跨会话继续使用。
- `ReddotReadState` 是调用方自有实例，不会因 Manager Reset 自动丢失确认集合；它原有的来源绑定会失效，需要按业务生命周期重新绑定。
- 业务 owner 只释放自己登记的来源，UI owner 只退订。关闭某个 Module 不等于自动结束外部红点业务。

## 绑定指示器

在 GameObject 上添加 `YLFramework/Reddot` 组件，配置 Path 与 Indicator。当前序列化字段是 `path` / `indicator`，运行时属性是 `Path` / `Indicator`。

组件在启用时订阅自己的路径并立即刷新，停用或销毁时精确退订，不移除业务来源，也不主动重置指示器的现有显隐。`Refresh()` 可显式同步表现。

只控制明确指定的 Indicator，拒绝控制宿主自身或其祖先，以免关闭监听宿主；不批量启停业务子物体。更换 Indicator 会刷新新对象，不负责复位之前的对象。按钮需要确认或删除业务来源时，调用业务 owner 的方法，再由该 owner 执行 `RemovePath` 或 Dispose 自有凭据，不把组件当作业务删除器。

## 显式已读状态

`new ReddotReadState(readNamespace)` 创建纯内存实例。namespace 是明确的账号/槽位身份，切换账号或槽位时使用新的状态实例，不靠全局 Manager 猜测。

已读信息是精确 `(key, string version)` 集合，不是最大版本号；key/version 使用 Ordinal 比较，非空白字符串的大小写和空白具有意义，不套用路径规范化。

| 入口 | 当前行为 |
| --- | --- |
| `MarkRead(key, version)` / `IsRead` | 只确认/查询这一精确版本 |
| `BindUnreadSource(path, sourceId, key, version)` | 返回自有 `ReddotUnreadSource`，精确当前版本未读贡献 1，已读贡献 0 |
| `ReddotUnreadSource.SetVersion(version)` | 切换要观察的版本，不把该版本标为已读 |
| `Forget(key, version)` | 只忘记这一对，不删除同 key 的其他确认版本 |
| `PruneRemovedKeys(liveKeys)` | 显式清理不在有效 key 集合中的记录，不自动剪枝 |
| `Export()` | 深拷贝当前确认集合，不做 IO |
| `Import(snapshot)` | 全量验证并深拷贝同 namespace 快照后替换确认集合，不执行 IO |
| `Dispose()` | 释放本实例拥有的绑定和记录，不影响其他实例的来源 |

### 当前快照格式

`ReddotReadSnapshot` 当前只接受 schema 1：

```json
{
  "schemaVersion": 1,
  "readNamespace": "account-42/slot-0",
  "records": [
    { "key": "welcome-mail", "versions": ["v1", "v3"] }
  ]
}
```

`records: []` 表示空确认集合；缺字段、错误 schema、空 namespace、空白 key/version、重复 key 或重复版本均非法。每条记录的 versions 必须是非空集合。`Export()` 按 Ordinal 排序输出，Import 不根据版本号推导先后关系。

## 可选 Save 组合

同时安装 `com.kano.ylframework.save` 后，包版本定义启用独立条件程序集 `YLFramework.RedDots.Save`，消费者 asmdef 必须明确引用它。扩展方法仍位于 `YLFramework.RedDots` namespace，不需要手工全局宏。未安装 Save 时核心照常可用。

`ReadReddotSnapshotAsync` / `SaveReddotSnapshotAsync` 扩展现有的调用方自有 `SaveStore`，使用显式 `name`、`id` 与 cancellation token。使用工程先 Export，再把 DTO 交给 Save；读取成功后自己决定何时在主线程 Import。

读取不会导入状态、修改计数、创建 Store 或选择槽位。Missing、备份来源、损坏、IO 失败与取消遵循 Save 原合同，不自动修复或补写默认数据。安装 Save 不会启用 PlayerPrefs、后台持久化或全局已读 Store。

具体 Store 生命周期与错误处理见[Save 手册](https://github.com/KONOK-A/YLFramework-dev/blob/main/PackageSources/com.kano.ylframework.save/Documentation~/README.md)，该链接指向源码仓库当前文档。

## 只读诊断

打开 `YLFramework/Red Dots/Diagnostics`。窗口展示规范路径、值/规则、本节点值、集合与租约计数、数值 sourceId/值、订阅数、generation、dirty 节点和 Batch 状态。它不持有回调/Unity 对象调试引用，也不为业务 sourceId 发明标签。

自动捕获限频 4 Hz，仅在窗口聚焦或显式启用 `Auto Sample When Unfocused` 时执行；`Capture` 只采样一次。关闭或禁用窗口即解除 Editor 更新回调，不创建 Runtime 宿主或全局采样服务。

Runtime 在主线程显式调用 `ReddotManager.CaptureDiagnostics()` 取得不可变副本。`Nodes` 与各节点 `Sources` 都复制、按 Ordinal 排序并只读；订阅或规则创建的零值节点仍可见，没有合成的空路径根。捕获不会刷新 Batch 或调用观察者。

`CaptureSequence` 仅随显式捕获变化，`Generation` 是诊断数值，不是来源凭据。普通读取和更新不构造诊断树；规范路径如 `Mail/Gift/` 可避免重复路径规范化分配，但不表示全部业务操作或诊断零分配。离线逻辑、Editor 表现与实际 Player 性能须分别验证，不把不可用的分配计数器当作测得零分配。

组件 Prefab、场景、持久事件和已读快照属于使用工程。卸载本包不会删除这些内容，应先处理相应组件、API、asmdef 与数据消费引用。
