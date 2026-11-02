using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Serialization;
using YLFramework.Foundation;

namespace YLFramework.Buff
{

    /// <summary>
    /// 汇集 Buff 定义并提供按 ID 查询的缓存；不保存宿主的运行实例。
    /// </summary>
    [CreateAssetMenu(menuName = "YLFramework/BuffsConfig(Buff配置)")]
    public class BuffsConfig : ScriptableObject
    {

        /// <summary>编辑器生成条目 ID 时使用的起始基数。</summary>
        [HideInInspector]
        [FormerlySerializedAs("start_id")] public int startId;

        /// <summary>供作者工具或业务界面展示的配表名称。</summary>
        [Header("显示名称")]
        public string displayName;

        /// <summary>本配表的定义列表；修改后调用 ClearCache 使查询索引失效。</summary>
        [Tooltip("Buff配表集合")]
        public List<BuffConfigData> buffs = new List<BuffConfigData>();

        private Dictionary<int, BuffConfigData> _buffsDic = new Dictionary<int, BuffConfigData>();


        /// <summary>编辑器限制新建配置类型所用的脚本 GUID；为空时使用基础配置类型。</summary>
        [FormerlySerializedAs("type_script_guid")] public string typeScriptGuid;

        /// <summary>
        /// 查询Buff配置信息
        /// </summary>
        /// <param name="buffId">要查询的 Buff ID。</param>
        /// <returns>配表中的共享定义；未找到时返回 null，不创建运行实例。</returns>
        /// <exception cref="System.Exception">建立缓存时发现重复 ID。</exception>
        public BuffConfigData GetBuffData(int buffId)
        {

            if (_buffsDic.Count == 0)
            {
                foreach (var item in buffs)
                {
                    if (!item)
                    {
                        continue;
                    }

                    if (_buffsDic.ContainsKey(item.id))
                    {
                        throw new System.Exception(string.Format("BuffInfo id:{0}重复!", item.id));
                    }

                    _buffsDic.Add(item.id, item);
                }
            }

            if (_buffsDic.ContainsKey(buffId))
            {
                return _buffsDic[buffId];
            }

            return null;
        }


        /// <summary>使 ID 查询缓存失效，下次查询时从定义列表重建。</summary>
        public void ClearCache()
        {
            _buffsDic.Clear();
        }

    }
}
