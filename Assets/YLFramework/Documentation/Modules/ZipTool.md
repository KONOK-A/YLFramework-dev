# 压缩与归档（ZipTool）

> [文档首页](../README.md) · [架构总览](../Architecture.md)

## 作用

提供目录打包、zip 解压、单条目读取与 GZip 压缩 / 解压，包装层源码与 Unity 解耦，底层采用随附 SharpZipLib 源码。

## 位置

- 运行时：[`Runtime/Utilities/ZipTool/`](../../Runtime/Utilities/ZipTool/)
- 对外封装：[`Utilities/`](../../Runtime/Utilities/ZipTool/Utilities/)；内置库：[`ICSharpCode.SharpZipLib/`](../../Runtime/Utilities/ZipTool/ICSharpCode.SharpZipLib/)
- 自有包装 namespace：`YLFramework.Compression`，消费者使用 `using YLFramework.Compression;`；内置 SharpZipLib 子树仍保留现有 `YLFramework.Utilities.ZipTool.*` 身份。两者归 `YLFramework.Runtime` 主体，不新增 DLL 或旧 namespace 转发壳；工具源码保持 BCL-only，不等于整个主体 DLL 无引擎依赖。主体不依赖已退休宿主扩展。
- 常规业务直接使用 `ZipUtility` / `GZipUtility`，多次读取同一归档使用 `using` 管理 `ZipArchiveReader`；无需 Manager、组件挂载或公共 Root。

## 用法

```csharp
using YLFramework.Compression;

// 业务方法中调用；路径由调用方选择，不覆盖已有目标。
int written = ZipUtility.UnzipToDirectory(
    "downloads/content.zip", "cache/content",
    UnzipOptions.ForNetworkDownloads(), overwrite: false);

using var reader = new ZipArchiveReader("downloads/content.zip");
if (reader.TryReadEntryBytes("config.json", out var bytes))
{
    // 使用 bytes；内存由调用方持有。
}
```

`ForNetworkDownloads()` 只是总量 4GB、单条目 1GB、10 万条目的预设，需按业务预算调整；无 options 的整包解压默认不限制大小。

## 主要功能

- `ZipUtility`：`ZipFolder`（可按过滤规则打包；输出位于源目录内或已存在时经临时文件打包后替换）、`UnzipToDirectory`（覆盖 / 进度回调 / 编码参数 / 配额）、`ExtractEntry`（提取单个条目）。
- `ZipArchiveReader`：会话式读取 zip（一次打开多次读取，`IDisposable`），解压前预扫描配额与 Zip Slip 路径。
- `UnzipOptions`：解压配额（总大小 / 单条目大小 / 条目数量，`ForNetworkDownloads` 预设）；0 = 不限制。
- `GZipUtility`：字节数组 / 流的 GZip 压缩与解压（永不关闭调用方流，支持 0 字节数据）。
- 内置 `ICSharpCode.SharpZipLib`：保留既有 `YLFramework.Utilities.ZipTool.*` namespace、SharpZipLib Contributors 来源声明与完整 MIT，不随自有 Compression 包装重命名。

## 主要类型

- `ZipUtility`：zip 打包 / 解压入口。
- `ZipArchiveReader`：会话式 zip 读取器。
- `UnzipOptions`：解压限制选项。
- `GZipUtility`：GZip 工具。
- `CodePageScope`（internal）：zip 条目名编码切换辅助。

## 关键语义

