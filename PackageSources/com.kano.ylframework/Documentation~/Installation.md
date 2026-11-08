# 安装与接入

> [文档首页](README.md) · [快速开始](QuickStart.md) · [架构](Architecture.md)

框架尚未正式发布，当前以源码和本地 UPM 包接入为主。使用自己的 Unity 工程，不复制开发宿主的设置和插件。

## 环境与依赖

- 包声明的 Unity 基线为 `2022.3.62f3`，开发宿主为 `6000.3.27f1`；源码按 C# 9 维护。声明基线不等于所有补丁、平台与后端均已验证。
- 获取源码需要 Git 与 Git LFS。BouncyCastle、Analyzer 和 Excel 后端等 DLL 必须是真实二进制，不能是 LFS 指针。
- 主包声明 UGUI、Json.NET 及所用内置 Unity 模块，Registry 依赖由 UPM 解析。不要拷贝同名替身 DLL；不同 Editor 的实际解析版本可能不同。
- DOTween 和 HybridCLR 是显式选择的集成，按对应手册准备 SDK。普通主包接入不要求 MCP、Odin、MiniExcel 或 HybridCLR。

Resource 内容加密、远端 Player 与 HybridCLR native 有更窄的平台/引擎前置，使用前阅读[资源平台限制](Modules/Resource/Delivery.md#平台与后端限制)及所属集成手册，不能由 Editor 导入成功推导支持范围。

## 获取源码

在准备放置源码的父目录执行：

```powershell
git clone https://github.com/KONOK-A/YLFramework-dev.git YLFrameworkDev
git -C YLFrameworkDev lfs pull
```

如果已克隆仓库，先确认 LFS 下载完成。产品包位于 `YLFrameworkDev/PackageSources/`，不需要为了使用框架而打开完整开发宿主。

## 本地 UPM 接入

### 最小主包

1. 打开自己的 Unity 工程，在 `Window > Package Manager` 中选择 `Add package from disk...`（Unity 版本不同，可能显示为 `Install package from disk...`）。
2. 选择源码中的 `PackageSources/com.kano.ylframework/package.json`，等待依赖解析和 Unity 编译。
3. 若业务使用 asmdef，显式引用 `YLFramework.Runtime`；仅 Editor 扩展引用 `YLFramework.Editor`。
4. 按[快速开始](QuickStart.md)运行示例，不需要先摆放 Root 或创建 Module。

也可编辑使用工程的 `Packages/manifest.json`。例如将源码与游戏工程并排放置：

```text
Workspace/
  YLFrameworkDev/PackageSources/...
  MyGame/Packages/manifest.json
```

在现有 `dependencies` 中合并所需条目，不覆盖其他依赖：

```json
{
  "dependencies": {
    "com.kano.ylframework": "file:../../YLFrameworkDev/PackageSources/com.kano.ylframework"
  }
}
```

`file:` 相对路径以使用工程的 `Packages/` 为起点，不以工程根为起点。Windows 也推荐使用 `/` 分隔路径。参考 [Unity 本地路径说明](https://docs.unity3d.com/2022.3/Documentation/Manual/upm-localpath.html)。

本地包直接连接源码目录，修改会反映到所有连接该目录的工程。需要隔离时使用独立源码副本，不编辑共享副本来做破坏性试验。

## 选择能力包

当前自有包尚未发布到 Registry。**本地或 Git 接入时，必须为所选的全部自有依赖显式提供来源**，不能只加一个叶子包后等待 Registry 下载兄弟包。先添加依赖包，或在同一次 manifest 编辑中一起声明它们。

| 选择 | 必须提供来源的自有包 | 主要 Runtime 程序集 |
| --- | --- | --- |
| 主包 | `com.kano.ylframework` | `YLFramework.Runtime` |
| DataAssets | 主包、`.data-assets` | `YLFramework.DataAssets` |
| Localization | 主包、`.localization` | `YLFramework.Localization` |
| Save | 仅 `.save`，不需要主包；Json.NET 由 UPM 解析 | `YLFramework.Save` |
| StateMachine | 主包、`.state-machine` | `YLFramework.StateMachine` |
| Movement2D | 主包、`.movement2d` | `YLFramework.Character2D`、`YLFramework.Platform2D` |
| Overlap | 主包、`.overlap` | `YLFramework.Overlap` |
| Gameplay | 主包、`.gameplay` | `YLFramework.Skill`、`YLFramework.Buff`、`YLFramework.Mission`、`YLFramework.Equipment` |
| RedDots | 主包、`.reddot` | `YLFramework.RedDots` |
| Excel | 主包、`.data-assets`、`.excel` | 仅 Editor：`YLFramework.Excel.Editor` |
| HybridCLR | 主包、`.hybridclr`，另提供官方 SDK `9.0.3` 来源 | `YLFramework.HybridCLR` |

表中 `.xxx` 均是 `com.kano.ylframework.xxx` 的简写。完整用法见[选装手册导航](README.md#选装能力)。

例如 Excel 的本地依赖条目为：

```json
{
  "dependencies": {
    "com.kano.ylframework": "file:../../YLFrameworkDev/PackageSources/com.kano.ylframework",
    "com.kano.ylframework.data-assets": "file:../../YLFrameworkDev/PackageSources/com.kano.ylframework.data-assets",
    "com.kano.ylframework.excel": "file:../../YLFrameworkDev/PackageSources/com.kano.ylframework.excel"
  }
}
```

以下组合需要额外显式选择：

- Excel + Gameplay：同装两者启用 Excel 包内 `YLFramework.Domain.Excel.Editor`，无需全局宏。
- RedDots + Save：同装两者启用红点包内 `YLFramework.RedDots.Save`；RedDots 核心不强装 Save。
- DOTween：适配源码在主包中，外置 Standard SDK 和 `DOTWEEN` 都成立后才启用 `YLFramework.Actions.DOTween`，业务 asmdef 需显式引用它。
- HybridCLR：准备官方 SDK `9.0.3` 与匹配 native 后端；SDK、native 与基座的要求见源码仓库当前的 [HybridCLR 手册](https://github.com/KONOK-A/YLFramework-dev/blob/main/PackageSources/com.kano.ylframework.hybridclr/Documentation~/README.md)。主包不自带 SDK。

## Git 接入

适合需要固定远端源码修订而不直接编辑包的工程。Unity 只支持在工程 manifest 中声明 Git 来源，不会通过包内 SemVer 声明自动查找兄弟 Git 包，见 [Unity Git dependencies](https://docs.unity3d.com/2022.3/Documentation/Manual/upm-git.html)。

从源码父目录读取一个**已经存在于远端**的完整提交，生成所需包 URL。例如 Excel 闭包：

```powershell
$revision = (git -C ./YLFrameworkDev rev-parse --verify HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $revision -notmatch '^[0-9a-f]{40}$') {
    throw '需要有效的完整提交 SHA。'
}
$packageIds = @('com.kano.ylframework', 'com.kano.ylframework.data-assets', 'com.kano.ylframework.excel')
foreach ($id in $packageIds) {
    '"{0}": "https://github.com/KONOK-A/YLFramework-dev.git?path=/PackageSources/{0}#{1}"' -f $id, $revision
}
```

将输出作为各条目的值合并到使用工程 `dependencies`，条目之间补 JSON 逗号。各自有包固定同一修订，`?path=` 在 `#` 之前。不要使用短 SHA，Git URL 也无法取得尚未提交或未推送的本地修改。生成 URL 仅确认修订和格式，不代表该提交已完成你的工程与目标平台验证。

HybridCLR 的官方 SDK 还需独立来源。当前 native 构建门禁要求精确 Git URL `https://gitee.com/focus-creative-games/hybridclr_unity.git#v9.0.3` 及其锁定提交，不能任意改为另一 locator，即使版本字符串相同；细节以该包 `Dependencies.json` 与手册为准。Registry 分发属于维护者的后续发行流程，不是当前安装主线。

## 样例与项目数据

- 在 Package Manager 的 Samples 中选择性导入 `Resource Startup`；HybridCLR 样例在其所属包。隐藏的 `Samples~` 在显式导入前不参与使用工程编译。
- Canvas、EventSystem、ScreenTransition 与 UI Prefab 是正式运行资产，不需要靠导入样例才能获得。
- 安装包是输入，配置、工作簿和生成代码由项目拥有。Module 默认输出 `Assets/YLFrameworkData/Generated/Modules`，SO 创建助手默认输出 `Assets/YLFrameworkData/ScriptableObjects`，见 [Editor](Modules/Editor.md#设置与生成)。
- 移除包不负责清理项目数据、SDK、资源发布目录或冻结基座；先处理自己的业务引用和运行责任，再移除不需要的依赖。

## 常见问题

| 现象 | 检查 |
| --- | --- |
| 找不到自有依赖包 | 是否为闭包中的每个自有包提供本地/Git 来源，而不是让 UPM 查未发布的版本 |
| DLL 无法导入或内容像文本 | 检查 Git LFS 是否下载完成，不复制缓存 DLL 代替源码声明 |
| 找不到类型或命名空间 | 查看真实 Unity 编译错误，确认业务 asmdef 引用了所属 Runtime；Editor 类型只在 Editor 程序集使用 |
| 出现重复脚本/程序集 | 是否同时导入了多份源码、Sample 副本或 SDK；只移除已确认的重复输入，保留项目自有修改 |
| 找不到 Markdown | `Documentation~` 不进入 AssetDatabase，需从包实际磁盘路径打开 |
| 放入 Root 后系统仍未运行 | Root 不自动启动资源、命名音频 Provider 或业务 Module；按所选模块显式装配 |
