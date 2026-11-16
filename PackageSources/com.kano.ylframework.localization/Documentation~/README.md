# Localization 使用手册

官方 Localization 唯一拥有 Locale、翻译正文、表集合、Smart Strings、fallback 和 Addressables 内容。YL 只负责消费者作用域、业务变量和本组内容就绪，不再维护 YL 多语言根或第二份翻译数据库。

## 安装与作者工作流

- 主体程序集：`YLFramework.Localization`，namespace `YLFramework.Localization`。
- TMP：独立 `YLFramework.Localization.TMP`；`LocalizationTMPSupport.IsAvailable` 如实反映实际 SDK。Unity 6 使用 UGUI 内置 TMP，2022 需安装对应 TMP 包。
- 官方包当前声明 1.5.14。实际传递版本以 UPM lock 为准，不能手写解析结果。
- 用 `Edit/Project Settings/Localization` 配置官方设置、Locale、启动选择器和回退；用官方 Localization Tables 维护正文、CSV/XLIFF/Sheets、属性变体和伪本地化。
- 本地化表、Locale、字体和 Sprite 走官方 Addressables。业务 DataAssets 根仍可走 YL Resource，但本地化内容不通过第二套 Provider 重复加载。

类型化组件 `LocalizeText`、`LocalizeTMPText`、`LocalizeSprite` 的引用字段使用官方 drawer。文本组件可选绑定官方 Font/TMP Font；不指定就保留目标原字体。独立使用时组件自管作用域，停用时精确退订并恢复仍由本方持有的显示值。

## 会话与数据

```csharp
using UnityEngine.Localization;
using YLFramework.Binding;
using YLFramework.Localization;

var count = new BindableProperty<int>(5);
var session = new LocalizationSession();
session.BindString(reference, text => label.text = text,
    binding => binding.BindVariable("count", count));
count.Value++;
// 由创建者在退出时调用；不关闭官方全局系统。
session.Dispose();
```

示例 `reference` 是包含 `{count:N0}` 的官方 Smart String。初始变量先配置，再订阅/格式化；数值类型直接交给官方文化格式和复数规则，不提前 ToString。不轮询业务数据，使用 `IReadOnlyBindableProperty<T>.Register` / `IUnregister`。

共享引用只作为模板：表项、fallback、Arguments 和既有变量语义保留；本轮新增业务变量在私有引用中。模板及 VariablesGroupAsset 不会被写入。已有官方变量是只读借入输入，不复制成第二份全局变量库。

`BindVariable` 不覆盖模板或仍有效的本方同名变量。单独释放返回句柄会退订并冻结最后值，已释放的本方槽位可显式重新绑定。配置失败回滚本次监听；消费者交付错误通过 `Error` 暴露，不打断其他 owner。

## 面板级协调

给面板加 `LocalizationGroup`，Content 必须是专属**子** CanvasGroup，不是 Panel 根组。内容保持 active；loading/error 节点放在门外。Panel 关闭 AutoStart，在 `OnOpen` 同步配置：

```csharp
protected override void OnOpen(LoadViewArgs args)
{
    group.BeginSession(this, _ =>
        counter.StringBinding.BindVariable("count", model.Count));
}
```

示例在正常 Panel 中使用 `YLFramework.Core` 与所持有的 model。只在钩子内借读 args，复制需要的数据；不要把参数容器捕获到 configure 委托。普通 View 在 OnLoaded 调用；普通 GameObject 可 AutoStart，或显式 BeginSession。

- State 为 Inactive/Loading/Ready/Failed；Error 暴露失败，Retry 仅重试本轮。
- 首次和切语言等待本组必要文本、图片、字体；先应用资产，再文本、合并布局，最后揭示内容。
- 普通 Ready 状态的数值变化只改相关文本/布局，不反复整组 loading。
- BeginSession 每次建立新轮次；Single 重开不依赖 internal View.Session。ViewRemoved 自动结束，Hide/Show 不结束，临时停用退订并在恢复时读取当前数据。
- 关闭、销毁、移出或被替换的成员不能让旧组继续等候或回写；嵌套组按最近归属独立协调。
- 不在 OnOpen 调用 Hide/Show，不重启业务 GameObject，不接管 Panel 根 CanvasGroup、焦点或全局输入。
- 独立官方 localizer 可以继续直接使用，但不自动成为 YL 屏障参与者。同一 text/font/sprite 属性不能同时由两套 localizer 写入。

