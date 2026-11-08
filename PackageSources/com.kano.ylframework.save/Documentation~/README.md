# Save 使用手册

Save 提供本地多槽存档、按显式文件名保存的对象、实现 `ISaveModel` 的主模型，以及不属于槽位的全局设置。`SaveStore` 是唯一业务 API；它独占一个根目录，管理文件、对象缓存和并发，不是云存档或跨文件事务系统。

## 前置与依赖

只安装 `com.kano.ylframework.save` 即可，由 UPM 解析声明的 `com.unity.nuget.newtonsoft-json`。本包不依赖主包，也不携带另一份 Newtonsoft DLL。

业务 asmdef 引用 `YLFramework.Save`，代码使用 `YLFramework.Save` namespace。若消费程序集启用了 `overrideReferences`，调用签名暴露 Json.NET 设置的 API 时还须显式加入 `Newtonsoft.Json.dll`，即使设置参数使用默认值。

对象和模型应为可序列化的普通 DTO。包内 `link.xml` 保留 `SaveIndex` 和 `SaveItem` 元数据；应用自己的 DTO、反射成员和 Converter 的 AOT / IL2CPP 保留仍由应用负责。

## 最小用法

以下脚本不需要主包。挂到场景物体后，第一次运行创建槽 0 并写入分数，后续运行读取相同文件。它会访问应用真实的默认存档目录；自动化或编辑器工具应另用明确的隔离根。

```csharp
using System.Collections.Generic;
using UnityEngine;
using YLFramework.Save;

public sealed class SaveExample : MonoBehaviour
{
    private void Start()
    {
        var store = SaveManager.Store; // 借用默认 owner，不使用 using 或 Dispose。
        if (!store.HasSaveItem(0))
        {
            store.CreateSaveItem(0);
            store.RenameSaveItem(0, "进度一");
        }

        var result = store.ReadObject<Dictionary<string, int>>("progress", 0);
        if (result.Status == SaveReadStatus.Missing)
        {
            store.SaveObject(new Dictionary<string, int> { ["score"] = 100 }, "progress", 0);
            Debug.Log("已创建分数存档");
            return;
        }

        Debug.Log($"分数={result.Value["score"]}，来源={result.Status}，缓存={result.IsCached}");
    }
}
```

默认槽参数 `0` 不代表自动创建槽。需要槽位的同步 / 异步读写对不存在槽抛错；已登记槽中主件和备份均缺失时，`LoadObject<T>` 返回 null，`ReadObject<T>` 返回 `Missing`。不能将权限、IO、JSON 或版本错误当成缺失继续覆盖。

## 常用任务

### 槽与元数据

`CreateSaveItem(id?)` 创建空槽，不创建主模型，也不自动设为当前槽。`GetAllSaveItems()` 返回新列表容器，条目仍是借用的原实例，列表不保证排序；调用方用 `SaveId`、时间或自己的比较器排序。

`HasSaveItem/GetSaveItem` 查询索引，`DeleteSaveItem/DeleteAllSaveItems` 显式删除槽；全局设置清理另用 `DeleteAllSetting()`。`CurrentSaveId` 可为 null，设置非空时必须是已登记槽。

`SaveItem` 暴露 `SaveId/CreateTime/LastSaveTime/DisplayName/Note`。用 `RenameSaveItem` 和 `SetSaveItemNote` 立即持久化，失败时回滚内存。直接修改借出的条目只改内存，冷重开前若无索引写入则丢弃；后续任何索引写入会快照当前元数据，可能把直接改动一起保存。已打开 owner 不合并外部索引编辑。

### 对象、缓存与全局设置

对象使用 `SaveObject(value, name, id)`、`ReadObject<T>(name, id)` 或 `LoadObject<T>(name, id)`；文件名由调用方明确指定，不从类型名推断。`HasObject` 只查已登记槽的主文件是否存在，不验证备份或内容；`GetObjectNames` 按 Ordinal 排序列出主 JSON 文件基名。未知槽的这些查询返回 false / 空列表，`DeleteObject` 返回 false，不隐式创建槽。

