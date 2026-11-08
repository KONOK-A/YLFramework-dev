# JSON 与 Unity 值类型

> [文档首页](../README.md) · [安装](../Installation.md) · [可序列化字典](Serialization.md)

## 使用 Json.NET

JSON 树直接使用主包已经声明的 Newtonsoft.Json 依赖：对象用 `JObject`，数组用 `JArray`，任意节点用 `JToken`。不需要框架包装器、全局 JSON Manager 或 Core Module。强类型对象仍使用 `JsonConvert`；Save 和 HTTP 分别拥有存档及传输合同。

```csharp
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

var data = JObject.Parse("{\"score\":10,\"items\":[]}");
data["score"] = data.Value<int>("score") + 1;
data["name"] = "player";
((JArray)data["items"]).Add(true);
string json = data.ToString(Formatting.None);
```

业务程序集按实际配置引用 Newtonsoft.Json；若使用 `overrideReferences`，显式保留 `Newtonsoft.Json.dll` 引用。框架不重新导出这套第三方 API，也不升级项目的 Unity 包解析版本。

## 读取与修改

| 需求 | 直接 API 与语义 |
| --- | --- |
| 对象、数组、标量或 JSON null | `JToken.Parse(json)`；只接受对象时用 `JObject.Parse(json)` |
| 字段存在 | `JObject.ContainsKey(name)`；字段存在不代表类型正确 |
| 字段与数组项 | `node["name"]`、`array[index]`；按预期检查 `JToken.Type` 后读取 |
| 增改字段 | `obj[name] = value`；JSON null 使用 `JValue.CreateNull()` |
| 删除字段 | `obj.Remove(name)`；写入 JSON null 不等于删除 |
| 对象枚举 | `obj.Properties()` 同时提供属性名和值；数组直接枚举元素 |
| 独立副本 | `node.DeepClone()`，不与原树共享可变子节点 |
| 结构等价 | `JToken.DeepEquals(left, right)` |
| 输出 | `node.ToString(Formatting.None)` 或 `Formatting.Indented` |

默认重复属性采用最后一个值。不能接受重复字段的外部协议须明确指定策略，并保留异常诊断：

```csharp
var data = JObject.Parse(json, new JsonLoadSettings
{
    DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error
});
```

Json.NET 解析不是业务 schema 校验。字段集合、必填项、类型、数字范围、深度和大小限制由具体协议负责；不以宽泛捕获异常并返回空对象伪装成功。

## Unity 值类型

主包提供可选的 [`UnityValueJsonConverter`](../../Runtime/Utilities/Serialization/UnityValueJsonConverter.cs)，namespace 为 `YLFramework.Serialization`，程序集为 `YLFramework.Runtime`。它使用 Json.NET，只适配以下值类型及对应的 `Nullable<T>`；业务显式注册，不改变其他模块的 JSON 协议。

| 类型 | 完整字段，按此顺序写出 |
| --- | --- |
| `Vector2` | `x`、`y` |
| `Vector3` | `x`、`y`、`z` |
| `Vector4`、`Quaternion` | `x`、`y`、`z`、`w` |
| `Color` | `r`、`g`、`b`、`a` |
| `Rect` | `x`、`y`、`width`、`height` |
| `Vector2Int` | `x`、`y`，Int32 整数 |
| `Vector3Int` | `x`、`y`、`z`，Int32 整数 |
| `RectInt` | `x`、`y`、`width`、`height`，Int32 整数 |
| `Color32` | `r`、`g`、`b`、`a`，0–255 整数字节 |
| `Bounds` | `center`、`extents`，均为上述完整 `Vector3` 对象 |
| `BoundsInt` | `position`、`size`，均为上述完整 `Vector3Int` 对象 |

显式加入调用方自己的 settings；不修改 `JsonConvert.DefaultSettings`，不自动扫描或全局注册，也不要求 Root、Module 或服务管理器：

