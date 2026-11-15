# 对象池（Pool）

> [文档首页](../README.md) · [架构总览](../Architecture.md)

本页导航：[主要类型](#主要类型) · [基本用法](#使用) · [回收约束](#生命周期与回收约束) · [容量与会话](#容量与会话) · [领域边界](#领域边界) · [GameObject 实例池](#gameobject-实例池)

## 作用

普通引用对象和集合共用一套 `ObjectPool<T>` 缓存、身份、容量与释放算法。领域封装负责自己的 reset、借用代次和归还时机，不维护第二套普通缓存。

普通对象不必实现接口，也不要求公开无参构造。`SafeObjectPool<T>` 将 `IOfPool` 生命周期适配到同一内核；`IPoolType` 只表示对象提供释放入口，不承诺该对象一定会缓存。

## 位置

| 内容 | 路径 | 程序集 |
|---|---|---|
| ObjectPool、IPool、工厂接口、Default/Custom 工厂、List/Dictionary、预热/顺序选项 | [`Runtime/Foundation/Pool/`](../../Runtime/Foundation/Pool/)，按 Object/Abstractions/Factories/Collections 分组 | `YLFramework.Runtime` |
| Safe、IOfPool/IPoolType、非公开/动态创建工具、自有 GO 池 | [`Runtime/Foundation/Pool/`](../../Runtime/Foundation/Pool/) | `YLFramework.Runtime` |
| LoadViewArgs 私有参数池、Core 临时集合消费者 | [`Runtime/Core/MVC/Views/Parameters/`](../../Runtime/Core/MVC/Views/Parameters/)、[`Runtime/Core/`](../../Runtime/Core/) | `YLFramework.Runtime` |

公开 namespace 为 `YLFramework.Pooling`，业务 asmdef 引用 `YLFramework.Runtime`。普通对象池不需要 Root、Module 或 Unity 宿主；GameObject 池使用自己的运行期宿主。池中立源码不消费上层领域或外部资源实现，整个 Runtime DLL 的依赖见 [架构](../Architecture.md)。

## 主要类型

| 类型 | 职责与主要 API |
|---|---|
| `ObjectPool<T> : IPool<T>, IDisposable`，`T : class` | 显式实例池；`Allocate`、`TryAllocate`、`Rent`、`Recycle`、`Discard`、`Prewarm`、`Clear`、`Dispose`、`CurStoreCount`、`MaxPoolSize`、`SetObjectFactory`、`SetFactoryMethod` |
| `sealed SafeObjectPool<T>`，`T : class, IOfPool, new()` | 私有无参构造的类型单例入口，直接组合ObjectPool；保留实用API、`Instance`、`Init(maxSize, initialCount)`，不提供派生覆写。创建定制走工厂，生命周期走IOfPool |
| `IOfPool` | `OnAllocate`、`OnRecycle`、`IsRecycled`；可观察领域状态不等于池归属 |
| `ListPool<T>` / `DictionaryPool<TKey,TValue>` | 静态LIFO集合入口；`Allocate()` / `Allocate(capacity)`、`Rent`、void `Recycle`、`RecycleToPool`、`Clear`、`MaxPoolSize`、`MaxRetainedCapacity` |
| `IObjectFactory<T>` / Default / Custom | 纯创建策略，分别为接口、`new()` 和委托；与 reset 分离 |
| `NonPublicObjectFactory<T>` / `MultiObjectFactory` | 非公开无参构造信息缓存、动态 Type/泛型带参创建和创建后初始化；不维护对象缓存、不自动重置成员 |
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

Unity2022 LTS与IL2CPP属于实际支持目标，不因日常开发优先当前版本而排除。兼容检查按包声明的最低版本和实际后端执行；当前覆盖2022.3.62f3与6000.3.27f1的Windows x64 Mono/IL2CPP构建运行。泛型租约、集合与实际Skill/Buff复用路径不依赖运行时Emit；只靠反射可达的业务类型仍需显式保留，不能将默认裁剪检查扩大成任意High裁剪/任意平台保证。

构造内核时第二个参数是 `onAllocate`，reset 应使用命名参数 `reset:`。涉及借用初始化、资源取得及失败清理时，配置取得/reset/永久丢弃钩子，不把资源销毁寄托于丢引用。

## 作用域租约

`ObjectPool<T>.Rent(out T)` 与 `SafeObjectPool<T>.Rent(out T)` 返回 `PoolLease<T>`，保留相同工厂和领域钩子：

```csharp
using var pool = new ObjectPool<List<int>>(() => new List<int>(), reset: items => items.Clear());
using (var rental = pool.Rent(out var items))
{
    try
    {
        items.Add(1);
        // 在作用域内使用，不把 items 保存到字段、回调或异步任务。
    }
    catch
    {
        rental.Discard(); // 由 owner 判断是否不再适合缓存，仍执行清理。
        throw;
    }
} // 正常 Dispose 尝试缓存；已 Discard 时不重复清理。
```

- `Dispose()` 正常回收，`Discard()` 清理后永久丢弃；两者消费同一个借用代次。复制件、默认租约、重复及旧代次调用不能结束新借用，失败清理也不重放。
- 有效租约必须通过租约结束，不能用 `Recycle(item)` 或 `Discard(item)` 绕过代次。租约保留原池，原池关闭后晚结束仍清理但不缓存。
- 工厂返回 null 时输出 null 和默认租约，它可安全结束，不表示取得了有效对象。代次耗尽不回绕，会隔离该对象并报错。
- 租约是 readonly struct，不逐次创建租约类实例；转存为 `IDisposable` 会装箱，闭包/异步捕获有各自成本，实际分配以对应环境测量为准。
- 租约不阻止原始引用逃逸或结束后继续写入，也不提供线程安全，不替代 Action/Audio/Resource 的领域句柄。
- `YL0011` 对真实四种框架池的Rent直接弃值发出Warning，包括表达式语句与实际`_ =` discard。合法using、命名保存、返回或明确交接不报；它不证明所有路径都已释放，也不做通用逃逸分析。见[诊断边界](Attributes.md#yl0011)。

## 生命周期与回收约束

- 工厂必须提供独占的新引用，不能反复返回同一对象或借用其他池对象。工厂返回 null 不缓存；`TryAllocate` 返回 false，但工厂/回调异常仍传播。
- 冷/热取得均先进入转换态再执行取得钩子，成功后才交付。归还只接收本池借出对象，先进入 Returning 再 reset；引用身份不调用用户 Equals/GetHashCode。
- `Recycle` 的 bool **只表示是否入空闲缓存**。满池/关闭池的 false 仍已消费引用，不得重试或继续使用；null、重复、同对象转换期归还拒绝且不重复清理。foreign 抛异常且不产生 reset 副作用。
- Safe 取得前标记 false，回收前标记 true；取得/reset 失败后永久隔离，不回滚成可重试对象。手工修改 `IsRecycled` 不能绕过内核归属或退休状态。
- 取得/reset 错误保留根因，永久清理仍尽力执行；附加清理错误聚合报告。失败隔离只保证不复用污染对象，不等于领域资源必然释放成功。
- `Clear` 和缩容先摘除/退休空闲快照，再逐项尝试永久清理，最后聚合异常，不再 reset。Clear 回调中新归还的缓存保留；`Clear(callback)` 的附加快照回调也逐项尝试。
- `Dispose` 先关闭，禁止取得/预热，不主动清理借出对象。晚归还原池仍清理但不缓存；Safe 新单例拒绝旧池借用，原池 Dispose 不会关闭新单例。
- 普通池不自动反射清零、不自动 Dispose 对象、不管理 Unity fake-null 或提供并发安全。原始 Allocate/Recycle/Discard 没有每租用token，不能识别再次借出后的旧别名；代次保护仅属于Rent租约，不能防止调用方直接误用旧对象引用。
- 弱键状态不会强制保活失联的借出对象；空闲缓存仍强持对象。热路径有状态查找成本，不承诺零分配或未经测量的性能提升。

## 容量与会话

- 数量上限默认20，`-1`不限、`0`禁缓存，`<-1`在构造或赋值前抛错且不改变原状态。0不是活动配额：Allocate/Rent仍可创建和借出，归还仍reset并永久清理；调成0先退休全部闲置，不打断在用对象。普通默认FIFO、集合/Action/Audio显式LIFO不变。
- `Prewarm(count, mode)`按目标空闲数补齐，受非负数量上限裁剪，0容量时返回0且不调用工厂；-1不限时可补齐超过默认20的目标。目标count非正仍不补齐，不是容量特殊值。`Recycle`执行reset、`Raw`不执行取得/reset，Safe Raw仍标记空闲。null跳过、不无限重试，成功项不因后续失败回滚。
- List与Dictionary的Allocate/Rent冷热借用均保证最小容量，非法负参数在借用前拒绝；不接纳外部new或自定义comparer字典。归还先Clear元素，再按实际backing capacity决定保留，超大对象丢弃而不是每次Trim。
- `MaxRetainedCapacity`按封闭类型共享，List默认4096实际槽位、Dictionary默认8192，-1不限、0仅保留实际容量0，其他负数抛错。它与数量MaxPoolSize独立：容量上限0不等于禁缓存，数量上限0才完全禁缓存。字典请求元素数与实际槽位不同；降低阈值清闲置不打断借用。这不是字节预算，特殊大容量/长期存储由明确owner持有，不临时改全局配置。
- 集合的静态Rent入口示例：`using var rental = ListPool<int>.Rent(out var items, capacity: 32);`；字典同理。作用域内不裸归还，需要永久退出可调用`rental.Discard()`；数量上限与保留容量是两个独立约束。
- 集合新Play会话清闲置并切换缓存代次，旧借用晚归还仍Clear但不能进入新缓存；公共Clear只清闲置，当前借用仍可正常归还。Core参数池保留自己的会话/Claim合同，Safe关闭原会话池并摘单例。实例池由owner清理；Action自己管理Shutdown和延迟队列，不受公共池重置顺序替代。
- 无普通池 TTL、自动扫描协程或 Update 宿主。有限容量、显式 Clear/缩容/Dispose 替代固定 scaled-time 淘汰；需要 TTL 的领域按实际时钟/释放需求另行设计。
- 会话重置逐项尽力执行已登记清理，最后聚合错误；任意失败时执行门禁保持关闭，健康初始化才重新开放。它不替代各领域的退出责任，也不以吞异常继续运行。

## 数量与 Safe API 迁移

本次为定向公开合同收敛，不保留旧语义或继承壳：

- 以前用 `maxPoolSize: 0` / `MaxPoolSize = 0` / `Init(0, count)` 表示不限的调用，改为对应的`-1`；其他旧负值也统一用`-1`。确实需要禁用缓存时才使用0。
- `SafeObjectPool.Init(int maxSize, int initialCount)`仍是两个显式参数，不新增可选/nullable哨兵；`Init(0,25)`禁缓存且不预热，`Init(-1,25)`可补齐25个。内部Action池已显式迁到-1，不改变其原不限策略。
- 原Safe子类把创建逻辑改为`SetObjectFactory`/`SetFactoryMethod`，对象准备/回收移到`T.IOfPool.OnAllocate/OnRecycle`；`IsRecycled`标记顺序不变。需要独立owner或不同清理策略时，组合显式`ObjectPool<T>`，不要反射创建私有Safe作为正式扩展方式。
- Safe不再暴露protected字段/virtual方法；Allocate/TryAllocate/Rent进入相同内核，不会因派生覆写而获得不同语义。反射构造单例仍保留无参入口，旧owner晚归还与会话隔离不变。

## 领域边界

- Action 保留 ActionId/Generation 和当前执行栈退出后的延迟归还；失败清理只能延迟丢弃，不重放失败清理。
- Events、Binding、FSM、Resource 经 Safe 复用；Binding readonly 内部事件仅清监听，不独立归池。Audio 私有池永久丢弃负责销毁 Source。
- LoadViewArgs 仅通过 Core 专用入口接管/归还，受线程、Claim、读取窗口及终态即时清值约束。Module/Model 不默认隐式池化；普通事件、MappedProperty、普通计时 run 不因接口名而自动池化；计时存储的显式复用由 ReusableTimer 负责。
- 自有 GO 池保持 `MonoSingletonPath("YLFramework/Pool/GameObjectPool")`，`Allocate(key,parent)` 空池返回 null，需要显式预热或接纳已有实例，不自动加载/创建 Prefab。
- 自有 GO 池与 [Resource](Resource.md) 的 InstanceLease/实例缓存是不同的 owner。Foundation 池不接管 Prefab/Bundle 租约、异步取消或物理卸载；通过资源系统创建的实例须归还其原 owner，不能改交普通池释放。

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

- GO 容量为存活空闲上限：`-1` 无限、`0` 禁缓存、其他负数抛错；不是借出数量配额。缩容立即退休多余空闲对象，FIFO 顺序摘除。正常取得沿出队跳过 fake-null，归还仅在容量/目标压力下全队列修剪；显式精确计数、快照与缩容仍扫描，不引入自动 TTL 或扫描宿主。
- Prefab 初始化按目标空闲数补齐，受已校验的容量限制；在 inactive 缓存根下 Instantiate，不因预热激活对象，不保存 Prefab 引用。非法 key、负预热数、目标超容量、缺少需要的 Prefab 均在修改桶前抛错。
- 数组初始化保留“数组长度是目标缓存总量”的行为，但按唯一、存活输入实际处理；null/重复/已空闲实例不占实际接纳名额，也不被误销毁。超出目标的未登记或本桶借出输入会退休销毁。数组的外来归属和非法参数先整体校验；生命周期回调错误不提供全有或全无事务。
- `Recycle(key, obj)` 可接管未登记的场景实例，拒绝 Prefab 资产、缓存根、池宿主及本桶根祖先。首次接管确定归属；跨 key/宿主抛 `InvalidOperationException` 且不修改对象。`Recycle(obj)` 对已登记对象按原 key 路由，只有首次接管使用 `name`，后续改名不改变身份。
- 同实例 Idle/Allocating/Returning/Retired 时不重复归还。激活、停用和父节点回调后复核桶生命周期、对象存活、层级及容量，不把半转换对象发布为空闲或交付到已关闭桶。任意桶的缓存根及后代不能作为 Allocate 的 parent；初始化层级回调不能重入创建同一 Data/key，Clear 可取消在建桶。
- `Clear/ClearAll` 先摘桶再销毁空闲实例及桶根，不主动销毁借出实例；同 key 重建有新生命周期，旧借出对象晚归还到原宿主/key时只销毁，不能进入新桶。内部退休先于停用/Destroy，拒绝同帧待销毁对象重入。宿主销毁和新 Play 会话关闭旧桶；Data 归还原 Safe owner，不静态转接到新池。
- GO 的 bool 同样仅表示是否缓存，满池/旧桶晚归还的 false 已消费引用。原始旧别名不区分租用代次，归还后禁止保留引用；失活缓存实例和根不得由外部激活、改层级或挂载其他对象。调用方外部 `Destroy` 的同帧排队状态无法被通用池预知，fake-null 后会剔除。
- Unity 的 OnEnable/OnDisable 等消息异常由引擎记录，通常不从 SetActive 传播；池维持自己的状态但不承诺自动修复组件业务状态。直接向池传播的 API 异常会隔离/销毁当前实例；批量清理尽力处理后聚合错误。
- `GameObjectPoolData.RootTransform`只读；PoolQueue扫描存活实例并分配快照，Count扫描精确存活数，均O(n)，不应每次借还调用。MaxCapacity负责校验/缩容，不得反射操作队列。独立Data用Init/OnRecycle管理根，从Safe类型单例取得的Data向原Safe归还；显式owner自行管理Data，不由静态入口接管。
- 数组入口为显式三参数 `InitOneGoData(key, capacity, gameObjects)` 或两参数 `InitOneGoData(key, gameObjects)`；容量/Prefab 入口使用前述命名参数，避免误选重载。

GO 池仅共享 Data 的普通缓存和会话支撑，不用 `ObjectPool<GameObject>` 接纳外部实例或处理 fake-null。GameObject 根和实例的所有权不等于 Prefab/Bundle 租约；正式 Resource 的 InstanceLease/实例缓存独占资产 pin、取消和卸载责任，不能由 Foundation 池代替。
