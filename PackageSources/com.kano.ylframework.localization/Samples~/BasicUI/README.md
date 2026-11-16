# Basic UI

导入本 Sample 后，运行菜单 `YLFramework/Localization/Samples/Create Basic UI`。它只在新的 `Assets/YLLocalizationDemo_<时间>/` 下生成场景、Core Panel 模板、官方 Locale、String/Asset Table，以及程序生成的两色 Sprite。

先显式保存或关闭未命名场景，处理已有 Addressables 的脏修改。生成器不覆盖目录、不保存已有场景；已有 Localization Settings 不会被替换。未配置时会在样例目录创建官方 Settings 并将其设为当前设置。

打开生成的 `LocalizationDemo.unity` 进入 Play：

- `English` / `Francais` 使用官方 Locale 切换。
- `+1 item` 改变 BindableProperty，Smart String 自动刷新。
- `Rebind view` 开始新的展示轮次，旧结果不能回写。
- 文本和图片由专属内容 CanvasGroup 门控，加载/错误节点在门外。

生成器 API `LocalizationSampleBuilder.Create(folder, englishFont, frenchFont)` 可借用两份已导入、许可适用的项目 Font，自动创建官方字体表项及绑定。不复制或发行操作系统字体；不指定时使用 Unity 自带 UGUI 字体，用户可在生成资产的官方表中配置自己的字体。

`LocalizedCounterPanel.prefab` 是实际 Core Panel 模板：由应用通过已有 `Module.LoadPanel<LocalizedCounterPanel>(prefab, root: uiRoot)` 打开。示例 `OnOpen` 只调用 `BeginSession(this, configure)`；不在 OnOpen 调用 Hide/Show，也不缓存 LoadViewArgs。应用仍负责正常启动 Module 与准备 UIRootContext，样例不自动接管业务启动。

Player 前按官方 Addressables 工作流构建内容。编辑器 AssetDatabase 快速模式不是 Player 内容可用的证明。
