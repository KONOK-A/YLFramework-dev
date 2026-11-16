using UnityEngine;
using YLFramework.Binding;
using YLFramework.Core;

namespace YLFramework.Localization.Samples
{
    /// <summary>Core Panel 接入范式：由应用通过 Module.LoadPanel 打开，不声明竞争的 Unity 消息。</summary>
    public sealed class LocalizedCounterPanel : Panel
    {
        [SerializeField] private LocalizationGroup _group;
        [SerializeField] private LocalizeText _counter;
        private readonly BindableProperty<int> _count = new BindableProperty<int>(1);

        /// <inheritdoc/>
        protected override void OnOpen(LoadViewArgs args)
        {
            _group.BeginSession(this, _ => _counter.StringBinding.BindVariable("count", _count));
        }

        /// <summary>更新本实例业务数据，不调用显示命令或操作翻译资产。</summary>
        public void Increase() => _count.Value++;
    }
}