缓存按槽和文件名保存对象原实例，保存后立即读取可能命中同一对象，不是磁盘往返验证。同名缓存类型与请求类型不符时抛错。`ClearCache()` 不改盘，也不取消其他在途读取；隔离验证应先结束在途操作，再清缓存或释放并重开 owner。调用方负责缓存对象本身的并发修改。

全局设置使用 `SaveGlobalSetting`、`ReadGlobalSetting<T>`、`TryLoadGlobalSetting<T>`、`LoadGlobalSetting<T>`。只有 `LoadGlobalSetting<T>` 在主备均缺失时创建并保存 `new T()`；其他读取不创建默认值。`TryLoadGlobalSetting` 不吞损坏、权限或 IO 错误。设置不使用对象槽或对象缓存。

### 主模型

模型实现 `ISaveModel` 的可读写 `int Id`：

```csharp
public sealed class ProgressModel : YLFramework.Save.ISaveModel
{
    public int Id { get; set; }
    public int Level = 1;
}
```

`CreateModel<ProgressModel>(id)` 创建新槽并写入固定文件 `ArchiveModel.json`，不能覆盖已有槽。`Id` 必须保留引擎赋予的非负槽号，否则抛 `ArgumentException`。`SaveModel(model)` 按 `model.Id` 保存；`LoadModel<T>()` 省略槽号时使用 `CurrentSaveId`，未选择时抛错，读取后还会验证模型身份。

对应 Async 方法使用相同槽、索引和对象缓存。`LoadAllModelsAsync<T>` 按槽号排序，跳过没有主模型的普通对象槽；失败不返回部分列表。

## 读取与显式恢复

`SaveReadResult<T>` 分别报告文件来源与缓存命中：

| `Status` | 含义与处理 |
| --- | --- |
| `Success` | 使用主文件或保留主文件来源的缓存 |
| `RecoveredFromBackup` | 使用有效备份值，主文件尚未修复；来源标记随缓存保留 |
| `Missing` | 主件与备份均不存在，可由业务决定是否新建 |

主文件缺失或读取失败会尝试备份；仍无有效结果则抛错，内部 `Failed` 不作为公开成功结果返回。有效备份可供读，不表示可以直接继续写入覆盖坏主件。

1. 读取时记录状态或异常，保全实际文件；不要把坏档当成空档，也不要自动清目录重试。
2. 索引从备份打开时 `IndexNeedsRecovery == true`。显式调用 `RestoreIndexBackup()`，成功后才进行受保护的索引 / 槽位写入。健康索引不能用该入口任意回滚。
3. 对对象或设置调用 `RestoreObjectBackup(name, id)` / `RestoreSettingBackup(name)`，用有效备份恢复主件。对象恢复会清掉对应缓存，索引恢复清空对象缓存。
4. 恢复保留 `.bak`，已有主件另存为 `.corrupt-*`，返回原件保留路径；原主件不存在时返回 null。核对恢复后的实际读取结果，再按业务继续写入。

主件损坏且没有有效备份、索引版本不符，或根内已有存档却没有索引时会拒绝打开 / 操作，不自动修造空索引。版本不支持抛 `SaveVersionException`，需要先恢复的写入抛 `SaveRecoveryRequiredException`。根锁冲突时先结束原 owner，而非绕过锁并发打开。

## 存储格式与文件边界

