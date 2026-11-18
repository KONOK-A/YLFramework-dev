using System;

namespace YLFramework.Resource
{
    /// <summary>YL 所有权与图片策略；资源目录、依赖及内容版本由官方 Addressables 配置管理。</summary>
    public sealed class ResourceConfiguration
    {
        /// <summary>动态图片的有界输入和闲置策略。</summary>
        public ResourceImageOptions Images
        {
            get;
        }

        /// <summary>有界诊断历史容量，零表示不记录历史，不影响必要责任计数。</summary>
        public int DiagnosticHistoryCapacity
        {
            get;
        }
        /// <summary>显式组织受控启动更新。组合根承诺本方最先初始化且没有外部并发 Init/Check/Update；本选项不自动证明全进程顺序。</summary>
        public bool EnableStartupCatalogUpdates
        {
            get;
        }

        /// <summary>仅冻结行为选项，不初始化 Addressables 或创建宿主。</summary>
        public ResourceConfiguration(ResourceImageOptions images = null, int diagnosticHistoryCapacity = 0, bool enableStartupCatalogUpdates = false)
        {
            if (diagnosticHistoryCapacity < 0 || diagnosticHistoryCapacity > 65536)
                throw new ArgumentOutOfRangeException(nameof(diagnosticHistoryCapacity));
            Images = images ?? ResourceImageOptions.Default;
            DiagnosticHistoryCapacity = diagnosticHistoryCapacity;
            EnableStartupCatalogUpdates = enableStartupCatalogUpdates;
        }
    }
}
