using YLFramework.Foundation;
using YLFramework.Attributes;
#if UNITY_EDITOR

using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using YLFramework.Buff;
using YLFramework.Editor.Common;


namespace YLFramework.Editor.Buff
{
    /// <summary>为业务效果分类编号提供作者工具中的显示名称。</summary>
    [Serializable]
    public class EffectType
    {
        /// <summary>与 EffectData.type 对应的业务分类编号。</summary>
        public int type;
        /// <summary>该分类的显示名称。</summary>
        public string name;
    }

    /// <summary>把相同 C# 数据类型的效果资产归组，供效果树展示与组内排序。</summary>
    [Serializable]
    public class GroupEffectData
    {
        /// <summary>本分组的效果定义引用，顺序用于树形列表展示。</summary>
        public List<EffectData> effectDatas = new List<EffectData>();

        /// <summary>首个有效效果类型的 EffectName 或类型全名；没有有效项时返回空字符串。</summary>
        public string GroupName
        {
            get
            {
                foreach (var item in effectDatas)
                {
                    if (item == null)
                    {
                        continue;
                    }

                    string groupName = string.Empty;

                    EffectName effectName = item.GetType().GetCustomAttribute<EffectName>();
                    if (effectName != null)
                    {
                        groupName = effectName.Name;
                    }
                    else
                    {
                        groupName = item.GetType().FullName;
                    }

                    return groupName;
                }

                return string.Empty;
            }
        }

        /// <summary>清空本分组的引用列表，不删除效果资产。</summary>
        public void Clear()
        {
            effectDatas.Clear();
        }


        /// <summary>把效果引用追加到分组，不检查重复项或类型一致性。</summary>
        public void Add(EffectData data)
        {
            effectDatas.Add(data);
        }


        /// <summary>判断分组内是否没有任何有效的效果资产。</summary>
        public bool IsEmpty()
        {
            foreach (var item in effectDatas)
            {
                if (item != null)
                {
                    return false;
                }
            }

            return true;
        }

    }

    /// <summary>保存 Editor 的效果分类、资产登记与 C# 类型分组，并提供 ID 查询缓存。</summary>
    [PreviousProjectSettingsName("YLFramework.BuffSystem.BuffSystemSettings")]
    public class BuffSystemSettings : ProjectSettingsScriptableObject<BuffSystemSettings>
    {

        #region 字段

        [Tooltip("效果类型")]
        [Header("效果类型")]
        [SerializeField]
        private List<EffectType> allEffectTypes;

        [Tooltip("效果配置")]
        [SerializeField]
        private List<EffectData> allEffectDatas;

        [SerializeField]
        private List<GroupEffectData> allEffectDataWithType;

        private float _lastUpdateTime;

        private float _lastUpdateTypeTime;

        private Dictionary<Type, GroupEffectData> _allEffectDataDic;

        private Dictionary<int, EffectData> _effectDatasDic = null;

        #endregion

        #region 属性

        /// <summary>返回共用效果列表；读取可能清空失效项、重新扫描资产并保存项目设置。</summary>
        public List<EffectData> AllEffectDatas
        {
            get
            {
                if (allEffectDatas == null)
                {
                    allEffectDatas = new List<EffectData>();
                }

                bool remove = false;

                for (int i = allEffectDatas.Count - 1; i >= 0; i--)
                {
                    if (allEffectDatas[i] != null)
                    {
                        continue;
                    }

                    allEffectDatas.RemoveAt(i);
                    remove = true;
                }

                if (remove)
                {
                    UpdateAllEffectData();
                    Save();
                }

                if (allEffectDatas.Count == 0)
                {
                    // 尝试查询 同步一下
                    SyncEffectData();
                }

                return allEffectDatas;
            }
        }

        /// <summary>返回按 C# 类型组织的共用分组；移除空组，必要时限频同步并保存。</summary>
        public List<GroupEffectData> AllEffectDataWithType
        {
            get
            {
                if (allEffectDataWithType == null)
                {
                    allEffectDataWithType = new List<GroupEffectData>();
                }

                for (int i = allEffectDataWithType.Count - 1; i >= 0; i--)
                {
                    if (allEffectDataWithType[i].IsEmpty())
                    {
                        allEffectDataWithType.RemoveAt(i);
                    }
                }

                if (allEffectDataWithType.Count == 0)
                {
                    // 尝试同步
                    SyncEffectDataWithType();
                }


                return allEffectDataWithType;
            }
        }

        /// <summary>取得可直接编辑的业务分类列表；首次读取会初始化空列表。</summary>
        public List<EffectType> AllEffectType
        {
            get
            {
                if (allEffectTypes == null)
                {
                    allEffectTypes = new List<EffectType>();
                }

                return allEffectTypes;
            }
        }

        #endregion


