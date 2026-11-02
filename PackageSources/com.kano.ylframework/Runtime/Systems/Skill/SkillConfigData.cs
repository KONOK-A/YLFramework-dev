using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Serialization;
using YLFramework.Foundation;

namespace YLFramework.Skill
{
    /// <summary>技能图标的资源模块与资源名配置，不在此加载图标。</summary>
    [Serializable]
    public class IconInfo
    {
        /// <summary>图标所在资源模块名称。</summary>
        public string projectName;
        /// <summary>模块内的图标资源名称。</summary>
        public string iconName;
    }

    /// <summary>
    /// 默认技能配置数据
    /// </summary>
    /// <remarks>资产保存定义；管理器装备时复制本对象，SkillBase 再将计时和取消等设置复制为可变运行状态。</remarks>
    public class SkillConfigData : ScriptableObject
    {

        /// <summary>定义所属配表；运行副本也保留该归属，供移除配表时定位技能。</summary>
        [Tooltip("当前数据所在的配表")]
        [Header("当前数据所在的配表")]

        [YLFramework.Excel.ExcelIgnore]
        public SkillsConfig parent;

        /// <summary>技能 ID，管理器要求在所有已登记配表间唯一。</summary>
        [Tooltip("技能ID(唯一标识,不可重复)")]
        [Header("技能ID(唯一标识,不可重复)")]

        public int id;
        /// <summary>技能显示名称，不作为运行实例的字典键。</summary>
        [Tooltip("技能名称")]
        [Header("技能名称")]
        [FormerlySerializedAs("skill_name")] public string skillName;

        /// <summary>技能图标的资源定位信息。</summary>
        [Tooltip("技能图标")]
        [Header("技能图标")]

        public IconInfo icon = new IconInfo();
        /// <summary>初始化运行冷却时长，单位为秒；释放结束后才开始推进冷却。</summary>
        [Tooltip("冷却时间")]
        [Header("冷却时间")]
        [FormerlySerializedAs("cooling_time")] public float cooldownSeconds;
        /// <summary>初始化运行持续时长，单位为秒；达到后仍需技能完成检查通过。</summary>
        [Tooltip("持续时间")]
        [Header("持续时间")]
        public float duration;
        /// <summary>允许并行释放的技能 ID；运行判定允许双方任一列表声明对方。</summary>
        [Tooltip("能够同时释放的技能(默认所有的技能都不能同时释放)")]
        [Header("能够同时释放的技能(默认所有的技能都不能同时释放)")]

        [FormerlySerializedAs("canbe_released_at_the_same_time")] public List<int> simultaneousSkillIds = new List<int>();
        /// <summary>初始化主动取消开关，仅约束 Cancel，不阻止强制 Interrupt。</summary>
        [Tooltip("是否能够主动取消")]
        [Header("是否能够主动取消")]
        [FormerlySerializedAs("active_cancellation")] public bool allowCancellation;

        /// <summary>供业务和作者工具显示的技能说明。</summary>
        [Tooltip("技能描述")]
        [Header("技能描述")]
        //[Multiline(8)]
        [TextArea(3, 8)]
        public string description;

        /// <summary>运行技能类型全名，须能解析为带公共无参构造器的非抽象 SkillBase 派生类。</summary>
        [Tooltip("技能类名称(含命名空间)")]

        [FormerlySerializedAs("skill_class_full_name")] public string skillClassFullName;

        /// <summary>供编辑器定位技能脚本的 GUID，运行时类型解析使用类型全名。</summary>
        [Tooltip("技能类脚本guid")]
        [HideInInspector]
        [YLFramework.Excel.ExcelIgnore]
        [FormerlySerializedAs("skill_class_guid")] public string skillClassGuid;

    }
}
