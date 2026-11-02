# JSON 树（JSONObject）

> [文档首页](../README.md) · [架构总览](../Architecture.md)

## 作用

提供轻量 JSON 容器：解析字符串、构建 JSON 对象树、按字段读写并输出字符串；同时提供 Vector、Color、Rect 等 Unity 类型的互转扩展，具体清单见 [JSONTemplates](#jsontemplates)。

该模块的定位是**轻量 JSON 解析 / 构建 / 字段操作工具**，不是完整对象序列化框架，不提供反射 Object Mapper、Schema Validator、JSONPath / LINQ to JSON。

JSONObject 用于直接遍历 / 修改 JSON 树；Json.NET 用于强类型对象序列化。Save 继续拥有存档格式、文件与恢复策略，HTTP 继续拥有请求体与传输生命周期，两者不强制改用 JSONObject。本模块不提供全局 JSON Manager，也不接管存档或网络服务。

## 位置

- 运行时：[`Runtime/Utilities/Json/`](../../Runtime/Utilities/Json/)
  - `JSONObject.cs`：核心节点与 Parser / Builder / 字段 API。
  - `JSONObjectEnumerator.cs`：容器枚举器。
  - `JSONTemplates.cs`：Unity 类型互转扩展。
  - `readme.md`：上游说明。
- namespace：`YLFramework.Utilities.Json`；程序集：`YLFramework.Runtime`。使用 `JSONObject.Create(...)` / `EmptyObject` 等直接入口，无需 Module、组件挂载或公共 Root。
- JSONObject 保留第三方来源与上游命名风格；上游说明见 [`readme.md`](../../Runtime/Utilities/Json/readme.md)。

## 快速使用

```csharp
using YLFramework.Utilities.Json;

var data = JSONObject.Create("{\"score\":10}");
if (data.TryGetInt("score", out var score))
{
    data.SetField("score", score + 1);
}
data.AddField("name", "player");
string json = data.Print(pretty: true);
```

`Create(string)` 解析 JSON 文本；创建普通字符串节点用 `CreateStringObject(value)`。`TryGetInt` 只表示字段存在，不验证其 JSON 类型；处理外部输入时应额外检查节点类型或改用严格校验方案。

## 核心数据模型

- `JSONObject` 一个节点在任一时刻只表现为一种 `JSONObject.Type`：`Null`、`String`、`Number`、`Object`、`Array`、`Bool`、`Baked`。
- `Object` 使用 `Keys` + `List` 按索引一一对应；`Array` 只使用 `List`；`Null` / `String` / `Number` / `Bool` / `Baked` 使用 `StringValue` / `LongValue` / `DoubleValue` / `BoolValue` 等标量字段。
- 默认参数下空输入或 `null` 输入返回 `Null`；非法 JSON 可能返回 `Null`、空容器或部分结果，不能将返回节点当成输入合法性的证明。
- 类型切换（`Add` / `AddField`）会清理无关的标量残留；`Clear()` 重置为 `Null` 且可复用。

## 解析与构建

- 解析：`JSONObject.Create(jsonString, offset, endOffset, maxDepth, storeExcessLevels)` 或 `new JSONObject(jsonString)`。
- 对非空输入且 `maxDepth != 0`，非法 `offset` 或超出字符串范围的 `endOffset` 抛 `ArgumentException`；宽松内容解析不等于所有参数错误都静默返回。
- 构建：`JSONObject.EmptyObject` / `EmptyArray` / `NullObject`；标量工厂 `Create(bool/float/double/int/long)`；另有接受构建委托、数组、List 与 Dictionary 的重载。
- 宽松解析规则（历史语义）：
  - 括号不匹配 / 未闭合不会抛低级运行时异常；`{`、`[`、`[[`、`{"a":{` 等以开括号结尾的输入返回对应空容器，其余情况返回部分结果或 `Null`。
  - 游离分隔符（`[,1]`、`{,"a":1}`、`[1,,2]`）被跳过，不生成幽灵 `null` 节点。
  - 未闭合对象收尾时会清理没有值的悬空 key，保证 `Keys.Count <= Count`。
  - 尾随垃圾按“首个完整值”处理：`{"a":1}abc`、`[1,2]xyz` 解析容器本身；`true xxx` 按首字符解析为 `true`；`1 garbage`、`-12tail` 无法构成数字则为 `Null`；`"hello"junk` 会连同垃圾一起作为字符串内容（历史行为）。
  - 非法数字（`-`、`1e`、`1e+`、`--1`、`1..2` 等）按 `Null` 处理并输出 warning；`1.`、`.1`、`+1`、`01` 等宽松写法仍被接受。
- `maxDepth` 与 `storeExcessLevels`：`maxDepth = 0` 时 `storeExcessLevels = false` 返回 `Null`、`true` 返回整段原文的 `Baked`；超过 `maxDepth` 的层级在 `false` 时替换为 `null`、`true` 时替换为截断位置的 `Baked` 原文（保留闭合 token），`Print` 后结构可还原。

## 字段与索引访问

- `AddField`：追加字段；同名 key 允许重复，查找 API 返回第一个匹配项。
- `SetField`：存在则替换（重复 key 只替换第一个），不存在则添加；传入 null 只删除。
- `RemoveField` / `HasField` / `HasFields` / `GetField`：常规增删查。
- 重复 key 契约（与上游一致）：解析与 `Print` 保留全部重复项；`HasField` / `GetField` / 索引器取第一个；`SetField` 移除第一个匹配再追加；`RemoveField` 删除第一个；`ToDictionary` 由最后一个覆盖；`Copy` 保留重复项。
- 易用 API：`TryGetField` / `TryGetString` / `TryGetInt` / `TryGetFloat` / `TryGetBool`。语义为“字段存在即成功”，值按节点自身字段读取（类型不匹配得到该类型默认值，如字符串节点 `TryGetInt` 返回 `0`）。
- 索引器：`obj[int]` 数组越界（含负数）返回 `null`，赋值越界静默忽略；`obj[string]` 缺 key 返回 `null`。
- 枚举：`foreach (JSONObject child in obj)` 对数组枚举元素，对对象枚举字段值（不含 key）；非容器枚举会抛 `InvalidOperationException`。

## 输出

- `Print(pretty = false)` / `ToString()` / `ToString(bool)` 输出 JSON 字符串；`Print(StringBuilder, pretty)` 直接写入 builder。
- 字符串与对象 key 使用同一套转义规则：`\\ \" \b \f \n \r \t`，其余控制字符输出为 `\uXXXX`，中文 / emoji 原样保留；解析同时支持 `\/` 与 `\uXXXX`。非法 escape（`\x`、`\u12XZ` 等）按原样保留，round-trip 稳定。
- 数字输出始终使用 `InvariantCulture`，小数点恒为 `.`；解析同样不受系统区域设置影响。
- 非标准数字（历史扩展）：`NaN`、`Infinity`、`-Infinity` 可被解析并原样输出（非严格 JSON）；整数溢出降级为 `Infinity`；`-0` 归一为 `0`，`-0.0` 首次输出 `-0`、再次解析后稳定为 `0`。
- 容器输出跳过 null 子节点与缺少对应 key 的条目，只有没有可写项时才输出空容器（`{}` / `[]`）；不会因为一个坏条目丢掉全部有效项。`Baked` 则原样输出片段，不做合法性校验。
- `Parse → Print → Parse` 对常规 JSON 保持结构等价。

## 生命周期

- `Clear()`：重置为 `Null`，清空全部状态并可复用。
- `Copy()`：深拷贝（Print + Parse 重建）；`Absorb()`：追加式浅合并（子节点引用共享）；`Merge()`：实验性合并，`Object` 递归、`Array` 同类型按索引覆盖、`Null` 直接 `Absorb`、类型不匹配不处理。
- `Merge` 的数组右侧若长于左侧，记录错误并拒绝该数组合并。递归合并也不是深拷贝，新增或替换的节点可能与来源共享引用。
- `Create(JSONObject[])` 复制数组容器但共享节点；`Create(List<JSONObject>)` 直接持有传入列表。不要把这些工厂当作隔离副本。
- `Bake()` / `BakeAsync()`：将序列化结果冻结到 `StringValue` 并转为 `Baked`；冻结后修改子节点不影响输出。对 `Baked` 节点调用 `AddField` 会转回 `Object`、清理 `StringValue` 并保留已有子节点（在其后追加）；需要结构化副本时使用 `Copy()`。

## 分步迭代

- `CreateAsync` / `PrintAsync` / `BakeAsync` 名字含 Async，但实现是 **IEnumerable / yield 分步迭代器（分帧）**，不是 `Task` / `await` / 多线程异步。
- `CreateAsync` 每次产出 `JSONObject.ParseResult`（`Result` / `Offset` / `Pause`），最后一次 `Pause` 为 `false`。大输入会真实产生中间 `Pause`（单帧预算约 8ms），可与同步解析得到相同结果。
- `PrintAsync` 序列化结束产出完整字符串，中途暂停产出 `null`；`PrintAsync(StringBuilder, pretty)` 暂停时产出 `true`。
- `PrintAsync(StringBuilder)` 为追加式写入，不清理 builder 已有内容；重复调用会再次追加（由调用方自行管理 builder）。
- 同步与分步版本结果一致（含 pretty 输出）。
- 中断语义：`CreateAsync` / `BakeAsync` 中途停止不会污染其他解析或后续调用（无全局可变解析状态）；`BakeAsync` 未完成时节点保持原状态，随后调用 `Bake()` 可正常完成。

## JSONTemplates

当前实际支持的 Unity 类型（`FromX` 写值、`ToX` 读值、`ToJson` 为 `FromX` 等价入口）：

| 类型 | 字段名 | 说明 |
| --- | --- | --- |
| `Vector2` | `x` `y` | `Vector2` |
| `Vector3` | `x` `y` `z` | `Vector3` |
| `Vector4` | `x` `y` `z` `w` | `Vector4` |
| `Quaternion` | `x` `y` `z` `w` | `Quaternion` |
| `Color` | `r` `g` `b` `a` | `Color` |
| `Matrix4x4` | `m00` … `m33` | `Matrix4x4` |
| `LayerMask` | `value` | 始终写出（含 0） |
| `Rect` | `x` `y` `height` `width` | `Rect` |
| `RectOffset` | `left` `right` `top` `bottom` | `RectOffset` |

- `FromX` 会省略值为 0 的分量（`LayerMask` 除外，始终写出 `value`），`ToX` 对缺失字段按 0 读取，round-trip 保持数值。
- `ToX` 对 `null` 节点、缺失字段或非对象节点返回默认值，不会抛空引用；单个字段类型错误不影响其他分量。
- 数值边界：`NaN` / `±Infinity` 分量与核心策略一致（`Print` 输出 `NaN` / `Infinity` 文本，可 round-trip）；`-0` 分量视为 0 并被省略；`LayerMask` 以 int 位掩码传输（支持 `int.MinValue` / `int.MaxValue`），不经过浮点；`RectOffset` 为 class，每次 `ToRectOffset` 返回独立实例。
- Transform 及局部/世界空间转换当前**未实现**，不在该模块职责内。

## 依赖

- 无框架内模块依赖；JSONObject 核心不依赖 Foundation、Core、Json.NET 或 Save。
- `JSONTemplates` 依赖 `UnityEngine` 数学类型。

## 备注

- 来源：Matt Schoen（Defective Studios）的 JSONObject 库；上游说明保留 `readme.md`，授权见 [框架许可证](../../LICENSE.md)。类型名保持上游风格（如全大写 `JSONObject`）。
- 主 UPM 包的 Runtime 包含 JSONObject 源码。上游 readme 中的 JSONChecker / 可选池化等说明不代表当前框架已提供对应编辑器或默认池化功能。
- 已知限制：非完整 JSON 序列化框架；宽松解析会容忍部分非法 JSON（括号不匹配、未闭合、尾随垃圾、非法 escape、`NaN` / `Infinity` 非标准输出等，具体策略见上文）。`Merge` 的递归修改与 `Absorb` 的追加均可能共享节点，需要独立副本时使用 `Copy()`；不做线程安全保证。
- `SerializedDictionaryJsonTests` 检查的是序列化字典，不是 JSONObject 的行为覆盖；历史算法验收见 [集成记录](../IntegrationPlan.md)，不作为当前测试保证。
