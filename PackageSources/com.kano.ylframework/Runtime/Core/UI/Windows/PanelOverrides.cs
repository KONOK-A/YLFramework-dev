using System;

namespace YLFramework.Core
{
    /// <summary>运行时字段级覆盖。null 表示未指定；显式 false/0 会覆盖下层值，不承担 Inspector 序列化。</summary>
    public struct PanelOverrides
    {
        /// <summary>覆盖根内显式层索引。</summary>
        public int? Layer;
        /// <summary>覆盖同层同 Modal 分组内优先级，0 也是显式值。</summary>
        public int? Priority;
        /// <summary>覆盖模态阻挡声明，false 也是显式值。</summary>
        public bool? Modal;
        /// <summary>覆盖完整覆盖声明，不设置 RectTransform。</summary>
        public bool? CoversRoot;
        /// <summary>覆盖 Top 参与资格。</summary>
        public bool? ParticipatesInTop;
        /// <summary>仅打开请求可覆盖实例模式，UpdateDisplay 拒绝此字段。</summary>
        public PanelInstanceMode? InstanceMode;
        /// <summary>仅打开请求可覆盖缓存策略，UpdateDisplay 拒绝此字段。</summary>
        public bool? CacheOnClose;

        /// <summary>生成新的配置值，不修改输入。层是否存在由实际根的提交预校验负责。</summary>
        public PanelConfig ApplyTo(PanelConfig defaults)
        {
            var result = new PanelConfig
            {
                layer = Layer ?? defaults.layer,
                priority = Priority ?? defaults.priority,
                modal = Modal ?? defaults.modal,
                coversRoot = CoversRoot ?? defaults.coversRoot,
                participatesInTop = ParticipatesInTop ?? defaults.participatesInTop,
                instanceMode = InstanceMode ?? defaults.instanceMode,
                cacheOnClose = CacheOnClose ?? defaults.cacheOnClose
            };
            result.Validate();
            return result;
        }

        internal void Validate() => ApplyTo(PanelConfig.Default);
        internal void ValidateDisplay()
        {
            Validate();
            if (InstanceMode.HasValue || CacheOnClose.HasValue)
            {
                throw new ArgumentException("UpdateDisplay 只接受五个显示字段，不能改变实例模式或缓存策略。");
            }
        }
    }
}
