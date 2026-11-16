namespace YLFramework.Localization
{
    /// <summary>局部本地化内容的就绪状态，不表示 Panel 的显示意图。</summary>
    public enum LocalizationGroupState
    {
        /// <summary>当前没有运行的展示轮次。</summary>
        Inactive,
        /// <summary>当前轮次仍有必要内容未就绪。</summary>
        Loading,
        /// <summary>必要内容已应用，布局已合并刷新。</summary>
        Ready,
        /// <summary>配置、初始化、加载或交付失败，可显式重试。</summary>
        Failed
    }
}
