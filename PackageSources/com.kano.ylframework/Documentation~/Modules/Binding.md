# 数据绑定（Binding）

> [文档首页](../README.md) · [架构总览](../Architecture.md)

## 作用

提供可监听变化的属性、列表和字典，用于 UI 刷新、逻辑联动等场景；另提供对接 PlayerPrefs / EditorPrefs 的持久化属性。通知同步执行，不是调度器或完整响应式框架。

## 位置

- 运行时：[`Runtime/Foundation/Binding/`](../../Runtime/Foundation/Binding/)
- namespace 保持 `YLFramework.Binding`，程序集为 `YLFramework.Runtime`。依赖的事件与池分别使用 `YLFramework.Events` / `YLFramework.Pooling`，不因共用 Runtime 反向依赖 Core 或 Systems。

## 主要功能

- `BindableProperty<T>`：`Value` 赋值使用 `TrySetValue`，相同值不通知；`WithComparer` 设置实例比较器，否则使用同类型静态 `Comparer`。`ForceNotify` 强制通知当前值，`SetValueWithoutCallback` 只写值，`RegisterOnce` 只监听一次。
- `BindableList<T>`：按操作提供 `OnAdd` / `OnRemove` / `OnReplace` / `OnMove` / `OnClear` / `OnCountChanged`，另有统一事件 `OnChanged`（`BindableListChange<T>`）；提供 `AddRange`、`RemoveAll`、`TryGet`。`Move` 先校验两个索引，越界抛异常且不修改列表。
- `BindableDictionary<TKey,TValue>`：按操作提供事件及统一的 `OnChanged`（`BindableDictionaryChange<TKey,TValue>`），支持 `TryAdd`、`AddOrUpdate`、`Remove(key, out value)`、`GetValueOrDefault`，实现 `IReadOnlyDictionary<TKey,TValue>`。
- `source.Map(selector)`：返回单源派生只读属性 `MappedProperty<TSource,TTarget>`，立即计算初值并订阅源；源变化后按相同值规则通知。调用方负责 `Dispose()`，也可经 `IUnregister` / `IPoolType` 接口释放或绑定 GameObject 销毁；释放只断开源订阅，保留最后值与已有目标监听，不回收到属性池。
- 批量通知：`BindableList` 与 `BindableDictionary` 均提供 `BeginBatch()` / `EndBatch()`。批量期间所有事件被抑制，结束时最多触发一次 `OnCountChanged`（数量确实变化时）和一次 `OnChanged(Reset)`。
- `BindPropertyWithUnityEvent<T>`：先执行代码通知，再调用 `UnityEvent<T>`，前者抛异常会阻断后者；`OnRecycle` 调用 `RemoveAllListeners()` 清理 UnityEvent 的运行时监听，不是修改资产持久绑定的入口。
- 持久化属性：`PlayerPrefsBoolProperty` / `PlayerPrefsFloatProperty` / `PlayerPrefsIntProperty` / `PlayerPrefsStringProperty`；编辑器侧 `EditorPrefsBoolProperty` / `EditorPrefsIntProperty` / `EditorPrefsFloatProperty` / `EditorPrefsStringProperty`（仅在 `UNITY_EDITOR` 下编译）。统一基类提供 `Key`、`DefaultValue`、`HasKey`、`Delete()`、`Reload()`；`Value` 变化立即写回；`Delete()` / `Reload()` 只在实际值变化时通知（与 `BindableProperty` 一致），回调里再次改值会正常写回。

## 快速使用

```csharp
using UnityEngine;
using YLFramework.Binding;

var score = new BindableProperty<int>(0);
using var text = score.Map(value => value.ToString());
var listener = text.RegisterWithInitValue(value => Debug.Log(value)); // 立即输出 "0"。
score.Value = 10; // 输出 "10"；再次写 10 不通知。
listener.Unregister();

var items = new BindableList<int>();
using (items.BeginBatchScope())
{
    items.AddRange(new[] { 1, 2, 3 });
} // 只补发数量变化与一次 Reset，而不是逐项 Add。
```

`RegisterWithInitValue` 先直接回调内部 `StoredValue`，成功后才注册；初始回调抛异常不会留下订阅。重写 `GetValue` 的派生属性需注意，它不通过 `Value` 取初值。

## 主要类型

