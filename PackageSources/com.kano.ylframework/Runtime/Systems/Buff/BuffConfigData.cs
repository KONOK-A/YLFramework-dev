using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Serialization;
using YLFramework.Excel;
using YLFramework.Foundation;

namespace YLFramework.Buff
{

    /// <summary>以资源项目与路径标识 Buff 图标，不持有已加载的纹理实例。</summary>
    [Serializable]
    public class IconInfo
    {
        /// <summary>图标所属的资源项目标识。</summary>
        public string projectName;
        /// <summary>图标在资源项目中的路径。</summary>
        public string iconName;
    }

    /// <summary>决定同一宿主再次添加相同 Buff ID 时如何处理已有实例。</summary>
    public enum BuffMode
    {
        /// <summary>
        /// 添加新实例前移除该宿主已有的同 ID 实例，不在旧实例上刷新时间。
        /// </summary>
        [Tooltip("不可叠加")]
        Single,
        /// <summary>
        /// 保留多个同 ID 实例，各自持有配置副本并独立计时，不合并为层数计数器。
        /// </summary>
        [Tooltip("可叠加")]
        Additive
    }


    /// <summary>
    /// 配表中的 Buff 定义；管理器添加时复制本对象及效果，供本次运行实例独立使用。
    /// </summary>
    public class BuffConfigData : ScriptableObject
    {

        /// <summary>所属配表；运行副本中指向提供定义的已登记配表。</summary>
        [Tooltip("当前数据所在的配表")]
        [Header("当前数据所在的配表")]

        [ExcelIgnore]
        public BuffsConfig parent;

        /// <summary>跨已登记配表唯一的 Buff ID。</summary>
        [Tooltip("唯一标识")]

        [ExcelScriptableObjectId]
        [FormerlySerializedAs("buff_id")] public int id;
        /// <summary>供界面展示的 Buff 名称。</summary>
        [Tooltip("名称")]
        [Header("名称")]
        [FormerlySerializedAs("buff_name")] public string buffName;
        /// <summary>Buff 图标的资源地址信息。</summary>
        [Tooltip("图标")]
        [Header("图标")]

        public IconInfo icon = new IconInfo();
        /// <summary>供界面或作者工具展示的说明文本。</summary>
        [Tooltip("描述")]
        [Header("描述")]
        [TextArea(3, 8)]
        public string description;
        /// <summary>默认持续秒数；小于等于零表示不按时间自动移除。</summary>
        [Tooltip("持续时间(如果小于等于0,认为该Buff长期存在!)")]
        [Header("持续时间(如果小于等于0,认为该Buff长期存在!)")]
        public float duration;
        /// <summary>同宿主、同 ID Buff 的实例共存策略。</summary>
        [Tooltip("模式(如果有多个相同的buff，它们的效果是否能够叠加?)")]
        [Header("模式(如果有多个相同的buff，它们的效果是否能够叠加?)")]
        public BuffMode mode = BuffMode.Additive;
        /// <summary>本 Buff 引用的效果定义，添加时逐项复制，保留列表中的重复项。</summary>
        [Tooltip("Buff效果(当前Buff有哪些效果)")]
        [Header("Buff效果(当前Buff有哪些效果)")]

        [BuffEffectExcel]
        public List<EffectData> effects = new List<EffectData>();
        /// <summary>管理器反射创建的 BuffBase 具体类型全名，须有 public 无参构造器。</summary>
        [Tooltip("Buff脚本名称")]

        [FormerlySerializedAs("buff_class_full_name")] public string buffClassFullName;

        /// <summary>供编辑器维护类型绑定的脚本 GUID，运行时按类型全名创建。</summary>
        [Tooltip("Buff脚本guid")]
        [HideInInspector]
        [ExcelIgnore]
        [FormerlySerializedAs("buff_class_guid")] public string buffClassGuid;

    }
}
