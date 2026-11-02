using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Serialization;
using YLFramework.Foundation;

namespace YLFramework.Mission
{
    /// <summary>
    /// 图标数据
    /// </summary>
    [Serializable]
    public class IconInfo
    {
        /// <summary>
        /// 资源模块名称
        /// </summary>
        [Tooltip("资源模块名称")]
        public string projectName;
        /// <summary>
        /// 图标名称
        /// </summary>
        [Tooltip("图标名称")]
        public string iconName;
    }

    /// <summary>
    /// 奖品数据
    /// </summary>
    [Serializable]
    public class RewardInfo
    {
        /// <summary>
        /// 奖励物品ID
        /// </summary>
        [Tooltip("奖励物品ID")]
        public int id;

        /// <summary>
        /// 奖励物品数量
        /// </summary>
        [Tooltip("奖励物品数量")]
        public int count;
    }

    /// <summary>
    /// 默认任务配置数据
    /// </summary>
    /// <remarks>资产保存任务定义；管理器为每个运行任务复制本对象和条件，奖励列表本身不执行发奖。</remarks>
    [Serializable]
    public class MissionConfigData : ScriptableObject
    {


        /// <summary>
        /// 当前数据所在的配表
        /// </summary>
        [Tooltip("当前数据所在的配表")]
        [Header("当前数据所在的配表")]
        [YLFramework.Excel.ExcelIgnore]

        public MissionsConfig parent;

        /// <summary>
        /// 任务ID(唯一标识,不可重复)
        /// </summary>
        [Tooltip("任务ID(唯一标识,不可重复)")]

        public int id;

        /// <summary>
        /// 任务名称
        /// </summary>
        [Tooltip("任务名称")]
        [Header("任务名称")]
        [FormerlySerializedAs("mission_name")] public string missionName;

        /// <summary>
        /// 任务图标
        /// </summary>
        [Tooltip("任务图标")]

        public IconInfo icon = new IconInfo();

        /// <summary>
        /// 任务描述
        /// </summary>
        [Tooltip("任务描述")]
        [Header("任务描述")]
        [TextArea(3, 8)]
        public string description;


        /// <summary>
        /// 完成任务的条件
        /// </summary>
        /// <remarks>任务执行中且列表非空时，全部条件满足才会自动完成；空列表不会自动完成任务。</remarks>
        [Tooltip("完成任务的条件")]

        public List<MissionCondition> conditions = new List<MissionCondition>();

        /// <summary>
        /// 任务完成的奖励
        /// </summary>
        [Tooltip("任务完成的奖励")]
        [Header("任务完成的奖励")]
        public List<RewardInfo> rewards = new List<RewardInfo>();


        /// <summary>
        /// 任务类型文件对应的guid
        /// </summary>
        [Tooltip("任务类型文件对应的guid")]
        [HideInInspector]
        [YLFramework.Excel.ExcelIgnore]
        [FormerlySerializedAs("mission_type_guid")] public string missionTypeGuid;

        /// <summary>
        /// 任务类全名称
        /// </summary>
        [Tooltip("任务类全名称")]

        [FormerlySerializedAs("mission_type_class_fullname")] public string missionTypeClassFullName;

        /// <summary>
        /// 任务完成后是否重置参数(该参数在没有其他正在执行中的任务引用的情况下,默认:true)
        /// </summary>
        /// <remarks>由条件满足驱动完成时，在状态通知后尝试归零；直接将任务状态设为 Completed 不执行这段重置。</remarks>
        [Tooltip("任务完成后是否重置参数(该参数在没有其他正在执行中的任务引用的情况下,默认:true)")]
        [Header("任务完成后是否重置参数(该参数在没有其他正在执行中的任务引用的情况下,默认:true)")]
        [FormerlySerializedAs("reset_param_on_completed")] public bool resetParamOnCompleted = true;

    }
}
