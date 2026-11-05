using System;
using System.Collections;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.PackageManager.UI;
using UnityEngine;
using YLFramework.Foundation;
using YLFramework.Attributes;
using YLFramework.Editor.Common;
using YLFramework.Mission;

namespace YLFramework.Editor.Mission
{
    /// <summary>检索任务配表并创建任务条目资产，负责 ID、归属和缓存的编辑器维护。</summary>
    public class MissionsConfigController
    {

        private static List<MissionsConfig> _allConfigs = new List<MissionsConfig>();

        /// <summary>重新扫描任务配表资产并返回共享结果列表；下次调用会清空并重填该列表。</summary>
        public static List<MissionsConfig> GetAllConfigs()
        {
            _allConfigs.Clear();

            string[] guids = AssetDatabase.FindAssets(string.Format("t:{0}", typeof(MissionsConfig).FullName));
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

                MissionsConfig config = AssetDatabase.LoadAssetAtPath<MissionsConfig>(assetpath);
                if (config == null)
                {
                    continue;
                }

                _allConfigs.Add(config);
            }

            return _allConfigs;
        }

        /// <summary>按给定数据类型创建任务对象，再交给条目创建入口登记为资产。</summary>
        public static int CreateMissionConfigData(Type type, MissionsConfig config)
        {
            MissionConfigData mission = ScriptableObject.CreateInstance(type) as MissionConfigData;
            return CreateMissionConfigData(mission, config);
        }

        /// <summary>为任务对象分配 ID 和默认名称，创建独立条目资产并登记到配表，未给配表时返回 -1。</summary>
        /// <remarks>修改会标脏并清缓存；本入口不统一保存所有修改，也不提供跨文件的 Undo 或失败回滚。</remarks>
        public static int CreateMissionConfigData(MissionConfigData mission, MissionsConfig config)
        {
            if (config == null)
            {
                return -1;
            }

            string assetPath = AssetDatabase.GetAssetPath(config);
            string guid = AssetDatabase.AssetPathToGUID(assetPath);

            int id = GenerateId(config);
            mission.id = id;
            mission.missionName = string.Format("Mission:{0}", id);
            mission.parent = config;

            string path = MissionAuthoring.GeneratedDirectory(config);
            CreateAsset(mission, path, false);
            config.missions.Add(mission);
            EditorUtility.SetDirty(config);
            config.ClearCache();
            mission.Rename();
            EditorUtility.SetDirty(mission);

            return id;
        }

        /// <summary>返回超过配表起始 ID 和现有最大任务 ID 的下一个值，不扫描其他配表。</summary>
        public static int GenerateId(MissionsConfig config)
        {
            int maxId = config.startId;

            foreach (var item in config.missions)
            {
                if (item.id > maxId)
                {
                    maxId = item.id;
                }
            }

            maxId++;

            return maxId;
        }

        /// <summary>必要时创建目录，以类型名生成不重名资产；ping 为 true 时同时选中并定位资产。</summary>
        public static void CreateAsset(ScriptableObject scriptableObject, string path, bool ping = true)
        {
            if (!System.IO.Directory.Exists(path))
            {
                System.IO.Directory.CreateDirectory(path);
                AssetDatabase.Refresh();
            }

            string name = AssetNameTools.GetName(scriptableObject.GetType().Name, path);
            AssetDatabase.CreateAsset(scriptableObject, string.Format("{0}/{1}.asset", path, name));

            if (ping)
            {
                Selection.activeObject = scriptableObject;
                EditorGUIUtility.PingObject(scriptableObject);
            }
        }

    }

}
