# 单例（Singleton）

> [文档首页](../README.md) · [架构总览](../Architecture.md)

## 作用

提供多种单例形态：普通 C# 单例、MonoBehaviour 单例（跨场景保留 / 可替换）、以及从 Prefab 或 ScriptableObject 资产创建的单例访问属性，覆盖纯逻辑对象与 Unity 对象两类场景。

## 位置

- 运行时：[`Runtime/Foundation/Singleton/`](../../Runtime/Foundation/Singleton/)
  - `Plain/`：普通 C# 单例与访问属性（`Singleton<T>`、`SingletonProperty<T>`）
  - `Mono/`：MonoBehaviour 单例与访问属性、路径特性（`MonoSingleton<T>`、`MonoSingletonProperty<T>`、`MonoSingletonPathAttribute`、`PersistentMonoSingleton<T>`、`ReplaceableMonoSingleton<T>`）
  - `Asset/`：Prefab / ScriptableObject 单例访问属性（`PrefabSingletonProperty<T>`、`ScriptableSingletonProperty<T>`）
  - `Internal/`：`SingletonCreator` 内部创建逻辑与 `SingletonSessionState` 会话状态重置

公开 namespace 为 `YLFramework.Singletons`，程序集仍为 `YLFramework.Runtime`；目录名 Singleton 不要求与 namespace 同名。旧 `YLFramework.Core.Singleton` 调用方须更新 using/完整名并重编译，不提供旧身份转发壳。

## 主要功能

- 普通单例 `Singleton<T>`：实现 `ISingleton` 的 C# 单例基类；`Instance` 懒创建，`OnSingletonInit` 只在新实例创建时调用一次。
- MonoBehaviour 单例 `MonoSingleton<T>`：支持 DontDestroyOnLoad 与运行时层级路径（`MonoSingletonPathAttribute`）。
- `PersistentMonoSingleton<T>`：场景中重复时保留先创建者；`ReplaceableMonoSingleton<T>`：保留后创建者。
- 懒加载访问属性：`SingletonProperty<T>`、`MonoSingletonProperty<T>`；`Singleton<T>` / `MonoSingleton<T>` 的 `Instance` 分别委托给对应访问属性，共享同一个 T 的缓存。
- 资产单例：`PrefabSingletonProperty<T>`（默认按类型名从 Resources 加载预制体）、`ScriptableSingletonProperty<T>`（默认按类型名从 Resources 加载资产），均可通过 `InstanceWithLoader` 替换加载方式。
- 生命周期回调：`ISingleton.OnSingletonInit()`；访问属性提供 `Dispose()` / `Dispose(bool isDestroy)` 释放实例，下次访问重新创建。

## 用法

```csharp
using YLFramework.Singletons;

public sealed class SessionCache : Singleton<SessionCache>
{
    private SessionCache() { }
    public override void OnSingletonInit() { }
}

// 以下调用放在业务方法中。
var cache = SessionCache.Instance;
bool exists = SessionCache.TryGetInstance(out var sameCache);
cache.Dispose(); // 解除静态缓存，不自动清理 cache 所持资源。
```

普通单例需要无参构造，public / private / protected 均可；示例用 private 防止业务直接构造。资源清理、事件注销等责任应由业务显式完成，`SingletonProperty<T>.Dispose()` 不会调用实例的清理回调。

## 便利 API

- `HasInstance`：当前是否已持有实例（不会触发查找 / 创建 / 加载）。
- `TryGetInstance(out T instance)`：尝试获取已持有的实例（不会触发查找 / 创建 / 加载）。
- 上述两个 API 在 `Singleton<T>`、`MonoSingleton<T>`、`SingletonProperty<T>`、`MonoSingletonProperty<T>`、`PrefabSingletonProperty<T>`、`ScriptableSingletonProperty<T>`、`PersistentMonoSingleton<T>`、`ReplaceableMonoSingleton<T>` 上命名与语义一致。

## 生命周期与重复实例

