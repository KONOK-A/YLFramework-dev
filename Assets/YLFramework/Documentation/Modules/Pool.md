# 对象池（Pool）

> [文档首页](../README.md) · [架构总览](../Architecture.md)

本页导航：[主要类型](#主要类型) · [基本用法](#使用) · [回收约束](#生命周期与回收约束) · [容量与会话](#容量与会话) · [领域边界](#领域边界) · [GameObject 实例池](#gameobject-实例池) · [兼容边界](#兼容边界)

## 作用

普通引用对象和集合共用一套 `ObjectPool<T>` 缓存、身份、容量与释放算法。领域封装负责自己的 reset、借用代次和归还时机，不维护第二套普通缓存。

普通对象不必实现接口，也不要求公开无参构造。`SafeObjectPool<T>` 将 `IOfPool` 生命周期适配到同一内核；`IPoolType` 只表示对象提供释放入口，不承诺该对象一定会缓存。

## 位置

| 内容 | 路径 | 程序集 |
|---|---|---|
| ObjectPool、IPool、工厂接口、Default/Custom 工厂、List/Dictionary、预热/顺序选项 | [`Runtime/Foundation/Pool/`](../../Runtime/Foundation/Pool/)，按 Object/Abstractions/Factories/Collections 分组 | `YLFramework.Runtime` |
| Safe、IOfPool/IPoolType、非公开/动态创建工具、自有 GO 池 | [`Runtime/Foundation/Pool/`](../../Runtime/Foundation/Pool/) | `YLFramework.Runtime` |
| LoadViewArgs 私有参数池、Core 临时集合消费者 | [`Runtime/Core/MVC/Views/Parameters/`](../../Runtime/Core/MVC/Views/Parameters/)、[`Runtime/Core/`](../../Runtime/Core/) | `YLFramework.Runtime` |

公开 namespace 为 `YLFramework.Pooling`；旧 `YLFramework.Core.Pool` 不保留转发类型，调用方须更新引用。Foundation/Core 共用 Runtime 主体，但池中立源码不消费上层领域或外部资源实现；Runtime 仍保留 UGUI/Json.NET，不能视作整个 DLL 零依赖。会话状态不是公共 API。

## 主要类型

| 类型 | 职责与主要 API |
|---|---|
| `ObjectPool<T> : IPool<T>, IDisposable`，`T : class` | 显式实例池；`Allocate`、`TryAllocate`、`Recycle`、`Discard`、`Prewarm`、`Clear`、`Dispose`、`CurStoreCount`、`MaxPoolSize`、`SetObjectFactory`、`SetFactoryMethod` |
| `SafeObjectPool<T>`，`T : class, IOfPool, new()` | 类型单例入口，直接组合 ObjectPool；保留上述实用 API、`Instance`、`Init(maxSize, initialCount)` 及 protected 创建/生命周期钩子 |
| `IOfPool` | `OnAllocate`、`OnRecycle`、`IsRecycled`；可观察领域状态不等于池归属 |
| `ListPool<T>` / `DictionaryPool<TKey,TValue>` | 静态 LIFO 集合入口；`Allocate()` / `Allocate(capacity)`、void `Recycle`、`RecycleToPool`、`Clear`、`MaxPoolSize` |
| `IObjectFactory<T>` / Default / Custom | 纯创建策略，分别为接口、`new()` 和委托；与 reset 分离 |
| `NonPublicObjectFactory<T>` / `MultiObjectFactory` | 保留非公开无参构造信息缓存、动态 Type/泛型带参创建和创建后初始化；不维护缓存、不自动重置成员 |
| `GameObjectPool` / `GameObjectPoolData` | string key + FIFO 实例缓存、稳定归属、转换期保护、预热/容量、场景回迁与永久销毁；Data 共用 Safe 内核，GO 生命周期独立 |

源码入口：[`ObjectPool.cs`](../../Runtime/Foundation/Pool/Object/ObjectPool.cs)、[`SafeObjectPool.cs`](../../Runtime/Foundation/Pool/Object/SafeObjectPool.cs)、[`GameObjectPool.cs`](../../Runtime/Foundation/Pool/GameObject/GameObjectPool.cs) 与 [`GameObjectPoolData.cs`](../../Runtime/Foundation/Pool/GameObject/GameObjectPoolData.cs)。

## 使用

```csharp
using System.Text;
using YLFramework.Pooling;

using var pool = new ObjectPool<StringBuilder>(
    () => new StringBuilder(),
    reset: value => value.Clear(),
    maxPoolSize: 20,
    cacheOrder: PoolCacheOrder.Lifo);
pool.Prewarm(4);
var buffer = pool.Allocate();
try
{
    buffer.Append("current lease");
    // 使用当前借用，不把引用保留到归还之后。
}
finally
{
    pool.Recycle(buffer);
}
```

工厂可以返回接口实现，也可以显式使用 `new ObjectPool<object>(() => MultiObjectFactory.Create(type, constructorArgs))` 创建动态类型。非公开构造可用 `var factory = new NonPublicObjectFactory<MyClass>();`，再传 `factory.Create` 或 `SetObjectFactory(factory)`。这些反射创建工具位于同一 Runtime 的 Foundation；Core 可直接配置自己的显式委托，不新增程序集依赖。IL2CPP/裁剪环境由调用方保留所需构造信息，本专题没有 AOT 自动注册机制。

构造内核时第二个参数是 `onAllocate`，reset 应使用命名参数 `reset:`。涉及借用初始化、资源取得及失败清理时，配置取得/reset/永久丢弃钩子，不把资源销毁寄托于丢引用。

## 生命周期与回收约束

- 工厂必须提供独占的新引用，不能反复返回同一对象或借用其他池对象。工厂返回 null 不缓存；`TryAllocate` 返回 false，但工厂/回调异常仍传播。
- 冷/热取得均先进入转换态再执行取得钩子，成功后才交付。归还只接收本池借出对象，先进入 Returning 再 reset；引用身份不调用用户 Equals/GetHashCode。
- `Recycle` 的 bool **只表示是否入空闲缓存**。满池/关闭池的 false 仍已消费引用，不得重试或继续使用；null、重复、同对象转换期归还拒绝且不重复清理。foreign 抛异常且不产生 reset 副作用。
- Safe 取得前标记 false，回收前标记 true；取得/reset 失败后永久隔离，不回滚成可重试对象。手工修改 `IsRecycled` 不能绕过内核归属或退休状态。
- 取得/reset 错误保留根因，永久清理仍尽力执行；附加清理错误聚合报告。失败隔离只保证不复用污染对象，不等于领域资源必然释放成功。
- `Clear` 和缩容先摘除/退休空闲快照，再逐项尝试永久清理，最后聚合异常，不再 reset。Clear 回调中新归还的缓存保留；`Clear(callback)` 的附加快照回调也逐项尝试。
- `Dispose` 先关闭，禁止取得/预热，不主动清理借出对象。晚归还原池仍清理但不缓存；Safe 新单例拒绝旧池借用，原池 Dispose 不会关闭新单例。
- 普通池不自动反射清零、不自动 Dispose 对象、不管理 Unity fake-null，不提供并发安全或每租用 token。归还后的原始旧别名无法与重新借出的同一引用区分，调用方不得保留。
- 弱键状态不会强制保活失联的借出对象；空闲缓存仍强持对象。热路径有状态查找成本，不承诺零分配或未经测量的性能提升。

## 容量与会话

- 默认空闲容量为 20，正数限制、非正值不限；不是活动配额。调小立即永久丢弃多余空闲对象。普通默认 FIFO，集合/Action/Audio 显式 LIFO；Audio 的 0=no-cache 是领域策略，不是普通池的 0 语义。
- `Prewarm(count, mode)` 按目标空闲数补齐，受容量裁剪，最多尝试缺口次创建；返回本次新增缓存数。`Recycle` 模式执行 reset，`Raw` 不执行取得/reset；Safe Raw 仍正确标记空闲。null 跳过、不循环重试；已经成功缓存的对象不因随后失败而自动回滚。
- 集合归还 Clear 元素，保留 backing capacity；List 保证最小容量，Dictionary 仅冷创建应用请求容量。非法负容量在借用前拒绝，不接纳外部 new 或自定义 comparer 字典。
- 集合与 Core 参数池在新 Play 会话清空闲缓存，不清借出对象；Safe 关闭原会话池并摘单例。实例池由 owner 清理；Action 自己管理 Shutdown 和延迟队列，不受公共池重置顺序替代。
- 无普通池 TTL、自动扫描协程或 Update 宿主。有限容量、显式 Clear/缩容/Dispose 替代固定 scaled-time 淘汰；需要 TTL 的领域按实际时钟/释放需求另行设计。

## 领域边界

- Action 保留 ActionId/Generation 和当前执行栈退出后的延迟归还；失败清理只能延迟丢弃，不重放失败清理。
- Events、Binding、FSM、Resource 经 Safe 复用；Binding readonly 内部事件仅清监听，不独立归池。Audio 私有池永久丢弃负责销毁 Source。
- LoadViewArgs 仅通过 Core 专用入口接管/归还，保留线程、Claim、读取窗口及终态即时清值。Module/Model 不恢复默认隐式池化；普通事件、MappedProperty、普通计时 run 不因接口名而自动池化；计时存储的显式复用由 ReusableTimer 负责。
- 自有 GO 池保持 `MonoSingletonPath("YLFramework/Pool/GameObjectPool")`，`Allocate(key,parent)` 空池返回 null，需要显式预热或接纳已有实例，不自动加载/创建 Prefab。
- 自有 GO 池、正式 Resource 的 InstanceLease/实例缓存与旧资源系统的所有权不同。Core 命名资源使用正式 Resource，历史资源链独立管理自身责任；Foundation 池不接管 Prefab/Bundle 租约、异步取消或物理卸载。通过资源系统创建的实例须归还其原 owner，不能改交普通池释放。

## GameObject 实例池

仅在 Unity 主线程、运行期使用。入口为 `InitOneGoData`、`Allocate`、`Recycle`、`Clear/ClearAll`；`GetCachedCount(key)` 查询不会建桶。宿主根进入 DDOL，缓存归还后停用并挂在失活的桶根下；取得时先设置最终父节点，再激活。`parent == null` 时对象移到当前 active scene，显式 parent 则跟随该 parent 的场景，inactive parent 下仅设置 `activeSelf=true`。

```csharp
var pool = GameObjectPool.Instance;
pool.InitOneGoData("Projectile", maxCapacity: 32, prefab: projectilePrefab, defaultQuantity: 8);
var instance = pool.Allocate("Projectile", projectileRoot);
if (instance != null)
{
    // 初始化本次业务状态。池不猜测组件字段、事件或协程的 reset 契约。
    pool.Recycle("Projectile", instance);
}
pool.Clear("Projectile");
```

- GO 容量为存活空闲上限：`-1` 无限、`0` 禁缓存、其他负数抛错；不是借出数量配额。缩容立即退休多余空闲对象，FIFO 顺序摘除；fake-null 在计数/归还/取得时惰性剔除，不占容量，没有自动 TTL 或扫描宿主。
- Prefab 初始化按目标空闲数补齐，受已校验的容量限制；在 inactive 缓存根下 Instantiate，不因预热激活对象，不保存 Prefab 引用。非法 key、负预热数、目标超容量、缺少需要的 Prefab 均在修改桶前抛错。
- 数组初始化保留“数组长度是目标缓存总量”的行为，但按唯一、存活输入实际处理；null/重复/已空闲实例不占实际接纳名额，也不被误销毁。超出目标的未登记或本桶借出输入会退休销毁。数组的外来归属和非法参数先整体校验；生命周期回调错误不提供全有或全无事务。
- `Recycle(key, obj)` 可接管未登记的场景实例，拒绝 Prefab 资产、缓存根、池宿主及本桶根祖先。首次接管确定归属；跨 key/宿主抛 `InvalidOperationException` 且不修改对象。`Recycle(obj)` 对已登记对象按原 key 路由，只有首次接管使用 `name`，后续改名不改变身份。
- 同实例 Idle/Allocating/Returning/Retired 时不重复归还。激活、停用和父节点回调后复核桶生命周期、对象存活、层级及容量，不把半转换对象发布为空闲或交付到已关闭桶。任意桶的缓存根及后代不能作为 Allocate 的 parent；初始化层级回调不能重入创建同一 Data/key，Clear 可取消在建桶。
- `Clear/ClearAll` 先摘桶再销毁空闲实例及桶根，不主动销毁借出实例；同 key 重建有新生命周期，旧借出对象晚归还到原宿主/key时只销毁，不能进入新桶。内部退休先于停用/Destroy，拒绝同帧待销毁对象重入。宿主销毁和新 Play 会话关闭旧桶；Data 归还原 Safe owner，不静态转接到新池。
- GO 的 bool 同样仅表示是否缓存，满池/旧桶晚归还的 false 已消费引用。原始旧别名不区分租用代次，归还后禁止保留引用；失活缓存实例和根不得由外部激活、改层级或挂载其他对象。调用方外部 `Destroy` 的同帧排队状态无法被通用池预知，fake-null 后会剔除。
- Unity 的 OnEnable/OnDisable 等消息异常由引擎记录，通常不从 SetActive 传播；池维持自己的状态但不承诺自动修复组件业务状态。直接向池传播的 API 异常会隔离/销毁当前实例；批量清理尽力处理后聚合错误。
- `GameObjectPoolData.RootTransform` 只读，`PoolQueue` 只返回队列快照，`Count` 为热路径计数，`MaxCapacity` 负责校验/缩容。不得直接操作内部 Queue 或用字段反射破坏状态。独立 Data 用 `Init/OnRecycle` 管理根；从 Safe 类型单例借出的 Data 可 `RecycleToCache` 归还原池，私有 Safe 使用者显式归还自己的 owner。
- 数组入口为显式三参数 `InitOneGoData(key, capacity, gameObjects)` 或两参数 `InitOneGoData(key, gameObjects)`；容量/Prefab 入口使用前述命名参数，避免误选重载。

GO 池仅共享 Data 的普通缓存和会话支撑，不用 `ObjectPool<GameObject>` 接纳外部实例或处理 fake-null。GameObject 根和实例的所有权不等于 Prefab/Bundle 租约；正式 Resource 的 InstanceLease/实例缓存独占资产 pin、取消和卸载责任，不能由 Foundation 池代替。

<a id="历史-api-迁移"></a>

## 兼容边界

`ClassObjectPool`、`IndependentObjectPool<T>`、`Pool<T>`、`SimpleObjectPool<T>` 已退出；Core `ClassObjectPoolManager/IReuseable`、集合 `StaticStackPool` 也不再保留。没有隐藏第二套算法或永久兼容壳。

| 旧入口/行为 | 正式承接方式 |
|---|---|
| Simple 委托工厂/reset、构造预热、默认无限 FIFO | ObjectPool 显式 `reset:`、`maxPoolSize: 0`，再 `Prewarm(initialSize)`；默认值不机械等同 |
| Pool/Independent 派生缓存、静态自持缓存 | 组合 ObjectPool，领域生命周期配置为委托；不再提供旧普通池基类身份 |
| Class/Manager 的 Type 创建与可选生命周期 | `ObjectPool<object>` + 显式动态工厂及 onAllocate/reset；无全局 Type registry |
| 反射自动写字段/属性 | 退出，不等价复刻；由类型/领域显式 reset，保留构造默认值与必要资源责任 |
| IOfPool 类型入口 | Safe 保留接口回调、单例、工厂/容量/预热及扩展钩子，直接组合内核 |

当前公开域统一为 `YLFramework.Pooling`；脚本 GUID 与 Runtime 程序集保留，不代表旧基类身份、仓库外源码/DLL 或二进制存档自动兼容。外部 new 接纳与异常后重试也不能照搬：普通池只收自己的借出对象，取得/reset 失败后永久隔离。反射创建只是显式能力，不恢复自动成员 reset。

<a id="当前状态"></a>

## 验证与历史

测试入口为 `Tests/EditMode/Foundation/ObjectPoolTests.cs`、`FoundationBoundaryTests.cs`、`Tests/EditMode/Foundation/Pool/` 下的 `PoolContractTests` / `CollectionPoolTests`；GO 专项为 `Tests/PlayMode/Foundation/Pool/GameObjectPoolPlayModeTests.cs`。Resource、Action、Events、Binding、Audio 与 Core 消费者的回归各归其领域；普通缓存不能自动解决这些模块的清理、取消或失效会话排空。

历史集成和验证范围见 [融合记录](../IntegrationPlan.md)，详细方案保留在 Git 历史中，不是导出包运行依赖。阶段进度不作为使用前提；IL2CPP、其他平台与性能结论需有各自的实际验证。
