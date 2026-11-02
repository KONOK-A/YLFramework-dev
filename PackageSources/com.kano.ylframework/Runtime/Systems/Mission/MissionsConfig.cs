using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Serialization;
using YLFramework.Foundation;

namespace YLFramework.Mission
{
    /// <summary>
    /// 任务配表
    /// </summary>
    /// <remarks>保存源定义及编辑器查找缓存；运行任务、参数值与监听由 MissionManager 另行创建和持有。</remarks>
    [CreateAssetMenu(menuName = "YLFramework/MissionSystem(任务配置)")]
    public class MissionsConfig : ScriptableObject
    {

        /// <summary>编辑器分配新任务 ID 时使用的下界，实际新 ID 还会超过现有任务最大值。</summary>
        [HideInInspector]
        [FormerlySerializedAs("start_id")] public int startId = 0;

        /// <summary>
        /// 任务配置集合
        /// </summary>
        [HideInInspector]
        [Tooltip("任务配置集合")]
        public List<MissionConfigData> missions = new List<MissionConfigData>();

        /// <summary>
        /// 参数集合
        /// </summary>
        [Tooltip("参数集合")]
        [HideInInspector]
        [FormerlySerializedAs("param")] public List<MissionParameter> parameters = new List<MissionParameter>();

        private Dictionary<int, MissionConfigData> _missionsDic = new Dictionary<int, MissionConfigData>();

        private Dictionary<string, MissionParameter> _paramDic = new Dictionary<string, MissionParameter>();

        /// <summary>
        /// 显示名
        /// </summary>
        [Tooltip("显示名")]
        [Header("显示名")]
        [FormerlySerializedAs("display_name")] public string displayName;

        /// <summary>编辑器限定可创建任务数据类型的脚本 GUID；为空时采用基础数据类型。</summary>
        [FormerlySerializedAs("type_script_guid")] public string typeScriptGuid;


        /// <summary>
        /// 查询任务配置
        /// </summary>
        /// <param name="id">任务id</param>
        /// <returns>源任务定义，未找到时为 null。</returns>
        /// <exception cref="Exception">首次建立缓存时发现重复任务 ID。</exception>
        /// <remarks>非空缓存不会自动跟随列表或 ID 修改，修改后需调用 ClearCache。</remarks>
        public MissionConfigData GetMissionConfigData(int id)
        {

            if (_missionsDic.Count == 0)
            {
                foreach (var item in missions)
                {
                    if (item == null)
                    {
                        continue;
                    }

                    if (_missionsDic.ContainsKey(item.id))
                    {
                        throw new Exception(string.Format("任务id:{0}重复!", id));
                    }

                    _missionsDic.Add(item.id, item);
                }
            }

            if (_missionsDic.ContainsKey(id))
            {
                return _missionsDic[id];
            }

            return null;
        }

        /// <summary>
        /// 查询参数配置
        /// </summary>
        /// <param name="paramName">参数名称</param>
        /// <returns>源参数配置，名称为空或未找到时为 null。</returns>
        /// <exception cref="Exception">首次建立缓存时发现重复参数名称。</exception>
        public MissionParameter GetMissionParam(string paramName)
        {

            if (string.IsNullOrEmpty(paramName))
            {
                return null;
            }

            if (_paramDic.Count == 0)
            {
                foreach (var item in parameters)
                {
                    if (_paramDic.ContainsKey(item.name))
                    {
                        throw new Exception(string.Format("参数名称:{0}重复!", item.name));
                    }

                    _paramDic.Add(item.name, item);
                }
            }

            if (_paramDic.ContainsKey(paramName))
            {
                return _paramDic[paramName];
            }

            return null;
        }

        /// <summary>
        /// 清空源定义的查找缓存，使后续查询重新读取列表，不影响已登记的运行实例。
        /// </summary>
        public void ClearCache()
        {
            _missionsDic.Clear();
            _paramDic.Clear();
        }

        /// <summary>
        /// 返回编辑器显示名称；未配置显示名时使用资产名，否则在资产名后附加显示名。
        /// </summary>
        public string GetDisplayName()
        {
            if (string.IsNullOrEmpty(displayName))
            {
                return name;
            }
            else
            {
                return string.Format("{0}({1})", name, displayName);
            }
        }

    }

}
