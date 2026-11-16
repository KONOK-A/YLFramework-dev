# DataAssets / Excel 引用

本扩展示例需要额外安装 `com.kano.ylframework.data-assets`、`com.kano.ylframework.excel`。基础 UI Sample 不依赖它们；这里不包含依赖缺席就产生 Missing Script 的场景或 Prefab。

1. 用现有 DataAssets 作者工具创建根，将条目类型绑定到 `LocalizedItemData` 脚本。
2. 创建一行业务数据，直接在 `title` / `picture` 官方选择器选择表项。
3. 选中根执行 `Assets/YLFramework/Localization/Validate Data References`。
4. 执行 `Export Reference Workbook`，导出器只规范化临时克隆，不改原数据。
5. 在工作簿 Json 列中填写 `{"table":"Items","entry":"name.sword"}`，或使用复制出的稳定引用 `{"tableGuid":"集合GUID","entryId":"KeyID文本"}`。
6. 执行 `Preview Reference Workbook` 检查预览，取消不写数据，确认后才提交。提交前删掉或替换表项会阻止提交，不会误连同名新条目。

业务行 id 与翻译 Key ID 是两个身份。Key ID 用十进制文本，不让 Excel 浮点数损坏 64 位精度。`picture` 明确允许空单元格/JSON null；`title` 不允许空。导入只改表项身份，保留已有 fallback 和局部变量配置。

运行时把 `item.title` 直接交给 `LocalizationSession.BindString` 或 `LocalizeText.StringReference`。业务配置可由 YL Resource 加载，翻译表/本地化图片仍由官方 Localization/Addressables 管理，二者不互相接管租约。

翻译正文仍用官方 Tables、CSV/XLIFF 或 Google Sheets；该工作簿不维护另一份正文。
