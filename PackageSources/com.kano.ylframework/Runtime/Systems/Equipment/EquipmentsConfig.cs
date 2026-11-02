using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Serialization;

namespace YLFramework.Equipment
{
    /// <summary>保存共享装备定义及 ID 查询索引，不持有运行装备或执行资产扫描。</summary>
    [CreateAssetMenu(menuName = "YLFramework/EquipmentSystem(装备配置)")]
    public class EquipmentsConfig : ScriptableObject
    {
        /// <summary>供作者工具或业务界面展示的配表名称。</summary>
        public string displayName;
        /// <summary>作者工具生成条目 ID 时使用的起始基数。</summary>
        [HideInInspector, FormerlySerializedAs("start_id")] public int startId;
        [SerializeField] private List<EquipmentConfigData> equipments = new();
        /// <summary>编辑器限制新建定义类型的脚本 GUID，为空时使用基础配置类型。</summary>
        [HideInInspector, FormerlySerializedAs("type_script_guid")] public string typeScriptGuid;
        private Dictionary<int, EquipmentConfigData> _index;
        private EquipmentConfigData[] _snapshot;
        private int[] _ids;

        /// <summary>读取不扫描或保存资产；编辑列表后查询会核对引用与 ID 快照。</summary>
        public List<EquipmentConfigData> Equipments => equipments;

        /// <summary>按 ID 返回共享装备定义，未找到或对应对象已销毁时返回 null。</summary>
        /// <exception cref="InvalidOperationException">重建索引时发现重复 ID。</exception>
        public EquipmentConfigData GetEquipmentConfigData(int id)
        {
            if (!CacheMatches())
            {
                RebuildCache();
            }

            return _index.TryGetValue(id, out var value) && value ? value : null;
        }

        /// <summary>使定义查询索引与快照失效，不修改定义列表或保存资产。</summary>
        public void ClearCache()
        {
            _index = null;
            _snapshot = null;
            _ids = null;
        }

        /// <summary>检查列表槽位引用与 ID 是否仍与上次建立索引时一致。</summary>
        private bool CacheMatches()
        {
            if (_index == null || _snapshot.Length != equipments.Count)
            {
                return false;
            }

            for (var i = 0; i < _snapshot.Length; i++)
            {
                if (!ReferenceEquals(_snapshot[i], equipments[i]) || _ids[i] != (equipments[i] ? equipments[i].id : 0))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>跳过空项建立新索引，通过重复 ID 检查后再替换原缓存。</summary>
        private void RebuildCache()
        {
            var next = new Dictionary<int, EquipmentConfigData>();
            var entries = equipments.ToArray();
            var keys = new int[entries.Length];
            for (var i = 0; i < entries.Length; i++)
            {
                var data = entries[i];
                if (!data)
                {
                    continue;
                }

                keys[i] = data.id;
                if (next.ContainsKey(data.id))
                {
                    throw new InvalidOperationException($"Equipment '{name}': duplicate ID {data.id}, row {i}.");
                }

                next.Add(data.id, data);
            }
            _index = next;
            _snapshot = entries;
            _ids = keys;
        }
    }
}