- **Zip Slip**：整包解压在任何写入前，对全部条目做 `Path.GetFullPath` 规范化与目录前缀校验；越界抛 `InvalidDataException`，不创建目标目录。绝对路径 / 盘符条目会先被内置 `ZipEntry.CleanName` 归一为相对路径。此校验不解析目标目录中的符号链接 / junction，也不防御外部并发改路径；应使用调用方控制的目标目录。`ExtractEntry` 的目标文件由调用方直接指定，条目名不参与拼接。
- **CRC 校验**：普通解压不校验 CRC（内置库 `GetInputStream` 行为）；需要完整性校验时使用 `ZipUtility.VerifyArchive`（`testData=true` 完整校验 CRC，损坏 deflate 数据返回 false）。
- **配额**：`UnzipOptions` 三项限制 0 = 不限制，负值抛 `ArgumentOutOfRangeException`；整包解压先按元数据预扫描，再按实际写出字节复核。超限抛 `InvalidDataException` 并停止；已完成的文件不回滚。`ReadEntryBytes`、单条目提取、条目流及 GZip 不使用这些配额，不能把预设当作所有读取入口的防护。
- **文件替换**：解压先写同目录临时文件，数据复制成功后才替换目标；复制阶段失败不截断旧文件，并在 `finally` 清理临时文件。替换实际是“删除旧文件 + `File.Move`”，**不是原子操作**；改名失败可能已经失去旧文件，清理本身的 IO 失败也会传播。需要事务或崩溃恢复时由上层管理独立输出与提交。
- **编码 / 线程安全**：`nameCodePage` 通过 `CodePageScope` 临时修改进程级静态 `ZipConstants.DefaultCodePage`；便捷方法结束（含异常）后恢复，reader 则持续到 `Dispose`。嵌套必须按 LIFO 释放；**非线程安全**，不保证并发调用。
- **进度回调**：值为已处理字节 / 元数据总字节，范围 0..1；正常完成时最后一次为 1，空 zip 回调一次 1。异常中断不保证完成回调，回调自身异常向上传播。
- **输出 zip 自打包**：`ZipFolder` 的输出位于源目录内部或目标已存在时，先打包到系统临时文件，避免将正在写出的 zip 打包进自身；打包失败保留旧输出。最后使用 `File.Copy(..., overwrite: true)`，不是原子替换，复制失败仍可能破坏旧输出；源目录内已有的旧 zip 也可能作为普通文件被收录，需要时用过滤器排除。

## 流与所有权

- `ZipArchiveReader(string)` 拥有内部文件流，`Dispose` 关闭归档并恢复编码；释放后继续读取抛 `ObjectDisposedException`。
- `ZipArchiveReader(Stream, leaveOpen)` 要求可寻址流；正常关闭时按 `leaveOpen` 决定是否关闭输入流。构造失败时底层清理可能关闭传入流，即使 `leaveOpen: true` 也不保证保留。
- `reader.OpenEntryStream` 返回的流由调用方先释放，再释放 reader；reader 关闭后条目流失效。`ZipUtility.OpenEntryReadStream` 则先完整读入内存，返回独立 MemoryStream，大条目优先使用 reader 流式读取。
- `GZipUtility` 永不关闭调用方输入 / 输出流，但失败时输出可能已有部分数据。截断 / 损坏输入向上传播异常；完整 GZip 块之后无法解析的尾随数据可能被忽略，不是严格的整段格式校验器。

## 依赖

- Zip 源码无框架内模块或 UnityEngine / UnityEditor 依赖；`ZipToolSourceTests` 使用 BCL 引用独立编译全源码，检查这一边界。它不代表整个 Runtime DLL 无 Unity 依赖，也不是独立裁剪交付的保证。

## 分发

默认导出递归包含 `Runtime/`，因此 ZipTool 封装、内置库及 `.meta` 一并归 Runtime 主体分发；来源声明与 [框架许可证](../../LICENSE.md) 一同保留。不需要额外安装 SharpZipLib Package 或供应商 DLL，不可只导出封装而漏掉内置实现。完整分发与宿主依赖见 [文档首页](../README.md)。

## 测试入口

`Tests/EditMode/Utilities/ZipTool/ZipToolSourceTests.cs` 检查源码编译边界；`Tests/EditMode/Editor/Distribution/ExportUnityPackageTests.cs` 使用内置 GZip / Tar 检查导出边界。两者不等同于 Zip / GZip 全量算法、Android APK 读取或所有平台验证，也不表示当前环境已执行通过。历史验收范围见 [集成记录](../IntegrationPlan.md)。
