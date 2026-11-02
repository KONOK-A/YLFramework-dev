# 类型查找（AssemblyTool）

> [文档首页](../README.md) · [架构总览](../Architecture.md)

## 作用

提供“按类型完整名称在当前 AppDomain 已加载的程序集中查找 `Type`”的小工具，并缓存成功结果。

只负责类型名查找，不负责加载程序集、注册类型、插件发现或创建对象。

## 位置

- 运行时：[`Runtime/Foundation/Utilities/AssemblyTool.cs`](../../Runtime/Foundation/Utilities/AssemblyTool.cs)，程序集 `YLFramework.Runtime`。
- namespace：`YLFramework.Foundation`，入口为 `AssemblyTool.GetType(string)`。

## 用法

```csharp
using YLFramework.Foundation;

var type = AssemblyTool.GetType("Game.Controllers.InventoryController");
if (type == null)
{
    // 类型未加载或名称不匹配，由调用方决定如何报告或处理。
}
```

调用方应传入包含 namespace 的类型完整名（已有类型可用 `typeof(T).FullName`）。Core 的字符串 Controller 查询/创建及 ModuleManager 的字符串启动使用此入口。

## 查找规则

- `null` / 空字符串 / 仅空白：返回 `null`。
- 非空名称：遍历 `AppDomain.CurrentDomain.GetAssemblies()`，逐个执行 `assembly.GetType(classFullName)`，返回**第一个**匹配的 `Type`；多个程序集存在同名类型时不建立额外优先级。
- 未找到：返回 `null`。
- 支持嵌套类型（`Outer+Inner`）与泛型定义形式（如 `` System.Collections.Generic.List`1 ``）。
- 不保证支持程序集限定名（assembly-qualified name）；畸形名称可能由底层 `Assembly.GetType` 原样抛出异常，不做拦截。

## 缓存语义

- **只缓存成功结果**：找到的 `Type` 写入 Ordinal 字典，重复查询直接命中；缓存键不自行裁剪或转换大小写，底层类型名解析仍交由 `Assembly.GetType`；
- **失败不缓存**：不存在的类型不写缓存，运行期间新加载程序集后再次查询仍可找到，不存在永久 stale miss；
- 无 `ClearCache` 等手动失效 API；成功结果在当前静态缓存生命周期内复用，后加载的同名类型不会替换已有结果。
- 非线程安全，按 Unity 主线程 / 普通单线程使用。

## 主要类型

- `AssemblyTool`：静态工具类，唯一 public 成员为 `GetType(string)`。

## 依赖

- 无框架内模块依赖（纯 C# / `System` / `System.Reflection`）。

## 测试入口

`Tests/EditMode/Foundation/AssemblyToolTests.cs`：null/空白、完整名与大小写、程序集限定名边界、嵌套/开放泛型、成功缓存、失败后的晚加载与同名类型首匹配。测试路径是回归入口，不代表当前环境已执行通过。

## 备注

- 本工具不为旧 namespace 建别名；身份升级后应显式迁移持久化类型字符串。Unity `MovedFrom` 也不使 `Assembly.GetType` / `Type.GetType` 自动解析旧全名或 AQN。
