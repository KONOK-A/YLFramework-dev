using UnityEngine;
using UnityEngine.Serialization;
using YLFramework.Foundation;

namespace YLFramework.Buff
{
    /// <summary>
    /// 保存效果的标识、分类与说明；业务效果数据可派生扩展，本类型不执行效果逻辑。
    /// </summary>
    [EffectName("基础效果(EffectData)")]
    public class EffectData : ScriptableObject
    {
        /// <summary>效果 ID，用于效果存在性查询和 Excel 引用解析。</summary>
        [Tooltip("唯一标识")]
        public int id;

        /// <summary>效果的显示名称。</summary>
        [Tooltip("名称")]
        [FormerlySerializedAs("effect_name")] public string effectName;

        /// <summary>业务分类编号，与效果的 C# 派生类型不同。</summary>
        [Tooltip("类型")]
        [Header("类型")]
        public int type;

        /// <summary>效果的说明文本。</summary>
        [Tooltip("描述")]
        [Header("描述")]
        [TextArea(3, 8)]
        public string description;



    }
}