## 配置表引用

安装可选 DataAssets/Excel 后，业务行直接保存官方引用：

```csharp
[LocalizedExcelReference] public LocalizedString title;
[LocalizedExcelReference(AllowEmpty = true)] public LocalizedSprite picture;
```

选中根的菜单为 `Assets/YLFramework/Localization/Validate Data References`、`Preview Reference Workbook`、`Export Reference Workbook`。官方字段选择器照常使用；右键官方引用可复制稳定 Excel 文本或定位官方表。

Json 列支持两种严格互斥的输入：

```json
{"table":"Items","entry":"name.sword"}
```

```json
{"tableGuid":"123456781234123412341234567890ab","entryId":"9007199254740993"}
```

tableGuid 是官方**集合** GUID，不是某个语言表的 AssetDatabase GUID。entryId 为 Int64 十进制**文本**，不接受 Excel 浮点数。业务行 id 不等于翻译 Key ID。

导入先解析暂存行、规范化为 GUID/Key ID，再走既有预览事务；取消零写入，提交前复核表项身份与资产类型。允许用工作簿修复旧坏引用，但不放松业务根/行所有权。重命名保留身份；删后同名重建不会误连。默认必需引用不允许空，可选字段显式允许空单元格/JSON null。只改引用身份，保留 fallback 和局部变量。

导出用克隆行规范化，不改源配置或保存无关脏内容。翻译正文仍由官方工具维护。首轮只支持直接类型化字段，不引入嵌套配置 ORM、Excel 正文双向同步或 Save 管线。

## 错误与资源所有权

所有操作在 Unity 主线程。默认异步，YL 不调用 WaitForCompletion；使用方显式开启官方同步设置不受此承诺抵消。加载失败不能只等待 StringChanged/AssetChanged，绑定检查实际句柄状态并进入可观察失败。

作用域只退订自己的回调和归还自己持有的引用。关闭面板不 ReleaseAllTables、ResetState 或 Shutdown 官方全局系统；官方缓存不要求归零。禁 Domain Reload 的 Play 收尾仅清理 YL 活跃会话，不接管用户 Editor 或场景。

缺译遵循官方 fallback/缺译策略。官方把空单元格和格式化后的空值均视为缺译；若设置为只记录 warning、不显示缺译正文，得到的空输出仍可就绪，不会让本组永远 loading。

## 旧内核迁移

本版本直接淘汰旧 Manager/根/整数 ID/Append/Other 反射/Resource 图片协议，没有双轨开关或空壳。仓库内只有旧测试夹具，没有需要转换的真实作者资产；外部工程须先保全自己的数据。

- 旧根/语言/条目改为官方集合、Locale 和稳定引用；正文交官方表。
- 旧中文/English 固定候选链改为官方 Locale fallback 配置，不自动沿袭。
- 旧 int PlayerPrefs 语言偏好不自动读入新系统，使用官方启动选择器/偏好流程。
- Append/字符串拼接改为 Smart String 模板/变量；TextMesh/Other 需要消费方迁移，不再要求 TMP text/font 反射保留。
- Resource 地址/URL 不是官方 Asset Table 资产 GUID，不能直接改几个字段伪装迁移；需明确迁移实际资产或由业务自管网络内容。

## 样例

Package Manager 的 `Basic UI` 提供完整官方资产/场景生成器、运行控制器和 Core Panel 模板；`DataAssets Excel` 提供业务行及完整引用工作流。生成器拒绝覆盖已有目录，拒绝自动保存未命名场景及已有 Addressables 脏修改。字体引用只借用用户明确选择、许可适用的项目资产。

Player 发布必须实际构建官方 Addressables 内容。Editor 快速模式、No Unity 工具门禁、Mono 和 IL2CPP 是不同证据，不能互相冒充。
