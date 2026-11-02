using System;
using UnityEngine.Serialization;

namespace YLFramework.Core
{
    /// <summary>同一 Module、实际根、可证明资源内的实例策略。</summary>
    public enum PanelInstanceMode
    {
        /// <summary>同一作用域复用健康会话，并合流有效在途意图。</summary>
        Single,
        /// <summary>每次打开建立独立会话。</summary>
        Multiple
    }

    /// <summary>Prefab 默认或当前会话的七字段值副本。结构体零值不等于框架默认，请使用 Default。</summary>
    [Serializable]
    public struct PanelConfig
    {
        /// <summary>实际根内的显式层索引，必须非负且存在。</summary>
        [FormerlySerializedAs("Layer")] public int layer;
        /// <summary>同层同 Modal 分组内的优先级，不是原生 Canvas 顺序。</summary>
        [FormerlySerializedAs("Priority")] public int priority;
        /// <summary>是否阻挡同根排序更低的标准 UI 输入。</summary>
        [FormerlySerializedAs("Modal")] public bool modal;
        /// <summary>完整覆盖声明；只允许停绘低于它的同根 Panel，不改变布局。</summary>
        [FormerlySerializedAs("CoversRoot")] public bool coversRoot;
        /// <summary>是否参与 Top 查询，不影响 Modal 本身的阻挡资格。</summary>
        [FormerlySerializedAs("ParticipatesInTop")] public bool participatesInTop;
        /// <summary>同 Module、实际根和可证明来源内的实例模式。</summary>
        [FormerlySerializedAs("InstanceMode")] public PanelInstanceMode instanceMode;
        /// <summary>正常结束时是否允许后端缓存；损坏或来源责任不可转移时仍会丢弃。</summary>
        [FormerlySerializedAs("CacheOnClose")] public bool cacheOnClose;

        /// <summary>轻量窗口默认：Layer/Priority 为 0，非模态、不覆盖、参与 Top、单实例且可缓存。</summary>
        public static PanelConfig Default => new PanelConfig { participatesInTop = true, cacheOnClose = true };

        /// <summary>依次按 Layer、Modal、Priority 和接受顺序升序比较；序号只打破前三项的平局。</summary>
        internal static int Compare(PanelConfig a, long aSequence, PanelConfig b, long bSequence)
        {
            int result = a.layer.CompareTo(b.layer);
            if (result == 0)
            {
                result = a.modal.CompareTo(b.modal);
            }

            if (result == 0)
            {
                result = a.priority.CompareTo(b.priority);
            }

            return result == 0 ? aSequence.CompareTo(bSequence) : result;
        }

        /// <summary>检查配置自身的层索引和实例模式；不在此判断实际根是否提供对应层。</summary>
        internal void Validate()
        {
            if (layer < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(layer), "Panel Layer 不能为负数。");
            }

            if (instanceMode != PanelInstanceMode.Single && instanceMode != PanelInstanceMode.Multiple)
            {
                throw new ArgumentOutOfRangeException(nameof(instanceMode));
            }
        }
    }
}