        /// <summary>将业务分类编号格式化为编号与名称，未登记分类时返回空分类提示。</summary>
        /// <param name="type">业务分类编号，不是 C# 类型。</param>
        /// <param name="richText">是否使用富文本将名称显示为灰色。</param>
        /// <returns>供作者界面显示的分类文本。</returns>
        public string GetType(int type, bool richText = true)
        {
            foreach (var item in AllEffectType)
            {
                if (item.type == type)
                {
                    if (string.IsNullOrEmpty(item.name))
                    {
                        return item.type.ToString();
                    }

                    if (richText)
                    {
                        return string.Format("{0}<color=grey>({1})</color>", type, item.name);
                    }
                    else
                    {
                        return string.Format("{0}({1})", type, item.name);
                    }
                }
            }

            return "空";
        }

        /// <summary>以一秒间隔限制资产重扫，并保存更新后的效果登记。</summary>
        internal void SyncEffectData()
        {
            if (Time.realtimeSinceStartup - _lastUpdateTime <= 1)
            {
                return;
            }

            _lastUpdateTime = Time.realtimeSinceStartup;

            UpdateAllEffectData();

            Save();
        }

        /// <summary>以一秒间隔限制类型分组重建，并保存更新后的设置。</summary>
        internal void SyncEffectDataWithType()
        {
            if (Time.realtimeSinceStartup - _lastUpdateTypeTime <= 1)
            {
                return;
            }

            _lastUpdateTypeTime = Time.realtimeSinceStartup;

            UpdateAllEffectDataWithType();

            Save();
        }

        /// <summary>扫描全部 EffectData 资产以重建登记列表并清空 ID 缓存，本方法不主动保存。</summary>
        internal void UpdateAllEffectData()
        {
            allEffectDatas ??= new List<EffectData>();
            allEffectDatas.Clear();
            ClearCache();
            string filter = string.Format("t:{0}", typeof(EffectData).FullName);

            string[] guids = AssetDatabase.FindAssets(filter);

            if (guids == null || guids.Length == 0)
            {
                return;
            }

            foreach (var guid in guids)
            {
                string assetpath = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(assetpath))
                {
                    continue;
                }

                EffectData config = AssetDatabase.LoadAssetAtPath<EffectData>(assetpath);
                if (config == null)
                {
                    continue;
                }

                allEffectDatas.Add(config);
            }

            //Save();
        }

        /// <summary>按效果实际 C# 类型重建分组列表，不按业务分类编号分组。</summary>
        internal void UpdateAllEffectDataWithType()
        {
            if (_allEffectDataDic == null)
            {
                _allEffectDataDic = new Dictionary<Type, GroupEffectData>();
            }

            foreach (var item in _allEffectDataDic.Keys)
            {
                _allEffectDataDic[item].Clear();
            }

            foreach (var item in AllEffectDatas)
            {
                Type type = item.GetType();
                if (!_allEffectDataDic.ContainsKey(type))
                {
                    _allEffectDataDic.Add(type, new GroupEffectData());
                }

                _allEffectDataDic[type].Add(item);
            }

            allEffectDataWithType ??= new List<GroupEffectData>();
            allEffectDataWithType.Clear();

            foreach (var item in _allEffectDataDic.Values)
            {
                allEffectDataWithType.Add(item);
            }


        }

        /// <summary>登记尚未收录的效果引用，更新分组、保存设置并清除 ID 缓存。</summary>
        public void AddEffectData(EffectData effectData)
        {
            if (AllEffectDatas.Contains(effectData))
            {
                return;
            }

            AllEffectDatas.Add(effectData);

            UpdateAllEffectDataWithType();

            Save();
            ClearCache();
        }

        /// <summary>从登记列表移除效果引用并保存设置，不删除效果资产文件。</summary>
        public void RemoveEffectData(EffectData effectData)
        {
            if (!AllEffectDatas.Contains(effectData))
            {
                return;
            }

            AllEffectDatas.Remove(effectData);

            UpdateAllEffectDataWithType();

            Save();
            ClearCache();
        }


        /// <summary>按 ID 查询登记的效果定义；重复 ID 记录错误并保留首项，未找到时返回 null。</summary>
        public EffectData GetEffectDataById(int id)
        {

            if (_effectDatasDic == null)
            {
                _effectDatasDic = new Dictionary<int, EffectData>();
            }

            if (_effectDatasDic.Count == 0)
            {
                foreach (var item in AllEffectDatas)
                {
                    if (_effectDatasDic.ContainsKey(item.id))
                    {
                        Debug.LogErrorFormat("EffectData发现重复的id:{0}", item.id);
                        continue;
                    }

                    _effectDatasDic.Add(item.id, item);
                }
            }


            if (_effectDatasDic.ContainsKey(id))
            {
                return _effectDatasDic[id];
            }

            return null;
        }


        /// <summary>清空效果 ID 查询缓存，不改动登记列表或资产文件。</summary>
        public void ClearCache()
        {
            if (_effectDatasDic != null)
            {
                _effectDatasDic.Clear();
            }
        }

    }

}

#endif