```csharp
using Newtonsoft.Json;
using UnityEngine;
using YLFramework.Serialization;

var settings = new JsonSerializerSettings();
settings.Converters.Add(new UnityValueJsonConverter());

string json = JsonConvert.SerializeObject(new Vector3(1.25f, 0, -2), settings);
// {"x":1.25,"y":0.0,"z":-2.0}
Vector3 value = JsonConvert.DeserializeObject<Vector3>(json, settings);
```

可向已有 settings 的 `Converters` 集合加入实例，保留其命名策略、其他 converter、排版和 reader 配置。外层 DTO 的字段命名、必填及整项 null 忽略策略仍由调用方负责；Json.NET 若在进入转换器前跳过了整个 DTO 属性，本转换器不会强行接管。本转换器实际处理的值对象，其分量名固定，不受 DTO 命名策略或 `MissingMemberHandling.Ignore` 放宽。

### 值与错误策略

- 每个分量都必须出现一次，大小写精确匹配；允许调整读取顺序。缺失、重复、未知字段，以及数组、字符串等非对象根均拒绝，不补零、不使用 existingValue 填补缺项。
- 浮点分量只能是 Json.NET 的 `Integer` 或 `Float` token；字符串冒充数字、bool、对象、数组及分量 `null` 均拒绝。非有限值、超出 `Single` 范围的值，以及非零数转换为 `float` 后下溢成零的值均拒绝；超出 Int64 的合法整数也按数值校验，不误当字符串。
- 整型分量走独立的整数路径，不经过 `float` 或 `double`。`Vector2Int/Vector3Int/RectInt` 要求 `Integer` token 且范围为 `Int32.MinValue` 到 `Int32.MaxValue`；`Color32` 要求 0–255 的 `Integer` token。`1.0`、`1e0`、小数和数值字符串均拒绝，不截断、舍入、钳制或回绕；`16777217` 等值保持精确。
- 读取非可空值类型时拒绝 JSON `null`；`Vector3?` 等可空目标保留 `null`。写入 CLR null 和外层 DTO 的 null 忽略规则由 Json.NET/调用方 settings 处理，不把 null 变成默认结构体。
- 所有分量都写出，包括零和透明度；不受 `DefaultValueHandling.Ignore` 省略。浮点数按 `float` 的实际值扩展为 `double` 后交给 Json.NET 写出，避免 Single 短格式在范围端点的表示误差；文本可能比手写小数更长，不承诺固定小数位数。整数直接按整数写出。
- 不归一化 Quaternion，不把全零改成 identity；不限制 Color 到 0–1、不做 gamma/linear 转换；不调整 Rect 的原点、符号或尺寸，不转换坐标空间。纯值转换不代替业务有效性检查。
- `Bounds` 只保存原始 `center + extents`，读取时直接设置 extents，不先乘二转换成 size，不同时保存 min/max/size。`BoundsInt` 只保存 `position + size`，不重算 min/max。两者保留零值与负尺寸；这不保证相应区域适合几何查询，也不代替业务对区域有效性的校验。
- 合同错误抛出带字段路径的 `JsonSerializationException`；JSON 语法错误由 Json.NET 抛出 `JsonReaderException` 等 `JsonException`，不捕获后返回默认值。调用方自己设置的错误处理回调仍由调用方负责。

校验基于 reader 已提供的数字 token，不重新解析原文或提供任意精度数值协议。普通舍入、Json.NET 已丢失的精度及调用方 reader 的范围限制不能由转换器恢复；例如 `FloatParseHandling.Decimal` 会先限制可读数字范围。若先使用默认 `JObject.Parse` 合并了重复属性，再交给转换器，也无法恢复已丢弃的重复项；需拒绝原文重复时应直接用配置后的 serializer 读取，或在原树解析时显式拒绝重复。

整型与区域类型使用示例（沿用同一份显式 settings）：

```csharp
string cell = JsonConvert.SerializeObject(new Vector2Int(16777217, -1), settings);
// {"x":16777217,"y":-1}
string color = JsonConvert.SerializeObject(new Color32(0, 128, 255, 0), settings);
// {"r":0,"g":128,"b":255,"a":0}
string region = JsonConvert.SerializeObject(
    new Bounds(new Vector3(1, 2, 3), Vector3.zero)
    {
        extents = new Vector3(4, 0, -1)
    }, settings);
// {"center":{"x":1.0,"y":2.0,"z":3.0},"extents":{"x":4.0,"y":0.0,"z":-1.0}}
string grid = JsonConvert.SerializeObject(
    new BoundsInt(new Vector3Int(16777217, 0, -1), new Vector3Int(2, 0, -3)), settings);
// {"position":{"x":16777217,"y":0,"z":-1},"size":{"x":2,"y":0,"z":-3}}
```

