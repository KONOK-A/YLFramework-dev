# Localization 使用手册

Localization 通过显式登记的 `MultiLanguageConfigs` 提供文本和图片条目。配置定义、语言偏好、显示订阅和图片租约是不同责任；登记配置不会自动创建 UI 或启动其他业务系统。

## 前置与作者流程

安装主包和 `com.kano.ylframework.localization`，由声明依赖解析 UGUI。业务 asmdef 引用 `YLFramework.Localization`，namespace 为 `YLFramework.Localization`。Editor 工具位于 `YLFramework.Localization.Editor` 程序集、`YLFramework.Editor.Localization` namespace。

1. 在 `Assets/` 下通过 Create 菜单 `YLFramework/Localization/Multi Language Configs` 创建根。
2. 打开 `YLFramework/Localization/多语言配置`，明确选择根，创建语言资产并为同一业务内容填写各语言的相同 ID。
3. 按条目类型填写文本、Resource 图片地址或网络图片 URL，然后显式保存根与语言资产。

工具创建的语言位于 `Assets/YLFrameworkData/Localization/<根GUID>/`，支持 Undo。移除语言引用不删除语言资产；保存只处理目标根和语言，不保存无关脏场景或安装包。

## 最小用法

把以下脚本放在使用工程中，将已保存的根赋给 `root`，并为 ID 1 创建文本条目。

```csharp
using UnityEngine;
using YLFramework.Localization;

public sealed class LocalizationExample : MonoBehaviour
{
    [SerializeField] private MultiLanguageConfigs root;
    private LocalizationConfigHandle handle;

    private void OnEnable()
    {
        handle = LocalizationManager.AddConfig(root);
        LocalizationManager.Language = SystemLanguage.ChineseSimplified;
        Debug.Log(LocalizationManager.GetString(1));
    }

    private void OnDisable()
    {
        handle?.Dispose();
        handle = null;
    }
}
```

`AddConfig` 借用已加载根，由外部 owner 保活。多个 owner 可以登记同一根，各自 `Dispose()` 只撤销自己的登记；普通消费者不要用 `RemoveConfig` 代替句柄释放，因为它会撤销该根的全部有效登记。

## 语言选择与回退

`RequestedLanguage` 表示用户请求的语言，首次读取有效偏好值，否则使用 `Application.systemLanguage`。`Language` getter 则返回当前配置中可用的候选语言，没有候选时为 `Unknown`。设置 `Language` 会保存请求并刷新显示；设置 `Unknown` 删除偏好，重新采用系统语言。

偏好键为 `YLFramework.Localization.Language`，保存 `SystemLanguage` 的整数值。条目查询从请求语言开始，逐项回退：

1. 请求的语言。
2. 若请求简体中文，依次尝试 `Chinese`、`ChineseTraditional`；繁体中文则尝试 `Chinese`、`ChineseSimplified`；`Chinese` 则尝试简体、繁体。
3. `English`。
4. 按根登记顺序和根内语言顺序追加尚未尝试的语言。

候选去重且跳过 `Unknown`。某语言存在但缺少当前 ID 时继续查下一候选，不把全局 `Language` 当成每个条目的唯一来源。

同一根不能重复语言；同一语言的 ID 在全部已登记根之间必须唯一。`LocalizationManager.Validate(roots)` 校验这些约束以及空引用和语言枚举，但不验证图片是否真实可加载或文本格式是否正确。

`GetLanguageInfo(id, language)` 可指定本次起点而不改变偏好。文本 `str == null` 表示不可用，空字符串仍有效；图片条目要求定位字段非空，但资源下载失败不会自动触发另一语言的图片重试。

## 常用任务

### 文本、参数与追加片段

`GetString(id, args)` 在有参数时按 `CultureInfo.CurrentCulture` 执行格式化。缺项或结果不是文本时记录警告并返回空串；格式不合法则抛出带语言、ID 和配置上下文的 `FormatException`，不把格式错误伪装成缺失。

在目标物体添加 `LocalizeString`，设置 `Id` 和目标方式：

- `Text`：写入同物体的 UGUI `Text.text`。
- `TextMesh`：写入同物体的 `TextMesh.text`。
- `Other`：按组件名查找同物体组件，写入指定的 public、可写、非索引 `string` 属性或非只读字段。可用于 TMP 的 `text` 成员，但本包没有 TMP 编译依赖，也不创建任意组件。