- `SaveStore.FormatVersion == 2`；索引为 `<root>/SaveData/SaveSystemData.json`，槽对象为 `<root>/SaveData/<槽ID>/<文件名>.json`，全局设置为 `<root>/Setting/<文件名>.json`。
- 索引使用固定 `DataVersion/SaveItemDict/CurrentSaveID` JSON 键；槽元数据使用 `SaveID/CreateTime/LastSaveTime/DisplayName/Note`。C# 属性 `SaveId/CurrentSaveId` 不改变磁盘键。
- 对象和设置使用裸 JSON，索引的固定序列化配置不受用户 Converter 影响。单文件写入先 flush 临时文件，再原子替换，并保留上一份 `.bak`。
- 文件名不含扩展名，不支持子目录；拒绝路径分隔符、`..`、非法字符、末尾点 / 空格、Windows 保留设备名及超过 200 字符的名称，非法时抛 `ArgumentException`。
- 根必须是明确的非文件系统根目录，路径检查拒绝穿越已有链接。同根使用排他 `.yl-save.lock`；不要让另一个 Store、进程或默认 owner 同时使用该根。
- 删除使用 `.deleted-*` 软删除，不是安全擦除，也不自动清理。跨索引 / 目录操作不是全根多文件掉电事务；故障时保留的 staging / tomb 和原件是恢复证据，不应直接清除。

## 所有权、异步与限制

自建 `new SaveStore(rootPath)` 时由创建者 `Dispose()`，并在另一 owner 打开同根前完成释放。`SaveManager.Store` 只借用默认 owner，不能由业务消费者释放；默认 owner 由应用级 `SaveManager.Close()`、换根与 Unity 会话退出管理。

默认根是 Unity 主线程捕获的 `Application.persistentDataPath`。编辑器读取 `StorageRoot` 不创建 Store，首次读取 `Store` 才懒建并可能访问真实目录。工作线程使用前先在主线程捕获默认路径，或设置显式根；`SetStorageRoot(path)` 关闭、取消并排空原 owner，只换路径，不复制数据。

`DefaultSerializerSettings` 是默认 Store 共用的配置原实例，并发前配置；关闭 / 换根不重置它。`DefaultSerializationFormat` 只在默认 Store 创建时应用，当前排版用 `Store.SerializationFormat`，再次读取 `Store` 不覆盖实例配置。独立 Store 同样须在并发前配置其 `SerializerSettings`。

- 同槽串行、跨槽可并发。保存先在调用线程序列化 JSON 快照，再进行异步 IO；同步反序列化在调用线程，异步读取及 Converter 在线程池，不得调用 Unity API。
- Converter 必须能并发使用，不能重入同 owner 的内部操作或释放路径。禁止 `TypeNameHandling` 从 JSON 实例化 CLR 类型。
- Async 方法可接收 `CancellationToken`，取消在单文件原子提交点前有效；提交后报告成功，不把已落盘结果改报取消。取消不代表已撤销其他文件或业务副作用。
- `Dispose/Close/SetStorageRoot` 拒绝原 owner 的新操作、取消排队并等待活动操作退出；这是阻塞式排空，不能从本 owner 的 Converter 中重入。
- 本包没有内建加密、认证、密钥来源、云同步或自动格式转换，备份和路径检查不构成这些保证；不承诺解决所有文件系统或超长路径问题。平台文件系统、持久目录和 AOT 行为须由实际应用验证。

## 可选组合与相关文档

本包自带 `SerializedVector2/SerializedVector3/SerializedColor` 及转换扩展，用于保存 Unity 数值。普通 DTO 和标准字典即可独立使用 Save。

需要 Unity Inspector 中的通用字典时，可另装主包使用 `YLFramework.Serialization.SerializedDictionary<TKey,TValue>`；其运行时、Inspector 和 JSON 投影边界完整说明归[主包 Serialization](https://github.com/KONOK-A/YLFramework-dev/blob/main/PackageSources/com.kano.ylframework/Documentation~/Modules/Serialization.md)，不是 Save 的安装依赖。

- [包入口](../README.md)
- [公共安装指南](https://github.com/KONOK-A/YLFramework-dev/blob/main/PackageSources/com.kano.ylframework/Documentation~/Installation.md)
- [框架快速开始](https://github.com/KONOK-A/YLFramework-dev/blob/main/PackageSources/com.kano.ylframework/Documentation~/QuickStart.md)

跨包链接指向源码仓库当前文档。