### 与 Save 组合

转换器属于主包，独立 Save 包只依赖 Json.NET。以下示例要求业务同时安装两个包并引用 `YLFramework.Runtime` 与 `YLFramework.Save`；存档接入、槽位及恢复流程见 [Save 包手册（源码仓库当前文档）](https://github.com/KONOK-A/YLFramework-dev/blob/main/PackageSources/com.kano.ylframework.save/Documentation~/README.md)。转换器通过公开 settings 组合，不改变存档索引或其他内部协议：

```csharp
using Newtonsoft.Json;
using UnityEngine;
using YLFramework.Save;
using YLFramework.Serialization;

public sealed class PlayerSnapshot
{
    public Vector3 Position;
    public Quaternion Rotation;
    public Color Tint;
}
```

业务方法内显式创建 Store，`rootPath` 由业务选择并拥有：

```csharp
var settings = new JsonSerializerSettings();
settings.Converters.Add(new UnityValueJsonConverter());

using (var store = new SaveStore(rootPath, settings))
{
    var slot = store.CreateSaveItem(7);
    store.SaveObject(new PlayerSnapshot
    {
        Position = new Vector3(1, 0, -2),
        Rotation = new Quaternion(1, 2, 3, 4),
        Tint = new Color(1, 0.5f, 0, 0)
    }, "player", slot.SaveId);

    store.ClearCache(); // 强制读盘，避免仅命中原对象缓存。
    var result = store.ReadObject<PlayerSnapshot>("player", slot.SaveId);
    if (result.Status == SaveReadStatus.Success)
    {
        PlayerSnapshot restored = result.Value;
    }
    // RecoveredFromBackup 等状态按业务恢复策略处理，不当作主件读取成功。
}
```

`SaveStore` 保留传入 settings 的原引用，不复制；在并发操作前完成配置，之后不要并发修改。转换器自身无可变实例状态，只操作值类型分量，不调用 Unity 对象或主线程 API，可用于 Save 的异步读取。默认 Store 可在使用前向 `SaveManager.DefaultSerializerSettings.Converters` 显式加入转换器；该 settings 是共享实例，不会被 Close/换根重置，`SaveManager.Store` 则是借用对象，不得 `using`/Dispose。需要隔离时优先使用上面的显式 Store。

Save 按自己的策略处理转换失败和备份恢复。增加转换器不会主动改写任何存量文件；显式选用后，该业务 DTO 的读写格式采用上面的严格完整字段。接入现有业务数据前核对实际 JSON，不满足字段合同的数据由调用方明确转换或版本策略，不靠转换器宽松兜底。

### 范围与裁剪

仅覆盖表中十二种值类型，不覆盖 `LayerMask`、`Matrix4x4`、`RectOffset`、`Ray/Ray2D`、`AnimationCurve`、`Gradient` 或 Unity 对象引用。不提供分帧解析、原文缓存、部分结果或全局自动注册；Core、Resource、Workbook 等内部 JSON 协议不因此切换 converter。

通过静态 `new UnityValueJsonConverter()` 注册，不使用泛型 converter 工厂或反射扫描。IL2CPP 业务 DTO 的反射成员仍由消费者定向保留；不以全程序集 Preserve 代替真实裁剪验证。本转换器没有额外 linker 配置，实际消费者通过静态注册保留其调用。

通用字典的 Unity 原始行与 Json.NET 有效投影见 [Serialization](Serialization.md#json-边界)。Json.NET 的树、serializer 和 reader 选项查阅 [官方文档](https://www.newtonsoft.com/json/help/html/Introduction.htm)，业务仍需对自己的 schema、数据规模和实际裁剪后消费者进行验证。
