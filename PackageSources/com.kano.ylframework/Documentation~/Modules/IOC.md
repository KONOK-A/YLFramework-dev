# 依赖注入（IOC）

> [文档首页](../README.md) · [架构总览](../Architecture.md)

## 作用

轻量、纯 C#、代码优先的依赖注入容器：注册类型映射与实例，按类型 / 名称解析对象，支持构造函数注入与 `[Inject]` 成员注入，用于解耦业务模块的创建与依赖关系。

定位边界：

- 普通 C# 容器，由使用方自行创建与持有，**不提供全局容器 / ServiceLocator**；
- 不做程序集扫描、Unity 场景注入、GameObject 层级注入、Source Generator；
- 不提供 Scope / Lifetime / Async / 线程安全等大型 DI 能力。

## 位置

- 运行时：[`Runtime/Foundation/IOC/`](../../Runtime/Foundation/IOC/)
  - `Abstractions/`：`IIOCContainer`
  - `Attributes/`：`InjectAttribute`
  - `Collections/`：`TypeMappingCollection` / `TypeInstanceCollection` / `TypeRelationCollection`（public，容器公开状态）
  - `Core/`：`IOCContainer`（含 `MemberSearchModes`）
  - `Exceptions/`：`IOCException` 体系
  - `Internal/`：解析路径、注入元数据与静态反射缓存（internal）

公开 namespace 为 `YLFramework.DependencyInjection`，程序集仍为 `YLFramework.Runtime`；使用 `using YLFramework.DependencyInjection;`。原 `YLFramework.Core.IOC` 不保留转发类型，调用方更新 using、完整类型名及反射/生成绑定后重新编译。

## 快速使用

```csharp
using YLFramework.DependencyInjection;

public interface IMessageSink { }
public sealed class MessageSink : IMessageSink { }

public sealed class Presenter
{
    public IMessageSink Sink { get; }
    public Presenter(IMessageSink sink) => Sink = sink;
}

// 以下调用放在业务方法中。
var container = new IOCContainer();
container.Register<IMessageSink, MessageSink>();
var presenter = container.Resolve<Presenter>(); // 自动构造，并解析构造参数。
container.Clear(); // 只清注册；不会销毁 presenter 或 Sink。
```

类型映射每次解析创建新对象；需要共享实例时使用 `RegisterInstance<IMessageSink>(instance)`，并由调用方负责其资源释放。

## 注册

- `Register<TSource>()` / `Register<TSource, TTarget>()` / `Register(Type, Type, name)`：注册类型映射，要求目标可赋值给源类型。
- `RegisterInstance(...)`：注册实例，可命名；`injectNow` 控制注册时是否立即成员注入；实例为 null 时视为显式注册空值（`IsRegistered` 为 true，解析结果为 null）。
- `RegisterInstance` 先写入注册再注入；立即注入失败时，注册和此前成功写入的成员不会回滚。
- `RegisterRelation<TFor, TBase, TConcrete>()`：注册关系映射。
- **冲突语义统一为“后注册覆盖先注册”**：同一 `(类型, 名称)` 键下 mapping 覆盖 mapping、instance 覆盖 instance；instance 与 mapping 可共存，解析时 instance 优先。
- 名称的 `null` 与 `""` 等价（无名注册）。
- abstract / interface 目标允许注册（便于先用实例覆盖），但没有实例可用时解析会抛出清晰异常。

## 解析

解析优先级（`Resolve<T>` 与 `Resolve(Type)` 行为一致）：

1. 已注册实例（精确 `(类型, 名称)` 键）；
2. 类型映射 → 创建目标类型；
3. 未注册的 concrete class 自动构造（**仅无名解析**，且不可为 abstract / interface / open generic / 数组 / 值类型 / string / delegate）；
4. 均不可用返回 null。