- `MonoSingleton<T>`：重复实例不会被自动销毁。`Instance` 在查找时警告并稳定采用其中一个实例；需要保留先创建者用 `PersistentMonoSingleton`，需要后创建者替换用 `ReplaceableMonoSingleton`。`OnDestroy` 只清理自己持有的缓存，不会误清新实例；`Dispose()` 会立即清缓存并排除待销毁宿主，同帧再次访问可重建。外部直接调用 `Destroy` 则需等 Unity 实际销毁，同帧仍可能取得待销毁对象。
- `MonoSingletonProperty<T>.Dispose(false)`：只释放缓存，不销毁实例；下次访问会重新认领同一对象并再次执行 `OnSingletonInit`。
- 退出保护：`OnApplicationQuit` 与 `Application.quitting` 会标记退出状态，退出过程中 `Instance` 不再创建新的 MonoBehaviour / Prefab 单例；标记在下一次进入 Play 时自动重置。
- `PersistentMonoSingleton<T>`：保留先创建者，后创建者在 `Awake` 中自杀；被 `Instance` 提前认领的实例在 `Awake` 时仍会 DontDestroyOnLoad。
- `ReplaceableMonoSingleton<T>`：保留后创建者；替换在 `Awake` 中同步完成，`Instance` 立即切换到新实例；同一帧内多个实例通过内部序号决胜。
- `ScriptableSingletonProperty<T>.Dispose()`：仅当资产由默认 Resources 加载方式载入时才调用 `Resources.UnloadAsset`；自定义 loader 载入的资产由调用方自行管理。

## Domain Reload / Play Session

- 每次进入 Play 时（`RuntimeInitializeLoadType.SubsystemRegistration`），重置各类 Singleton 缓存、Replaceable 创建序号、资产加载警告和退出标记；Prefab / Scriptable loader 恢复默认 Resources 方式。Mono 的已处置集合仅移除已销毁占位，仍存活的已退休宿主继续排除，避免重新认领。关闭 Domain Reload 时也执行这些重置。
- `InstanceWithLoader` 属于单次 Play Session 的运行期配置：每个 Play Session 需重新调用。这是与 Domain Reload enabled 行为对齐的有意设计。
- 静态重置注册表属于进程级状态，不随 Play Session 清空。清缓存不等于 Dispose 旧业务对象，不替代其资源清理。

## 依赖

- 使用 UnityEngine 与 Foundation 内的会话机制，不反向依赖 Core、Systems、Utilities 或框架根宿主；同一 Runtime 不等于取消这条源码边界。

## 测试入口

契约入口为 `Tests/EditMode/Foundation/Singleton/SingletonContractTests.cs`，宿主生命周期入口为 `Tests/PlayMode/Foundation/Singleton/SingletonHostPlayModeTests.cs`；Foundation 身份/边界另由 `Tests/EditMode/Foundation/FoundationBoundaryTests.cs` 检查。测试入口不表示当前环境已执行，禁用 Domain Reload 的实际连续 Play 仍需单独验证。

## 备注

- 层级路径会决定单例对象的场景位置，例如 `YLFramework/Pool/GameObjectPool`；修改路径会影响已有场景中的对象查找。namespace 迁移不改这些宿主字符串；旧 UI 与 Resource 服务单例已退出，正式 `UIRootContext` 和显式初始化的 `ResourceSystem` 不采用这些旧单例路径。
- 路径层级已存在时按原样复用（不会把已存在的父节点改为 DontDestroyOnLoad）；路径为空时回退为按类型名创建独立对象并 DontDestroyOnLoad。
- `InstanceWithLoader` 替换静态 loader，只作用于后续创建；已有实例不会被替换。
- Prefab / Scriptable 加载失败（loader 返回 null 或找不到资源）返回 null 并输出一次警告；loader 抛出的异常不会被吞掉，替换 loader 后可重试；loader 内重入 `Instance` 不会递归创建。
- Prefab 缺少目标组件时会销毁已实例化的克隆，不留下半初始化对象。