- `IReadOnlyBindableProperty<T>` / `IBindableProperty<T>` / `BindableProperty<T>`：属性绑定接口与实现。
- `BindableList<T>` / `BindableDictionary<TKey,TValue>`：集合绑定。
- `BindableCollectionChangeType` / `BindableListChange<T>` / `BindableDictionaryChange<TKey,TValue>`：统一变化类型与数据。
- `MappedProperty<TSource,TTarget>`：单源派生只读属性。
- `PlayerPrefsProperty<T>` / `EditorPrefsProperty<T>`：持久化属性基类。
- `CustomProperty<T>` / `BindPropertyWithUnityEvent<T>`：扩展属性类型。

## 事件语义

- 单次操作按“具体事件 → `OnCountChanged` → `OnChanged`”顺序触发；`Replace` / `Move` 不触发 `OnCountChanged`。
- `Clear` 在空集合上也会触发 `OnClear` / `OnChanged(Clear)`，但不触发 `OnCountChanged`。
- 批量通知结束时 `OnChanged(Reset)` 只表示“结构已变化，需要整体刷新”，不携带具体差异；即使批量内净变化为零也会发一次 Reset（只抑制重复通知，不比对快照）。
- 回调基于 `EasyEvent` / 共享 `EventChannel` 的注册快照：回调中新增监听收不到本次事件，普通注销 / `UnregisterAll` 不撤回已开始的快照；owner 的 `ResetOwner` / 回收会截断旧代次。某个监听抛异常会中断后续监听和后续通知阶段，数据修改本身已完成、不回滚。
- 回调内再次修改同一属性或集合会同步触发嵌套分发，事件可能交错；这是 `EasyEvent` 的实时语义，Binding 不加重入锁、也不吞掉合法更新。`RegisterOnce` 先注销再回调，回调抛异常也已注销。
- `BindableList.RemoveAll` 在每轮开始钳制游标，可处理移除通知导致的集合收缩；`match` 谓词本身应保持只读，不保证其内部任意增删安全。
- 批量建议使用 `using (list.BeginBatchScope())` / `using (dict.BeginBatchScope())`：代码抛异常时也会 `EndBatch` 并补发合并通知。`BeginBatch` / `EndBatch` 保留，少调用一次 `EndBatch` 会保持静默直到补齐，多调用一次抛 `InvalidOperationException`（不会破坏状态）。
- 批量作用域不隔离通知异常：`OnCountChanged` 抛异常时，不再执行随后的 `OnChanged(Reset)`；`Dispose` 期间的异常也会向上传播。

## 依赖

- `Events`：变更回调基于 `EasyEvent` / `IUnregister`；可用 `UnregisterWhenGameObjectDestroyed` 等扩展绑定生命周期。
- `Pool`：`BindableProperty<T>.Allocate/Recycle` 与事件复用。

## 测试入口

`Tests/EditMode/Foundation/Binding/BindingContractTests.cs` 检查绑定契约；`Tests/EditMode/Foundation/FoundationBoundaryTests.cs` 检查 Foundation 源码依赖边界。两者是回归入口，不表示当前环境已执行通过。

## 已知限制

- `BindableList(List<T>)` / `BindableDictionary(Dictionary)` 是引用语义：直接包装传入集合，外部修改不会触发事件（`BindableList(IEnumerable<T>)` 构造会复制）。
- 持久化属性不监听外部对 PlayerPrefs / EditorPrefs 的直接修改，需要时调用 `Reload()`；`Delete()` 删除键并把内存值重置为默认值（不写回键）。
- PlayerPrefs 属性仅调用 `PlayerPrefs.SetX`，不主动调用 `PlayerPrefs.Save()`；需要确定落盘时由业务选择时机保存。`SetValueWithoutCallback` 不触发持久化写回，`ForceNotify` 则会走写回与通知流程。
- `Allocate` 得到的基础属性通过 `Recycle` / `RecycleToCache` 归还；外部 `new` 出的对象、派生实例或旧池借用不被当前基础属性池接纳。不要归还或永久关闭其他属性拥有的通知事件。
- 派生属性仅支持单源 `Map`，不支持 Where/Merge/CombineLatest/Throttle 等响应式操作符；多源组合需自行用 `Register` 组装并管理注销。
- 同一个属性在自身回调里再次赋值会导致嵌套分发，后置 listener 可能“先收到新值、再收到外层旧值”；需要严格顺序时应避免在回调里修改同一属性。
- `Map` 的 selector 在后续源变化时抛异常会向上传播，订阅保留，派生值保持上一次成功结果；恢复后继续更新。
- 非线程安全；涉及 UnityEvent / Prefs 的操作应在 Unity 主线程调用。