- `requireInstance: true` 时只返回已注册实例，跳过映射与自动构造。
- 映射只跳一层：映射目标必须是可实例化的具体类型，不支持映射链。
- `IsRegistered<T>()` / `IsRegistered(Type)`：是否已有显式注册（instance 或 mapping）。
- `TryResolve`：与 `Resolve` 同一语义，仅当结果为空时返回 false；创建与注入阶段的真实错误照常抛出，不吞异常。

## 构造函数注入

- 选择“参数最多的公开实例构造函数”；**最大参数数量并列时 fail-fast**（异常列出并列签名），不依赖反射返回顺序，也不做“可解析性回退”。
- 没有公开构造函数时回退非公开无参构造（兼容历史行为）。
- 参数解析顺序：无名显式注册 → 参数名命名注册 → 完整 `Resolve`（未显式注册的 concrete class 自动构造）→ optional 默认值；仍失败抛出带依赖链的异常。显式注册先于自动构造，避免 concrete 参数遮蔽命名注册。
- 数组参数优先使用显式注册的数组实例 / 映射（无名或参数名命名）；`params` 参数在无显式注册时传空数组；可选数组参数在无匹配元素注册时使用默认值；其余数组按元素类型的注册生成强类型数组（`ResolveAll`）。
- 显式参数（`CreateInstance(type, args)` / `Resolve(type, name, false, args)`）直接匹配公开构造函数，不再自动解析。
- 构造函数自身抛出的异常作为 `InnerException` 保留；`TargetInvocationException` 会被解包；构造函数体内触发的 IOC 异常（如循环依赖、依赖缺失）保留原异常类型与依赖链，不降级为普通解析失败。

## 循环依赖检测

- 一次解析链内维护类型路径栈；同一类型重复构造（自引用、A→B→A、A→B→C→A、构造与成员注入混合）立即抛出 `IOCCircularDependencyException`，消息与 `CyclePath` 包含完整链（如 `A -> B -> A`），不会栈溢出。
- 菱形依赖、同一类型顺序出现多次不会误报；异常路径通过 try/finally 保证出栈，容器可继续使用。

## 成员注入

- `Inject(object)`：注入带 `InjectAttribute` 的实例字段与属性；`null` 对象不执行任何操作；值类型对象（装箱副本无法回写）抛出异常；成员按顺序注入，某个成员失败时之前已注入的成员保持，异常向上传播（无事务回滚）；重复调用覆盖已有值。
- `InjectAll()`：对全部已注册实例注入；开始时快照并按引用去重；装箱值类型实例跳过（无法回写成员）；注入过程中新注册的实例本轮不处理；注入回调中再次调用 `InjectAll()` 会被忽略（由外层快照完成本轮注入）；任一实例失败 fail-fast（不回滚已完成的注入）。
- setter 自身抛出的异常会解包 `TargetInvocationException`，异常消息与 `InnerException` 保留原始异常类型、消息与成员上下文。
- `MemberSearchModes` 构造时指定，默认 `OnlyPublic`：只搜索公开实例字段与属性（含继承）。`OnlyNonPublic` 搜索非公开成员（private / protected / internal，含基类私有），`All` 搜索两者。
- static / const / 方法 / 事件不参与扫描；被标记但不可注入的成员（readonly 字段、无 setter 属性、indexer）抛出 `IOCInjectionException` 并说明原因；private setter 属性可注入。
- 被标记的成员视为必需依赖：未注册且无法自动构造时抛异常；显式注册的 null 依赖按 null 注入。

