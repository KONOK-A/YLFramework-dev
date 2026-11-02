using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Serialization;

namespace YLFramework.DataAssets
{
    /// <summary>保存配置条目引用并提供根内 ID、名称查询；查询结果是共享定义，不是运行时实例。</summary>
    [CreateAssetMenu(menuName = "YLFramework/Data Assets/Configuration Root", fileName = "DataConfigs")]
    public class ScriptableObjectConfigs : ScriptableObject
    {
        /// <summary>编辑器显示名称，不参与管理器按资产对象名进行的根登记。</summary>
        public string displayName;
        /// <summary>作者维护的有序条目引用；查询忽略空槽，但拒绝重复 ID。</summary>
        [HideInInspector]
        public List<ScriptableObjectData> configs = new List<ScriptableObjectData>();
        /// <summary>编辑器允许创建的条目基类脚本 GUID；为空时使用默认条目基类。</summary>
        [HideInInspector]
        [FormerlySerializedAs("type_script_guid")] public string typeScriptGuid;

        private DataAssetIdIndex _index;
        private ScriptableObjectData[] _indexedItems;
        private int?[] _indexedIds;

        /// <summary>有效条目的最小 ID；空目录返回 0，需要区分 ID 0 时使用 TryGetIdRange。</summary>
        public int MinId
        {
            get
            {
                TryGetIdRange(out var min, out _);
                return min;
            }
        }
        /// <summary>有效条目的最大 ID；空目录返回 0。</summary>
        public int MaxId
        {
            get
            {
                TryGetIdRange(out _, out var max);
                return max;
            }
        }

        /// <summary>取得忽略空槽后的 ID 范围；重复 ID 会使索引校验失败。</summary>
        /// <param name="min">有效条目的最小 ID，无有效条目时为 0。</param>
        /// <param name="max">有效条目的最大 ID，无有效条目时为 0。</param>
        /// <returns>存在有效条目时为真。</returns>
        public bool TryGetIdRange(out int min, out int max) => GetIndex().TryGetRange(out min, out max);

        /// <summary>按根内 ID 借读当前条目，未找到时返回空引用。</summary>
        /// <param name="id">条目的业务 ID。</param>
        public ScriptableObjectData GetScriptableObjectData(int id)
            => GetIndex().TryGetRow(id, out var row) ? configs[row] : null;

        /// <summary>按 ID 借读指定类型的条目；缺失或类型不符时返回空引用。</summary>
        /// <typeparam name="T">调用方需要的配置条目类型。</typeparam>
        /// <param name="id">条目的业务 ID。</param>
        public T GetScriptableObjectData<T>(int id) where T : ScriptableObjectData
            => GetScriptableObjectData(id) as T;

        /// <summary>名称精确匹配；歧义名称明确失败，不按列表顺序静默选取。</summary>
        /// <param name="configName">按 Ordinal 比较的配置名称，不能为 null。</param>
        /// <returns>匹配的借读条目；不存在匹配项时为空引用。</returns>
        public ScriptableObjectData GetScriptableObjectData(string configName)
        {
            if (configName == null)
            {
                throw new ArgumentNullException(nameof(configName));
            }

            GetIndex();
            ScriptableObjectData result = null;
            if (configs == null)
            {
                return null;
            }

            foreach (var item in configs)
            {
                if (!item || !StringComparer.Ordinal.Equals(item.configName, configName))
                {
                    continue;
                }

                if (result)
                {
                    throw new InvalidOperationException($"DataAssets '{name}': ambiguous name '{configName}' (IDs {result.id}, {item.id}).");
                }

                result = item;
            }
            return result;
        }

        /// <summary>取得指定 ID 的配置名称，条目缺失时返回空引用。</summary>
        public string GetName(int id)
        {
            var item = GetScriptableObjectData(id);
            return item ? item.configName : null;
        }

        /// <summary>借读指定 ID 的图标定位信息，条目缺失时返回空引用，不加载图片。</summary>
        public IconInfo GetIcon(int id)
        {
            var item = GetScriptableObjectData(id);
            return item ? item.icon : null;
        }
        /// <summary>生成当前索引中未占用的正 ID，不新增条目或预留该 ID。</summary>
        public int GenerateId() => GetIndex().NextId();

        /// <summary>核对条目引用和 ID 快照，在缓存失效或数据变化时重新校验并发布索引。</summary>
        private DataAssetIdIndex GetIndex()
        {
            var count = configs?.Count ?? 0;
            var changed = _index == null || _indexedItems == null || _indexedItems.Length != count;
            for (var i = 0; !changed && i < count; i++)
            {
                var item = configs[i];
                int? id = item ? item.id : (int?)null;
                changed = !ReferenceEquals(_indexedItems[i], item) || _indexedIds[i] != id;
            }
            if (!changed)
            {
                return _index;
            }

            ClearCache();
            var items = new ScriptableObjectData[count];
            var ids = new int?[count];
            for (var i = 0; i < count; i++)
            {
                items[i] = configs[i];
                ids[i] = items[i] ? items[i].id : (int?)null;
            }
            var rebuilt = new DataAssetIdIndex(ids, name);
            _indexedItems = items;
            _indexedIds = ids;
            _index = rebuilt;
            return _index;
        }

        /// <summary>丢弃查询索引与快照，不清空配置列表，也不释放配置资产。</summary>
        public void ClearCache()
        {
            _index = null;
            _indexedItems = null;
            _indexedIds = null;
        }

        /// <summary>资产启用时丢弃旧索引，避免沿用上次启用的数据快照。</summary>
        protected virtual void OnEnable() => ClearCache();
        /// <summary>资产停用时移除查询缓存，不改写序列化条目。</summary>
        protected virtual void OnDisable() => ClearCache();
        /// <summary>编辑器校验配置时使索引失效，后续查询重新校验数据。</summary>
        protected virtual void OnValidate() => ClearCache();
    }
}
