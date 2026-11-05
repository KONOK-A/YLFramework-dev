using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using YLFramework.Foundation;
using YLFramework.Attributes;
using YLFramework.Buff;
using YLFramework.Editor.Common;


namespace YLFramework.Editor.Buff
{
    /// <summary>提供 Buff 配表与定义资产的查找、ID 分配和创建操作，不创建运行 Buff。</summary>
    public class BuffsConfigController
    {
        private const string BuffConfigDataPath = "Assets/YLFrameworkData/Buff/Buffs/{0}";

        private static List<BuffsConfig> _allConfigs = new List<BuffsConfig>();

        /// <summary>返回本表最大 ID 与起始基数较大者再加一，不检查其他配表的占用。</summary>
        public static int GenerateId(BuffsConfig config)
        {
            int maxId = config.startId;

            foreach (var item in config.buffs)
            {
                if (item.id > maxId)
                {
                    maxId = item.id;
                }
            }

            maxId++;

            return maxId;
        }

        /// <summary>创建指定 ScriptableObject 数据类型，再按已有实例入口写入配表。</summary>
        /// <param name="type">可创建的 BuffConfigData 类型，由调用方提供正确类型。</param>
        /// <param name="buffsConfig">接收定义的配表。</param>
        /// <returns>新条目 ID；配表为 null 时返回 -1。</returns>
        public static int CreateBuffConfigData(Type type, BuffsConfig buffsConfig)
        {
            BuffConfigData buff = ScriptableObject.CreateInstance(type) as BuffConfigData;
            return CreateBuffConfigData(buff, buffsConfig);
        }

        /// <summary>为数据实例分配 ID、设置所属配表并创建资产，登记后保存并刷新 AssetDatabase。</summary>
        /// <param name="buff">要写为独立资产的数据实例，不是运行 Buff。</param>
        /// <param name="buffsConfig">已保存的目标配表；null 时直接返回。</param>
        /// <returns>分配的 ID；配表为 null 时返回 -1。</returns>
        public static int CreateBuffConfigData(BuffConfigData buff, BuffsConfig buffsConfig)
        {
            if (buffsConfig == null)
            {
                return -1;
            }

            int id = GenerateId(buffsConfig);
            buff.parent = buffsConfig;
            buff.id = id;
            buff.buffName = string.Format("Buff:{0}", id);
            string path = GetBuffConfigDataPath(buffsConfig);
            CreateAsset(buff, path, false);
            buffsConfig.buffs.Add(buff);
            EditorUtility.SetDirty(buffsConfig);
            buffsConfig.ClearCache();
            buff.Rename();
            EditorUtility.SetDirty(buff);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            return id;
        }

        /// <summary>在目标目录创建不重名的 .asset 并选中，不登记配表关系或建立 Undo 记录。</summary>
        /// <param name="scriptableObject">待保存的数据对象。</param>
        /// <param name="path">资产目录，不含文件名；不存在时创建目录。</param>
        /// <param name="ping">是否在 Project 窗口中高亮新资产。</param>
        public static void CreateAsset(ScriptableObject scriptableObject, string path, bool ping = true)
        {
            if (!System.IO.Directory.Exists(path))
            {
                System.IO.Directory.CreateDirectory(path);
                AssetDatabase.Refresh();
            }

            string name = AssetNameTools.GetName(scriptableObject.GetType().Name, path);
            AssetDatabase.CreateAsset(scriptableObject, string.Format("{0}/{1}.asset", path, name));
            Selection.activeObject = scriptableObject;
            if (ping)
            {
                EditorGUIUtility.PingObject(scriptableObject);
            }
        }

        /// <summary>按配表资产的 GUID 取得条目目录，不创建目录。</summary>
        public static string GetBuffConfigDataPath(BuffsConfig buffsConfig)
        {
            string assetPath = AssetDatabase.GetAssetPath(buffsConfig);
            string guid = AssetDatabase.AssetPathToGUID(assetPath);
            return GetBuffConfigDataPath(guid);
        }

        /// <summary>按配表 GUID 组成条目目录；GUID 为空时返回空字符串。</summary>
        public static string GetBuffConfigDataPath(string guid)
        {
            if (string.IsNullOrEmpty(guid))
            {
                return string.Empty;
            }

            return string.Format(BuffConfigDataPath, guid);
        }


        /// <summary>扫描项目内 Buff 配表并返回共用结果列表；下次调用会清空并重填该列表。</summary>
        public static List<BuffsConfig> GetAllConfigs()
        {
            _allConfigs.Clear();

            string filter = string.Format("t:{0}", typeof(BuffsConfig).FullName);

            string[] guids = AssetDatabase.FindAssets(filter);
            if (guids == null || guids.Length == 0)
            {
                return _allConfigs;
            }

            foreach (var item in guids)
            {
                string assetpath = AssetDatabase.GUIDToAssetPath(item);
                if (string.IsNullOrEmpty(assetpath))
                {
                    continue;
                }

                BuffsConfig config = AssetDatabase.LoadAssetAtPath<BuffsConfig>(assetpath);
                if (config == null)
                {
                    continue;
                }

                _allConfigs.Add(config);
            }

            return _allConfigs;
        }

    }

}