成员声明还有默认 Warning 的 [YL0008](Attributes.md#yl0008) 提前检查：static/const/readonly 字段、static 属性、无 setter 属性及 indexer 不属于受支持的成员注入形态；真实 Inject 的派生特性同样识别。private setter 合法，不会仅因 private 报警。规则不推测 `MemberSearchModes`、注册是否存在或名称能否解析，也不替代值类型 owner 与运行时异常检查。

```csharp
class Presenter
{
    [Inject] public static object Shared; // YL0008：不会被实例扫描注入
    [Inject] public readonly object Locked; // YL0008：改用构造注入等显式设计
    [Inject] public object Service { get; private set; } // 合法
}
```

不要为了消警随意移除 readonly、改成实例成员或添加 setter；按业务所有权选择构造函数注入、显式 `Resolve` 或可写成员。配置入口为 `YLFramework/Core/分析器规则`，不提供猜测业务设计的自动修复。

## ResolveAll

- 包含无名与命名注册，匹配规则为“注册键类型可赋值给请求类型”。
- 每个 `(类型, 名称)` 键下 instance 优先于 mapping，不重复创建。
- 顺序为稳定契约：无名在前，命名按名称 Ordinal 排序；不要把它当作数据一致性保证，集合内容变化会改变结果。
- 结果为空返回空序列；创建失败抛出带上下文的异常；枚举期间注册不会破坏遍历（快照）。

## 关系映射

- `RegisterRelation` / `ResolveRelation` 按 `(TFor, TBase)` 解析具体实现；关系只是薄的二级 key 映射，与普通类型映射互不影响。
- 重复注册覆盖；关系缺失返回 null；具体实现走 `CreateInstance`（含注入与循环检测）；`ResolveRelation<TBase>` 的转换失败会包装为带上下文的异常。

## 所有权与清理

- 容器只保存引用：`RegisterInstance` 的对象归调用方，`Resolve` 创建的 transient 由调用方持有，容器不追踪、不 Dispose。
- `Clear()` 清空 mappings / instances / relations，不 Dispose 任何实例，不清静态反射缓存；清空后容器可继续注册与解析。
- 不提供 Lifetime / Scope / Dispose 跟踪。

## 错误诊断

使用异常类型区分失败原因，避免 `KeyNotFoundException` 式的模糊错误：

- `IOCRegistrationException`：注册参数非法或类型不可赋值；
- `IOCResolutionException`：依赖缺失、目标不可实例化、构造选择歧义、构造函数抛异常（`DependencyPath` 含依赖链）；
- `IOCCircularDependencyException`：循环依赖（`CyclePath`）；
- `IOCInjectionException`：成员注入失败（不可注入成员或成员依赖缺失）。

正常解析过程不刷 Log。

## 反射缓存

- internal 静态缓存：类型 → 注入成员清单 / 构造计划；仅缓存纯静态元数据，不缓存容器与解析对象。
- `Clear()` 不清缓存；不做线程同步（与容器一致按单线程使用）。

## 已知限制

- 不保证线程安全，仅按 Unity / 普通单线程业务使用。
- 不支持 open generic、映射链、装饰器链、上下文绑定与复杂 Lifetime。
- `ResolveAll` 某项创建失败会立即抛出，已产出的项不回滚。
- init-only 属性未做特殊校验，不应依赖容器保证其只在构造阶段被写入；引入这类属性时需自行约束注入方式。
- 未注册 concrete class 自动构造不适用于 string / delegate / 值类型 / 数组；命名解析不会自动构造。
- 非公开成员搜索包含基类私有字段，但不扫描 static / const 成员（标记了也会被忽略）。

## 主要类型

- `IIOCContainer` / `IOCContainer`：容器接口与实现（含 `MemberSearchModes`）。
- `InjectAttribute`：标记需要注入的实例字段 / 属性，可指定注册名称。
- `TypeMappingCollection` / `TypeInstanceCollection` / `TypeRelationCollection`：容器公开的多键集合，直接操作不经过注册校验。
- `IOCException` / `IOCRegistrationException` / `IOCResolutionException` / `IOCCircularDependencyException` / `IOCInjectionException`：异常体系。

## 依赖

- 无框架内模块依赖（纯 C#，使用反射）。

## 测试入口

`Tests/EditMode/Foundation/FoundationBoundaryTests.cs` 检查 Foundation 的身份与源码依赖边界，不等同于完整容器行为验证；不能据此承诺构造、注入与异常路径均已通过测试。

## 备注

- 容器为普通 C# 对象，需要由使用方自行持有与生命周期管理（框架内暂无全局容器入口）。
