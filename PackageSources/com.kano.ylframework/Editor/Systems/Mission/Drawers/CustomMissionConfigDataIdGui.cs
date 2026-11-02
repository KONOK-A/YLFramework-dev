using YLFramework.Foundation;
using YLFramework.Attributes;
#if UNITY_EDITOR

using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using YLFramework.Mission;


namespace YLFramework.Editor.Mission
{
    /// <summary>任务 ID 编辑绘制器，跨项目任务配表检查占用并同步条目资产名称。</summary>
    public class CustomMissionConfigDataIdGui : CustomPropertyGui
    {

        /// <summary>创建绘制器，并请求接入 Inspector 在 ID 变化后重载列表树。</summary>
        public CustomMissionConfigDataIdGui() : base(true) { }


        /// <summary>延迟提交未占用 ID，应用序列化修改后请求资产改名并清除父配表缓存。</summary>
        public override void OnPropertyGui(SerializedProperty serializedProperty)
        {
            GUILayout.Space(10);

            EditorGUILayout.LabelField(string.Empty, "任务ID(唯一标识,不可重复)", "BoldLabel");

            int id = serializedProperty.intValue;

            EditorGUI.BeginChangeCheck();
            id = EditorGUILayout.DelayedIntField("Id", id);
            if (EditorGUI.EndChangeCheck())
            {
                if (ContainsId(id))
                {
                    EditorWindow.focusedWindow.ShowNotification(EditorGUIUtility.TrTempContent(string.Format("ID:{0}已经存在,请使用其他的数字作为ID!", id)));
                    return;
                }

                serializedProperty.intValue = id;
                serializedProperty.serializedObject.ApplyModifiedProperties();
                MissionConfigData missionConfigData = serializedProperty.serializedObject.targetObject as MissionConfigData;
                if (missionConfigData != null)
                {
                    missionConfigData.Rename();
                }

                if (missionConfigData != null && missionConfigData.parent != null)
                {
                    missionConfigData.parent.ClearCache();
                }
            }
        }


        /// <summary>扫描全部任务配表，通过各配表查找缓存判断 ID 是否已被使用。</summary>
        private bool ContainsId(int id)
        {

            List<MissionsConfig> allConfigs = GetAllConfigs();

            foreach (var item in allConfigs)
            {
                if (item.GetMissionConfigData(id) != null)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>扫描并返回可加载的任务配表资产，结果使用新的列表保存。</summary>
        public static List<MissionsConfig> GetAllConfigs()
        {
            List<MissionsConfig> allConfigs = new List<MissionsConfig>();

            string[] guids = AssetDatabase.FindAssets(string.Format("t:{0}", typeof(MissionsConfig).FullName));
            if (guids == null || guids.Length == 0)
            {
                return allConfigs;
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

                allConfigs.Add(config);
            }

            return allConfigs;
        }

    }

}

#endif
