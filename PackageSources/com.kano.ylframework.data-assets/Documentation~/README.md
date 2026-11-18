# DataAssets 使用手册

DataAssets 用于组织共享 ScriptableObject 定义：根保存有序条目引用，条目以业务 ID 标识，管理器只登记明确提供的根，不扫描资产或自动加载。运行实例应把可变状态保存在自己的对象中。

## 前置与配置

安装主包与 `com.kano.ylframework.data-assets`，业务 asmdef 引用 `YLFramework.DataAssets`；运行时 namespace 为 `YLFramework.DataAssets`。Editor 作者 API 位于 `YLFramework.DataAssets.Editor` 程序集、`YLFramework.Editor.DataAssets` namespace，只能由 Editor 代码引用。

1. 通过 Project 的 Create 菜单 `YLFramework/Data Assets/Configuration Root` 创建 `ScriptableObjectConfigs`，保存在使用工程的 `Assets/` 下。
2. 打开 `Window/YLFramework/Data Assets`，明确选择要编辑的根；可绑定一个 `ScriptableObjectData` 派生脚本作为条目基类。
3. 创建条目并设置根内唯一 `id`、`configName` 及业务字段，显式保存。新条目位于 `Assets/YLFrameworkData/DataAssets/<根GUID>/`，不写入安装包。

自定义条目继承 `ScriptableObjectData`；`typeScriptGuid` 为空时使用默认基类，非空时必须解析为可用且无开放泛型参数的条目类型。切换绑定不能排除根中已有条目。

## 最小用法

把以下脚本放入使用工程，将已保存的配置根赋给 `root`。`AddConfig` 借用外部已经加载的资产，不负责销毁它。

```csharp
using UnityEngine;
using YLFramework.DataAssets;

public sealed class DataAssetsExample : MonoBehaviour
{
    [SerializeField] private ScriptableObjectConfigs root;
    [SerializeField] private int entryId = 1;
    private ScriptableObjectRegistration registration;

    private void OnEnable()
    {
        registration = ScriptableObjectManager.AddConfig(root);
        var entry = registration.Config.GetScriptableObjectData(entryId);
        Debug.Log(entry ? entry.configName : "未找到配置");
    }

    private void OnDisable()
    {
        registration?.Dispose();
        registration = null;
    }
}
```

这里的条目是借读引用。业务对象停止使用条目后才能释放登记；直接引用根的外部 owner 仍须保证根和条目在使用期间存活。

## 常用任务

### 查询根与条目

- `ScriptableObjectManager.GetConfig(name)` 使用登记时的资产对象名 `root.name`，不是 `displayName`。也可先用 `StringToHash(name)` 取得哈希；不同根的名称或哈希冲突会在登记时拒绝。
- `GetScriptableObjectData(id)`、`GetScriptableObjectData<T>(id)` 和名称重载借读条目；缺失或泛型类型不符返回 null。名称按 Ordinal 精确匹配，重名歧义抛错，不按列表顺序任选一项。
- `TryGetIdRange` 区分空根与包含 ID 0 的根；`MinId/MaxId` 在空根中均为 0。索引忽略空槽，但拒绝重复 ID。
- `GenerateId()` 只计算当前未占用的正 ID，不创建条目、不预留 ID。`GetIcon(id)` 只返回定位信息，不加载图片。
- `ClearCache()` 丢弃查询索引，不清空条目或释放资产。共享定义不应在运行时被业务逻辑改写。

### 通过 Resource 加载

先由应用启动并持有 `ResourceStartup`，业务借用其 `Resources`。配置根配置为官方 Addressables 条目，例如 `game/config/items`；`new ResourceAddress("game", "config/items")` 只是将项目命名空间与路径投影为同一个官方地址，不挂载私有 Catalog，也不登记另一套资产类型清单。

| 入口 | 返回值与责任 |
| --- | --- |
| `AddConfig(root)` | `ScriptableObjectRegistration`；借用外部根，不取得其资源所有权 |
| `Load(resources, address)` | `ScriptableObjectRegistration`；只增持 YL 已持有的根，登记持有独立 `AssetLease` |
| `LoadAsync(resources, address)` | `ScriptableObjectAsset<ScriptableObjectConfigs>`；拥有本消费者请求、成功根租约及关联登记 |

冷资源使用 `LoadAsync`。同步 `Load` 未命中已加载租约时抛 `ResourceException`，`Kind == ResourceError.InProgress`；不会发起加载、阻塞等待或返回 null。只让外部 Addressables 持有资产，不等于它已经进入 YL 的热资源集合。

下面是 Unity 协程内的一次读取，文件引用 `YLFramework.DataAssets` 与 `UnityEngine`，假定根内已创建 ID 1。等待成功且 owner 仍有效后才借读；方法退出时归还本次 owner：

```csharp
using var owner = ScriptableObjectManager.LoadAsync(resources,
    new YLFramework.Resource.ResourceAddress("game", "config/items"));
yield return owner;
if (owner.Status != YLFramework.Resource.ResourceRequestStatus.Succeeded || !owner.IsValid)
{
    Debug.LogException(owner.Error ?? new System.OperationCanceledException());
    yield break;
}

var entry = owner.Asset.GetScriptableObjectData(1);
Debug.Log(entry ? entry.configName : "未找到配置");
// 此处借读已结束；using 退出后不能继续保留 entry 使用。
```

