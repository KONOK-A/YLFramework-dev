# 可序列化字典（Serialization）

> [文档首页](../README.md) · [安装](../Installation.md) · [JSON 与 Unity 值类型](Json.md)

`YLFramework.Serialization.SerializedDictionary<TKey,TValue>` 同时提供标准字典接口与 Unity 字段序列化，适合需要在 Inspector 编辑、运行时按键查询的配置。它是主包 Foundation 能力，不要求安装 Save 或初始化 Root、Module。

运行时源码为 [`SerializedDictionary.cs`](../../Runtime/Foundation/Serialization/SerializedDictionary.cs)，属于 `YLFramework.Runtime`；原生 Inspector 为 [`SerializedDictionaryDrawer.cs`](../../Editor/Foundation/Serialization/SerializedDictionaryDrawer.cs)，属于 `YLFramework.Editor`。

## 最小用法

业务 asmdef 引用 `YLFramework.Runtime`，组件字段使用 `[SerializeField]`。可直接声明泛型字段，也可定义带 `[Serializable]` 的具体非泛型子类。

```csharp
using UnityEngine;
using YLFramework.Serialization;

public sealed class ScoreConfig : MonoBehaviour
{
    [SerializeField]
    private SerializedDictionary<string, int> scores =
        new SerializedDictionary<string, int>();

    public void SetScore(string id, int value) => scores[id] = value;

    public bool TryReadScore(string id, out int value) =>
        scores.TryGetValue(id, out value);
}
```

挂载组件后可在 Inspector 编辑条目。运行时直接调用字典 API，不需要字典组件、手动 Refresh 或序列化前回写。

## 字典 API

| 入口 | 语义 |
| --- | --- |
| `dictionary[key]` | 缺键读取抛 `KeyNotFoundException`；写入更新首个有效行或追加新行 |
| `Add(key, value)` / `TryAdd(key, value)` | 重复键分别抛 `ArgumentException` / 返回 false，不覆盖已有值 |
| `TryGetValue` / `ContainsKey` / `ContainsValue` | 只查询有效字典投影；值为 default 不等于缺键 |
| `Remove(key)` | 删除该键的全部原始行，防止隐藏重复行重新生效 |
| `Clear()` | 清空全部原始行，包括无效行 |
| `Count` / `InvalidEntryCount` | 有效唯一键数量 / 被忽略的原始行数量 |
| `Keys` / `Values` / 枚举 | 只读实时视图，按首个有效原始行的顺序，不是副本 |
| `new SerializedDictionary<TKey,TValue>(source)` | 复制条目，不借用外部集合；null 来源、null 键或重复键抛错 |

- 实现 `IDictionary<TKey,TValue>`、`IReadOnlyDictionary<TKey,TValue>` 与 `ISerializationCallbackReceiver`，键和值均使用默认相等比较器，不提供自定义 comparer 参数。
- 所有接收键的 API 拒绝 CLR null，允许空字符串、值类型 default 键以及 default/null 值。Unity 已销毁对象的托管引用不会被当作 CLR null 自动删除。
- 枚举期间写入字典或发生反序列化，继续迭代会抛 `InvalidOperationException`；需要独立结果时自行复制。字典不负责释放值对象，也不提供线程安全。

## 原始行与有效投影

Unity 保存 `datas` 列表，每行使用 `Key` / `Value` 字段；列表是事实源，运行时写入直接修改对应原始行。

- null 行、CLR null 键与重复键保留在原始数据中，但不参与查询、视图、枚举及 Count；同键只有首个有效行生效。
- Inspector 修复 Key 后，该行可重新有效；重排可能改变同键的首个有效值。查询不会自动删除问题行。
- `InvalidEntryCount` 按运行时可判断的原始数据统计，不等同于 Unity Inspector 对所有对象引用的诊断结论。
- `OnBeforeSerialize` 不回写列表；`OnAfterDeserialize` 只使索引与已有枚举器失效，下次查询惰性重建。两处回调不访问 Unity 对象，不应在其中追加业务刷新逻辑。

## Inspector 操作

- 原生 Drawer 展示条目、有效数量与重复行号，单对象支持增删、重排、字段编辑、Undo/Redo 和 Prefab override/apply/revert。
- 多对象按目标分别诊断，保留 mixed-value 字段编辑；禁止整组列表增删和重排。列表长度不同或字段缺失时，整组字段编辑也被禁用，应单独选择目标修复。
- 多对象在相同位置修改 Value 不会将首个目标的 Key 复制到其他目标。
- 同时提供 UI Toolkit `PropertyField`/binding 与 IMGUI 实现，无需第三方 Inspector；第三方接管 Editor 后是否调用原生 Drawer，取决于该 Editor。
- Key/Value 必须满足 Unity 字段序列化规则。Drawer 不提供任意复杂泛型键、嵌套容器、多态、alias 或固定缓冲的自定义 serializer，Unity inline 序列化也不保证任意 null 行保真。
- 无法通过 `SerializedProperty.boxedValue` 可靠诊断的键会明确提示。Unity 对象键的 None/Missing 无法可靠区分 CLR null 与 fake-null，Inspector 标为待确认，不用 `ToString` 猜测重复或虚报精确有效数。

## JSON 边界

Json.NET 按标准字典合同输出裸对象，只保存有效投影，不输出 Unity 的 `datas/Key/Value` 原始行或无效条目：

```csharp
using Newtonsoft.Json;
using YLFramework.Serialization;

var scores = new SerializedDictionary<string, int>();
scores.Add("a", 2);
scores["b"] = 4;
string json = JsonConvert.SerializeObject(scores); // {"a":2,"b":4}
var restored = JsonConvert.DeserializeObject<SerializedDictionary<string, int>>(json);
```

- string/int/enum 键及相应值可按 Json.NET 支持的合同往返；复杂键的 `ToString()` 不代表可逆编码，跨会话建议使用稳定 ID。
- Unity 对象引用不适合直接作为 JSON 键。自定义 converter/resolver、`$type/$id/$ref` 等元数据名称和引用保留配置，必须用实际数据核验。
- 需要 Unity 值类型的固定字段表示时显式配置 [UnityValueJsonConverter](Json.md#unity-值类型)，不会因使用字典而自动注册。
- 可选组合 [Save 包手册（源码仓库当前文档）](https://github.com/KONOK-A/YLFramework-dev/blob/main/PackageSources/com.kano.ylframework.save/Documentation~/README.md)。Save 不依赖主包，使用此字典需由业务同时引用两个包。
- Save 缓存可能直接返回之前保存的内存实例；内存读取不是磁盘往返验证。验证数据格式时使用业务明确拥有的隔离根、清缓存和冷读，不在真实用户目录做破坏性试验。

Unity 原始行格式与 JSON 字典投影是不同数据表示，不能用 JSON 往返来保留 Inspector 的重复或无效行。字段类型、对象身份及业务 schema 变化需由数据 owner 以真实数据制定转换策略。
