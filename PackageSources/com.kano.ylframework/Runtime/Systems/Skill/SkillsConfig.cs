using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Serialization;
using YLFramework.Foundation;

namespace YLFramework.Skill
{
    /// <summary>
    /// 技能配表
    /// </summary>
    /// <remarks>保存源技能定义和按 ID 的查找缓存，不持有宿主的技能运行状态。</remarks>
    [CreateAssetMenu(menuName = "YLFramework/SkillSystem(技能配置)")]
    public class SkillsConfig : ScriptableObject
    {

        /// <summary>编辑器分配新技能 ID 时的下界，实际新 ID 还会超过当前配表的最大 ID。</summary>
        [HideInInspector]
        [FormerlySerializedAs("start_id")] public int startId = 0;

        /// <summary>配表显示名称，不替代管理器登记时使用的资产名称。</summary>
        [Header("显示名称")]
        public string displayName;

        /// <summary>作为装备定义来源的技能条目列表。</summary>
        [Tooltip("技能配置集合")]
        public List<SkillConfigData> skills = new List<SkillConfigData>();

        /// <summary>编辑器限制可创建数据类型的脚本 GUID；为空时使用 SkillConfigData。</summary>
        [HideInInspector]
        [FormerlySerializedAs("type_script_guid")] public string typeScriptGuid;

        private Dictionary<int, SkillConfigData> _skillsDic = new Dictionary<int, SkillConfigData>();

        /// <summary>
        /// 查询技能数据
        /// </summary>
        /// <param name="id">源技能定义的 ID。</param>
        /// <returns>源定义；未找到时为 null，不返回装备实例的配置副本。</returns>
        /// <exception cref="Exception">首次建立缓存时发现重复技能 ID。</exception>
        /// <remarks>非空缓存不自动跟随列表或 ID 修改，修改后需调用 ClearCache。</remarks>
        public SkillConfigData GetSkillConfigData(int id)
        {

            if (_skillsDic.Count == 0)
            {
                foreach (var item in skills)
                {

                    if (item == null)
                    {
                        continue;
                    }

                    if (_skillsDic.ContainsKey(item.id))
                    {
                        throw new Exception(string.Format("技能id:{0}重复!", id));
                    }

                    _skillsDic.Add(item.id, item);
                }
            }

            if (_skillsDic.ContainsKey(id))
            {
                return _skillsDic[id];
            }

            return null;
        }

        /// <summary>清空定义查找缓存，让下次查询重读列表，不改变已装备技能。</summary>
        public void ClearCache()
        {
            _skillsDic.Clear();
        }

    }

}
