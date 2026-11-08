# 事件系统（Events）

> [文档首页](../README.md) · [架构总览](../Architecture.md)

## 作用

提供实例事件、独立事件总线、精确订阅句柄与轻量组合。共享机制和 Unity 适配归 Runtime 主体的 Foundation；Core 的 `CoreEvents` 只定义领域事件键，不要求所有通知进入全局上下文。

选择入口：对象自己的通知用 `EventChannel`，需要池化时用 `EasyEvent`；独立业务上下文用显式持有的 `EventBus`；只有确实需要全局通信时才选 `EventBus.Default`。局部 Unity 消息适配见 [局部 Unity 监听组件](#局部-unity-监听组件)。

## 位置

- 共享机制与注销族：[`Runtime/Foundation/Events/Subscriptions/`](../../Runtime/Foundation/Events/Subscriptions/)，归 `YLFramework.Runtime`：Channel / Bus / Key / OrEvent、IUnregister / Scope、EasyEvent 与生命周期注销触发器。
- Foundation 事件族公开 namespace 为 `YLFramework.Events`；业务 asmdef 引用 `YLFramework.Runtime` 即可，无需 Root 或 Module。
- `Unity/`：EventListener 与便捷扩展、MonoEventListener 及物理/动画 Listener，统一使用 `YLFramework.Events`；不把各用途合并为同一组件。
- `Internal/`：EasyEvent 池化入口。Core 的领域 `YLFramework.Core.CoreEvents` 另在 [`Runtime/Core/Events/`](../../Runtime/Core/Events/)。

## 主要功能

### 实例通道与总线

- `EventChannel` / `EventChannel<T1, ...>` 使用 `Action` 0~7 参数；`Register` / `RegisterOnce` 返回独立、不池化的精确注册身份。普通 multicast 注册按 target 展开，一次 token 取消自身剩余整组；`Unregister(delegate)` 删除最后一个相等的普通 target 子序列，Once 使用 token 取消。
- `Trigger` / `Send` 按注册顺序 fail-fast；显式 `TriggerAll` / `SendAll` 才逐监听继续，结束后抛出单个原异常或 `AggregateException`。Once 的 multicast 回调整体作为一个监听，内部仍按委托异常规则中断。
- 发布捕获写时复制数组，普通退订和 `ClearListeners` 不改变已开始的快照，嵌套发布读取新状态。Once 在用户代码前消费，嵌套及旧快照不会重复执行。
- `ResetOwner` 保持 channel 实例但终结旧代次；`Dispose` 永久关闭。两者都截断旧在途快照并撤销旧 token。`IEventSource.Register(Action)` 将无参观察放在同一个强类型序列，不另建先行通道；null 回调不注册。
- `EventBus` 可 `new` 建立独立上下文。`EventKey` 保留完整 Ordinal string、int、枚举类型与完整值、声明 Type 四个独立键域，不使用字符串 hash 路由。default(EventKey) 无效，string 键允许空字符串但拒绝 null。枚举构造键需要装箱，可缓存 `EventKey` 用于重复发布；payload 分发不打包 `object[]`、不反射调用。
- 同一 bus/key 的签名由首次非空注册绑定，按泛型声明类型精确校验，不按实参运行时类型或委托 variance 改签名。空回调仍校验已存在签名；缺失键的发布、退订和空注册不建键。末监听离开及 `ClearListeners` 保留签名，`RemoveKey` / `ClearKeys` 终结旧 channel 后才允许重绑。
- 命名消息使用 `Register(key, callback)` / `Send(key, args)`；类型消息使用 `RegisterMessage<T>` / `RegisterMessageOnce<T>` / `SendMessage<T>` / `SendMessageAll<T>` / `UnregisterMessage<T>`。分开命名避免单参数 string/int/enum 被重载解析为错误的键域；路由仍是同一个 registry。
- `Default` 是显式的共享上下文，Core View 的自动订阅和 `CoreEvents.ViewRemoved` 直接接入。`TryGetDefault(out bus)` 和 `ResetDefault()` 不创建实例；不需要创建上下文的发布先查询是否存在。Default 随 Play 会话重置关闭，独立 `new EventBus()` 由创建者清理。已关闭实例的 Register/Send 抛 `ObjectDisposedException`，旧 token 退订仍安全。

```csharp
using UnityEngine;
using YLFramework.Events;

using var bus = new EventBus();
var subscription = bus.Register<int>("score", value => Debug.Log(value));
bus.Send("score", 10);
subscription.Unregister();
// 同一键仍绑定 int；显式删除后才允许换签名。
bus.RemoveKey("score");

using var changed = new EventChannel<int>();
using var scope = new SubscriptionScope();
scope.Add(changed.Register(value => Debug.Log(value)));
changed.Trigger(20);
changed.ResetOwner(); // 截断旧 owner 的在途快照；实例仍可重新注册。
```

### 共享取消与组合

- `SubscriptionCleanup.ReleaseAll` / `SubscriptionSet.ReleaseAll` 先摘本轮责任，全部尝试后报告，保留失败项和重入新加入项；不在同一轮自动重试失败项。`UnregisterAllRetainingFailures` 为 `IUnregisterList` 提供相同必要清理入口。
- `SubscriptionScope` 只管理明确加入的订阅；Dispose 先永久关闭再清理，关闭后 Add 立即取消。失败责任保留供明确处理；句柄自身若是调用前消费的 `SingleUseUnregister`，失败并不因此变得可恢复。
- `CustomUnregister` 同实例执行中重入为空操作，成功后释放回调，失败可重试；`SingleUseUnregister` 在调用前消费回调，抛错也不会再次执行。`CallbackUnregister` 是值类型，副本各有释放状态；默认构造值使用 StrictAfterSuccess，空回调或重复调用会抛 `NullReferenceException`，不要将任意 IUnregister 都视为可重复执行。
- `OrEvent` 首 listener 才连接 source，末 listener 断开并可再注册重连；重复/追加 source 保留。连接失败回滚本次责任，旧 source 快照和迟到 token 不影响重连后的新轮；Dispose 永久终结并截断自身在途通知。source 描述保留到 Dispose，不是弱订阅。
- `OrEvent` 首次连接尚未完成时，同步重入产生的 listener 属于该轮临时责任，失败时一并回滚；明确断连后重建的新轮不受旧轮回滚影响。取消失败的 source token 保留，可再次 `Dispose` 显式重试。

### 运行时入口

- `EasyEvent`（0~3 个参数）：继承共享 channel 的池化适配（`IOfPool`），`Register` 返回精确 `IUnregister`；支持 `RegisterOnce`、`HasListener` / `ListenerCount`。
- `EasyEvent` 普通注册 / 注销 / `UnregisterAll` 保留在途快照；`OnAllocate` / `OnRecycle` 使用 `ResetOwner`，截断旧代次。仅在需要池化的 owner 中使用，归还后不能继续持有原引用。
- `EasyEvents`：按事件定义 `Type` 管理实例，不是 payload bus。`Clear` / `Remove<T>` 只丢映射；`ClearListeners` 摘本轮映射后逐项清监听，错误集中报告，重入新增映射保留，不自动归池。
- `EventListener` 组件：按 `EventType`（鼠标、拖拽、碰撞、触发器）注册回调，并支持 Inspector 中的 UnityEvent。
- Binding 的 owned event 不随属性回收归池：属性回收只旋转事件代次，保留同实例；Once 使用共享机制。集合顺序、比较器与持久化边界见 [Binding](Binding.md)。
- Action 的 owned 消息事件在宿主结束时终结代次；Audio 的设置通知沿自身 session 守卫管理。事件机制不替代这些消费者的调度、播放或资源所有权。
- 注销机制：`IUnregister` / `IUnregisterList` / `CustomUnregister`，以及随生命周期自动注销的触发器 `OnDestroyUnregisterTrigger`、`OnDisableUnregisterTrigger`、`SceneUnregisterTrigger`。
- 扩展方法：`EventListenerExtension`、`UnregisterExtension`、`RegisterListExtension`。

## 注册 / 注销 API

- 基础注册：`easyEvent.Register(action)` → `IUnregister`；该注册 token 的 `Unregister()` 幂等，只取消本次责任。
- 一次性注册：`RegisterOnce(action)`；立即回调注册：`RegisterWithACall(action)`。
- 批量注销：`handle.AddToUnregisterList(owner)` + `owner.UnregisterAll()` 为快照/fail-fast；抛错后本轮剩余项不会继续，集合也已清空。必要所有权清理使用 `UnregisterAllRetainingFailures()`，逐项尝试并保留失败责任；Unity 生命周期触发器采用后者机制。
- 生命周期绑定：`handle.UnregisterWhenGameObjectDestroyed(go)` / `UnregisterWhenDisabled(go)` / `UnregisterWhenSceneUnloaded(scene)` / `UnregisterWhenAnySceneUnloaded()`。

## 上下文所有权

独立业务域由其 owner 显式持有 `new EventBus()` 并在退出时 `Dispose`；不要仅为调用方便将不同上下文并入 `Default`。Core View 使用共享 Default，由 `RegisterEvent` 管理启用/停用轮次的精确订阅。

发布和清理沿实际 owner：共享发布不需要创建上下文时先 `TryGetDefault`；Module/View 退出只取消自己持有的 token，不调用 Default 的 `ClearKeys` 或 `ResetDefault` 清理其他消费者。`ViewRemoved` 保留 `ON_REMOVE_VIEW` 键和 `SendAll` 的全部尝试/聚合异常语义。

## 生命周期注销

- 触发器先摘本轮责任，逐项尝试；失败项与新加入项保留，不在同轮隐式重试。显式 `trigger.Unregister()` 结束后抛出原异常或 `AggregateException`；Unity 的 OnDisable / OnDestroy / sceneUnloaded 边界全部尝试后集中 `Debug.LogException`。
- `UnregisterWhenGameObjectDestroyed`：随 GameObject 销毁；null / fake-null 宿主立即取消。Unity 2022.3 仅保证曾 active 的对象收到 OnDestroy，从未 active 的宿主需显式清理或使用 `SubscriptionScope`，适配器不会强制激活或扫描对象。
- `UnregisterWhenDisabled`：绑定后下一次实际 OnDisable 才注销，绑定 inactive 对象不立即取消，Enable 不自动重订。触发组件自身销毁时也收尾 pending 责任；销毁后新增责任立即取消。
- `UnregisterWhenSceneUnloaded(Scene)`：捕获显式传入的已加载 `Scene.handle`，只有该场景卸载才取消；active scene 切换、Additive 的其他场景卸载和业务 GameObject 迁移不改变所有权。无效或已卸载 Scene 立即取消，不创建宿主。
- `UnregisterWhenAnySceneUnloaded()`：任意场景卸载时取消本轮 AnyScene 订阅，不清理绑定到其他指定 Scene 的订阅。
- 两种场景绑定共用 `SceneUnregisterTrigger` 常驻宿主。显式 `Unregister()` 或宿主销毁清理其所有场景及 AnyScene 责任；销毁时先解除 sceneUnloaded 监听，不为清理创建新单例。
- 失败项保留只表示责任未被丢弃，是否可重试取决于 token 契约；已消费的 SingleUse 失败不能据此恢复。销毁边界报告失败，不声称全部取消成功。

### 通用 Unity 监听组件

- 指针 / 拖拽使用 `PointerEventData`；3D / 2D 碰撞使用 `Collision` / `Collision2D`；3D / 2D Trigger 使用 `Collider` / `Collider2D`。`OnMouseEnter/Exit` 是 EventSystem 指针入口，不是 Unity 无参鼠标消息。
- `Register<T>` 在分配通道前精确校验原生 payload；错误类型抛 `ArgumentException`，非法 EventType 抛 `ArgumentOutOfRangeException`。正确类型的 null 回调返回空 token；已销毁组件不能重新注册。
- 代码监听由组件私有 `EventChannel<T>` 承载，直接复用共享精确 token，不依赖全局 EasyEvent 池寿命；公共 EasyEvent 池化能力不变。组件 Disable 不自动清代码，需要停用边界时绑定返回 token。销毁 / 清指定类型 / 清全部代码永久关闭对应通道，旧 token 和旧在途代码快照不能进入新通道。
- 始终先代码监听、后 Inspector UnityEvent；代码异常仍阻断后续 UnityEvent。默认 `UnregisterAll()` 不动 Inspector 通道；显式 `UnregisterAll(true)` 把全部 20 个 UnityEvent 字段置 null，不重置物理掩码，仅断开组件的运行时通道，不改资产，也不清外部持有的旧 UnityEvent 对象。
- `PhysicsLayerMask`（序列化字段 `physicsLayerMask`）仅过滤12个Collision/Trigger消息的**对方 GameObject 层**，3D/2D共用，同时约束代码和Inspector；八种指针/拖拽消息不受影响。默认AllLayers，显式None/0不接收物理通知；不修改Unity碰撞矩阵。一次回调入口只过滤一次，代码回调内改掩码只影响下一次消息，不承诺层变化后的Enter/Exit配对或对Stay去重。
- 专用 LayerMask 物理监听、`OnAnimationEvent(string)` 和覆盖完整 EventTriggerType 的 UGUI 扩展各自独立；局部监听组件属于 Events，通用 UGUI 扩展属于 `YLFramework.Extensions`。
- 原生 Inspector 位于 [`Editor/Foundation/Events/EventListenerEditor.cs`](../../Editor/Foundation/Events/EventListenerEditor.cs)，属于 `YLFramework.Editor`。隐藏 Script 字段，默认展开“Unity事件中心”，按鼠标事件、碰撞事件、Trigger 事件展示 20 个中文标签；物理掩码与派生类额外字段通过附加属性显示。折叠和页签只保存在 Editor 会话，不写入组件。UnityEvent 使用原生 Drawer，支持多对象编辑、Undo/Redo 和 Prefab override/apply/revert，无需第三方 Inspector。

### 局部 Unity 监听组件

- `YLFramework.Events.AnimationEventListener` 接收 AnimationClip 的 `OnAnimationEvent(string)`，转发至公开 `animationEvent`；不是 Animator 状态变化监听器。配置 Clip 时使用这个方法名，代码订阅使用该字段。
- `YLFramework.Events.CollisionEventListener` / `TriggerEventListener` 各提供六个 3D / 2D UnityEvent，保留序列化字段 `layerMask`，可通过 `PhysicsLayerMask` 在运行时配置。新组件与 Reset 默认 AllLayers，持久化的显式 0 仍是 None；事件默认初始化，可直接 `AddListener`。
- `YLFramework.Events.MonoEventListener` 提供默认初始化的 `onAwake/onStart/onEnable/onDisable/onDestroy` 五个 UnityEvent，直接对应本对象的原生生命周期；没有 Update、全局宿主、事件总线或 Root 初始化。组件在 `YLFramework/Events/` 菜单中可添加。
- 需要观察首次Awake/OnEnable时，在Prefab或尚未激活的对象上预先配置；对active对象AddComponent后再AddListener不会补发已发生的回调。从未active对象的OnDestroy遵守Unity原生限制，不强制激活来补事件。
- 需要精确 token 和代码 / Inspector 共同物理过滤时使用 `EventListener`；只需专用 Inspector 事件时使用对应局部组件，避免对同一消息重复订阅两套组件。

## Play 会话与 Domain Reload

- `EasyEvents` 全局容器在新 Play 会话清理：标准 channel 终结旧代次，自定义 `IEasyEvent.UnregisterAll` 逐项执行；全部尝试后集中记录异常，不阻断其他模块重置。禁用 Domain Reload 时也执行这些清理。
- Default 的 session reset 关闭旧 bus。旧实例的 Register / Send 抛 `ObjectDisposedException`，退订幂等，旧 token 不影响新注册。
- 独立 `new EventBus()` 不随全局重置清理，由创建者自行 `Dispose`。

## 重要限制

- `EasyEvent` 的 listener 顺序为注册顺序；重复注册同一委托会被调用多次，注销一次只移除一次。
- 默认 `Trigger` / `Send` 为 fail-fast；需要全部尝试时显式使用 `TriggerAll` / `SendAll`，仍会在结束后报告异常。
- 池化 `EasyEvent` 回收及再次分配时都会清空 listener，旧引用不应在回收后继续使用。
- 池租用应通过 `RecycleToCache` / `Recycle` 结束；继承的 `Dispose` 是永久关闭，不是归池或可重开的 reset。调用方不得 Dispose 其他 owner 持有的 borrowed event。
- `EventListener` 必须使用对应 Unity 原生 payload 类型；不接受 `BaseEventData` 等基类替代精确声明类型。
- `RegisterListExtension.UnregisterAll` 为 fail-fast；必要所有权清理使用 `UnregisterAllRetainingFailures`，不要与逐项尝试的 Unity 生命周期触发器混淆。
- Core 的 `CoreEvents` 只描述 View 等领域通知的键，不是另一套总线。
- 事件、句柄与组合器不是线程安全的，应由所属上下文串行使用；Unity 组件适配在主线程使用。
