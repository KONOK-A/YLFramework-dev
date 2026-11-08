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

先由应用初始化 `ResourceSystem`，挂载目录时通过 `ResourceProjectMount(..., assetTypes: compiledTypes)` 或 `ResourceLocalSnapshot.CreateCatalog` 提供显式编译类型清单。登记实际加载的根类型与直接加载的具体条目类型，不能用开放泛型替代具体类型。

`ScriptableObjectManager.Load(resources, address)` 返回 `ScriptableObjectRegistration`，其内部持有根 `AssetLease`；登记校验失败会归还租约。`LoadAsync(resources, address)` 返回 `ScriptableObjectAsset<ScriptableObjectConfigs>`，可在协程中等待：

```csharp
var owner = ScriptableObjectManager.LoadAsync(resources, address);
try
{
    yield return owner;
    if (owner.Status != YLFramework.Resource.ResourceRequestStatus.Succeeded)
    {
        Debug.LogException(owner.Error ?? new System.OperationCanceledException());
        yield break;
    }

    var loadedRoot = owner.Asset;
    // 只在 owner 有效期间借读 loadedRoot 及其条目。
}
finally
{
    owner.Dispose();
}
```

片段中的 `resources/address` 由应用提供。等待完成不会释放句柄；若需持续使用配置，把 owner 存入业务所有者并在停用、销毁或取消时显式释放，不依赖协程自然结束。`IsDone` 只表示请求已终结，`IsValid` 才表示当前仍持有有效租约；失败原因见 `Error`。

### 单例配置与作者操作

`SingletonScriptableObjectConfigs<T>` 供以自身具体类型封闭的 SO 子类使用。`LoadAsync` 显式加载，`Instance` 仅借读有效结果，不会自动创建或加载。已有有效或在途加载时再次加载会拒绝；`Release()` 或 owner `Dispose()` 结束责任。

作者窗口支持条目复制、改名、排序和诊断。字段编辑、改名、排序等操作保留相应 Undo；创建新条目不注册创建 Undo。删除会核对根引用、`parent` 和实际资产位置，只删除本根拥有的条目；外部条目只解除引用。独立主资产和根的物理删除不可 Undo，须明确确认，根内子资产删除使用 Undo；删除根按顺序处理，不提供整批失败回滚。保存是显式操作，不把 getter 当成扫描或写盘入口。

需要工作簿时另装 Excel 包，使用它的 DataAssets Workbook 面板。工作簿路径按根 GUID 保存在本机 `EditorPrefs`，不进入 Runtime 根；本包不引用 MiniExcel 或 Excel Editor 后端。

## 字段与地址

| 数据 | 当前字段与含义 |
| --- | --- |
| 配置根 | `displayName/configs/typeScriptGuid`；显示名、条目列表、作者类型脚本 GUID |
| 配置条目 | `parent/id/configName/icon/description`；所属根、根内 ID、名称、图标定位和说明 |
| 图标定位 | `projectName/iconName`；YL Resource 的 ProjectId / Path，`TryGetAddress` 只构造地址 |
| 工作簿表头 | `configName` 显式对应 `config_name`，`description` 对应 `des`，`typeScriptGuid` 对应 `type_script_guid` |

工作簿列名是独立格式协议，按 `ExcelColumnName` 声明解释，不从 namespace 或程序集名推断。配置资产和生成数据属于使用工程，包的更新或移除不会替应用删除它们。

## 所有权、错误与限制

- 同一根可由多个 owner 登记，每份返回值只释放自己的登记和租约。普通消费者用自身句柄 `Dispose()`；`RemoveConfig` 会撤销匹配根的全部登记，只用于明确的域级管理。
- `Clear()` 关闭 DataAssets 的所有登记与加载，包括单例配置，不关闭应用共享的 `ResourceSystem`。先停止使用配置的 UI / 业务，再结束配置 owner。
- 在 Unity 主线程使用这些 API。根和条目都是 Unity 对象，不提供后台访问或复制隔离。
- 注册失败、重复 ID、名称歧义或失效登记会明确报错。异步加载成功取得资源后仍可能因登记校验失败而 `Failed`；此时修复输入再重新创建 owner，不把完成误当成功。
- Resource Catalog 使用真实类型全名与程序集身份，Bundle 与 Catalog 应由同一套当前配置正式构建和发布，不能手改 Catalog JSON 或摘要。
- 自定义派生类型、反射成员及其 IL2CPP 保留由应用负责；仅登记根类型不等于保留全部业务子类。

## 相关文档

- [包入口](../README.md)
- [Excel 作者手册](https://github.com/KONOK-A/YLFramework-dev/blob/main/PackageSources/com.kano.ylframework.excel/Documentation~/README.md)
- [Resource 使用说明](https://github.com/KONOK-A/YLFramework-dev/blob/main/PackageSources/com.kano.ylframework/Documentation~/Modules/Resource.md)
- [公共安装指南](https://github.com/KONOK-A/YLFramework-dev/blob/main/PackageSources/com.kano.ylframework/Documentation~/Installation.md)与[框架快速开始](https://github.com/KONOK-A/YLFramework-dev/blob/main/PackageSources/com.kano.ylframework/Documentation~/QuickStart.md)

跨包链接指向源码仓库当前文档。