`SetArgs` 复制替换参数；`Append(id)` 添加本地化片段，`Append(string)` 添加字面片段，`ClearAppend()` 清空追加。可用 `richText` 中的 `{0}` 包装片段。直接修改 `Appends` 后须调用 `Refresh()`；组件不加载或替换字体。

绑定组件启用时注册 `Changed` 并立即刷新，停用时精确退订。刷新直接写显示，不切换业务 GameObject 的活动状态；停用时不写 UI。组件刷新入口会记录显示异常，直接调用字符串组合 API 则须按其异常合同处理。

### 配置资源与异步加载

使用 `LocalizationManager.LoadConfigAsync(resources, address)` 前，应用先初始化 `ResourceSystem`，在资源类型清单登记 `typeof(MultiLanguageConfigs)` 及实际直接加载的具体类型。返回的 `LocalizationConfigHandle` 持有本消费者的在途请求、成功根租约与登记。

`IsLoading` 只表示请求尚未完成，不是成功标志；完成后检查 `Error` 和 `Config`，不能仅以 `Error == null` 判断成功。句柄不是协程等待对象，协程可在持有期间检查 `IsLoading`。停用、销毁、取消或不再需要结果时都要 `Dispose()`，防止迟到结果重新发布。

加载或登记失败后修复输入并创建新的句柄；配置在完整联合校验后才登记，不留下半成功根。应用须为 IL2CPP 保留自己的反射类型与成员，类型清单不替代裁剪配置。

### 图片绑定

`LocalizeImage` 要求同物体的主包 `ResourceImage`，把资源地址或 URL 交给后者。`projectName/imageName/subAsset` 分别是 YL Resource 的 ProjectId / Path / SubAsset，不是其他资源系统的地址。

按 Resource 的接入方式给 `ResourceImage` 提供可用资源服务和目标图片组件。图片请求、租约、代次 / 取消检查和原始显示恢复由 `ResourceImage` 负责；本地化根句柄不拥有这些图片租约。缺项、非图片条目或组件停用会清空本方图片责任，迟到结果不能在停用后重新发布。

## 字段协议

| 资产 | 当前字段 |
| --- | --- |
| 多语言根 | `displayName/languages`，语言列表公开读取为 `Languages` |
| 单语言配置 | `language/displayName/languagesInfo` |
| 语言条目 | `id/type/str/image/url/notes` |
| 图片定位 | `projectName/imageName/subAsset` |

`GetConfig(name)` 按资产对象名查询，不按 `displayName`。根和语言列表是共享定义，运行时消费者不把业务状态写回这些资产。

## 所有权、错误与限制

- API 只在 Unity 主线程使用，不提供后台加载器。配置 owner、`Changed.Register(...)` 返回的订阅、图片 owner 分别由各自登记者归还。
- 自定义显示若直接订阅 `Changed`，保存返回的 `IUnregister` 并在退出时 `Unregister()`；释放配置句柄不能代替解除业务订阅。
- `Shutdown()` 关闭整个本地化责任域、释放其 owner 并清理刷新事件，不关闭外部 `ResourceSystem`。应用先停止本域 UI / 业务；关闭回调期间拒绝新登记，释放失败会在尝试其他 owner 后汇总抛出。
- `Other` 的反射成员必须由应用在 IL2CPP 中保留。普通字符串绑定不表示字体、排版、复数规则或远端翻译服务已由本包处理。
- 本包没有 Excel 作者流程，不因安装 Excel 就支持本地化工作簿导入。
- Bundle 与 Catalog 须按真实类型全名和程序集身份由正式 Resource 工具配套构建，不手改目录 JSON 或摘要。配置资产和语言偏好属于使用工程，移除包不会自动删除它们。

## 相关文档

- [包入口](../README.md)
- [Resource 使用说明](https://github.com/KONOK-A/YLFramework-dev/blob/main/PackageSources/com.kano.ylframework/Documentation~/Modules/Resource.md)
- [公共安装指南](https://github.com/KONOK-A/YLFramework-dev/blob/main/PackageSources/com.kano.ylframework/Documentation~/Installation.md)与[框架快速开始](https://github.com/KONOK-A/YLFramework-dev/blob/main/PackageSources/com.kano.ylframework/Documentation~/QuickStart.md)

跨包链接指向源码仓库当前文档。