片段中的 `resources` 由应用提供，`Debug` 来自 `UnityEngine`。等待完成本身不会释放句柄；若 UI 或业务要长期借读，不使用上述局部 `using`，而把 owner 存入业务所有者，在停用、销毁或取消时显式 `Dispose()`。`IsDone` 只表示请求已终结，`IsValid` 才表示当前仍持有有效租约；失败原因见 `Error`。

确需同步登记时，先在主线程 async 方法中预加载，并在调用 `Load` 时保留预加载租约：

```csharp
using var warm = await resources.AcquireAsync<ScriptableObjectConfigs>("game/config/items");
using var registration = ScriptableObjectManager.Load(resources,
    new YLFramework.Resource.ResourceAddress("game", "config/items"));
var root = registration.Config; // registration 独立保活，不借用 warm 的释放责任。
```

### 单例配置与作者操作

`SingletonScriptableObjectConfigs<T>` 供以自身具体类型封闭的 SO 子类使用。`LoadAsync` 显式加载，`Instance` 仅借读有效结果，不会自动创建或加载。已有有效或在途加载时再次加载会拒绝；`Release()` 或 owner `Dispose()` 结束责任。

作者窗口支持条目复制、改名、排序和诊断。字段编辑、改名、排序等操作保留相应 Undo；创建新条目不注册创建 Undo。删除会核对根引用、`parent` 和实际资产位置，只删除本根拥有的条目；外部条目只解除引用。独立主资产和根的物理删除不可 Undo，须明确确认，根内子资产删除使用 Undo；删除根按顺序处理，不提供整批失败回滚。保存是显式操作，不把 getter 当成扫描或写盘入口。

需要工作簿时另装 Excel 包，使用它的 DataAssets Workbook 面板。工作簿路径按根 GUID 保存在本机 `EditorPrefs`，不进入 Runtime 根；本包不引用 MiniExcel 或 Excel Editor 后端。

## 字段与地址

| 数据 | 当前字段与含义 |
| --- | --- |
| 配置根 | `displayName/configs/typeScriptGuid`；显示名、条目列表、作者类型脚本 GUID |
| 配置条目 | `parent/id/configName/icon/description`；所属根、根内 ID、名称、图标定位和说明 |
| 图标定位 | `projectName/iconName`；投影为官方 `projectName/iconName` 地址，`TryGetAddress` 只构造地址、不加载 |
| 工作簿表头 | `configName` 显式对应 `config_name`，`description` 对应 `des`，`typeScriptGuid` 对应 `type_script_guid` |

工作簿列名是独立格式协议，按 `ExcelColumnName` 声明解释，不从 namespace 或程序集名推断。配置资产和生成数据属于使用工程，包的更新或移除不会替应用删除它们。

## 所有权、错误与限制

- 同一根可由多个 owner 登记，每份返回值只释放自己的登记和租约。普通消费者用自身句柄 `Dispose()`；`RemoveConfig` 会撤销匹配根的全部登记，只用于明确的域级管理。
- `Clear()` 关闭 DataAssets 的所有登记与加载，包括单例配置，不关闭应用共享的 `ResourceSystem`。普通 owner 退出只释放自己；只有应用明确结束整个 DataAssets 域时才调用 `Clear()`。
- 关闭顺序是先停止配置的 UI / 业务借读，再释放登记或加载 owner、调用所持单例的 `Release()`，最后才由应用等待 `ResourceStartup.StopAsync()`。仍有独立租约时 Resource 可返回 `Busy`，应找到并归还责任后重试，不强制卸载。
- Resource Scope 只管理其接纳的资源租约，不是业务 registry。关闭 Scope 不等于 `ScriptableObjectManager.Clear()`，也不能把 `ScriptableObjectRegistration` 或 `ScriptableObjectAsset<T>` 当成可交给 `Scope.Own` 的资源租约；业务 owner 仍负责注销。
- 在 Unity 主线程使用这些 API。根和条目都是 Unity 对象，不提供后台访问或复制隔离。
- 注册失败、重复 ID、名称歧义或失效登记会明确报错。异步加载成功取得资源后仍可能因登记校验失败而 `Failed`，并归还本次根租约；这不撤销其他 owner 的成功登记。加载未完成时 Dispose 只撤销本消费者等待，不取消其他消费者，也不让迟到结果重新登记。
- 配置根要求精确的主资产地址，不支持附加 SubAsset 选择。官方地址未找到、类型不符或资源会话不可用时检查 `Error`；同步热路径的 `InProgress` 不能当成“配置不存在”。
- 资源通过官方 Addressables 内容流程与 Player 一起构建、发布和验证，不手工维护私有 Catalog 或摘要，也不能把一次 Editor 读取当成 Player / AOT 验证。
- 自定义派生类型、反射成员及其 IL2CPP 保留由应用负责；仅登记根类型不等于保留全部业务子类。

## 相关文档

- [包入口](../README.md)
- [Excel 作者手册](https://github.com/KONOK-A/YLFramework-dev/blob/main/PackageSources/com.kano.ylframework.excel/Documentation~/README.md)
- [Resource 使用说明](https://github.com/KONOK-A/YLFramework-dev/blob/main/PackageSources/com.kano.ylframework/Documentation~/Modules/Resource.md)
- [公共安装指南](https://github.com/KONOK-A/YLFramework-dev/blob/main/PackageSources/com.kano.ylframework/Documentation~/Installation.md)与[框架快速开始](https://github.com/KONOK-A/YLFramework-dev/blob/main/PackageSources/com.kano.ylframework/Documentation~/QuickStart.md)

跨包链接指向源码仓库当前文档。
