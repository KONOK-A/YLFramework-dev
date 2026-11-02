using System;
using System.Collections;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using YLFramework.Foundation;
using YLFramework.Attributes;
using YLFramework.Mission;

namespace YLFramework.Editor.Mission
{
    /// <summary>扫描任务脚本并通过菜单显式刷新 GUID 与类型全名绑定，同时提供数据类型候选。</summary>
    public class ScriptBindController
    {

        // 从脚本 GUID 到任务运行类型全名的索引。
        private static Dictionary<string, string> _guidClassFullName = new Dictionary<string, string>();

        // 从任务运行类型全名到脚本 GUID 的反向索引。
        private static Dictionary<string, string> _classFullNameGuid = new Dictionary<string, string>();

        // 保存扫描得到的 MissionConfigData 及其派生类型。
        private static List<Type> _skillConfigDataTypes = null;

        /// <summary>重建任务脚本索引，遍历全部任务配表更新绑定并保存资产。</summary>
        [MenuItem("Tools/YLFramework/Mission/Refresh Script Bindings")]
        static void RefreshAllSkillScripts()
        {
            RefreshScript();

            foreach (MissionsConfig skillsConfig in MissionsConfigController.GetAllConfigs())
            {
                RefreshScriptClassFullName(skillsConfig);
            }

            // 保存一下
            AssetDatabase.SaveAssets();
        }

        /// <summary>按脚本索引尝试更新配表内各任务的类型全名或 GUID，变更后标脏条目。</summary>
        private static void RefreshScriptClassFullName(MissionsConfig skillsConfig)
        {
            foreach (MissionConfigData missionConfigData in skillsConfig.missions)
            {
                if (missionConfigData == null)
                {
                    continue;
                }

                if (!string.IsNullOrEmpty(missionConfigData.missionTypeGuid))
                {
                    // guid 不为空 尝试更新 class_full_name
                    string classFullName = GetClassFullNameByGuid(missionConfigData.missionTypeGuid);
                    if (string.IsNullOrEmpty(classFullName))
                    {

                        // 此分支仍调用 GUID 到全名的索引，不能据此保证失效 GUID 能按类型全名修复。
                        string guid = GetClassFullNameByGuid(missionConfigData.missionTypeClassFullName);
                        if (!string.IsNullOrEmpty(guid) && missionConfigData.missionTypeGuid != guid)
                        {
                            missionConfigData.missionTypeGuid = guid;
                            EditorUtility.SetDirty(missionConfigData);
                        }
                    }
                    else
                    {
                        if (missionConfigData.missionTypeClassFullName != classFullName)
                        {
                            missionConfigData.missionTypeClassFullName = classFullName;
                            EditorUtility.SetDirty(missionConfigData);
                        }
                    }


                }
                else if (string.IsNullOrEmpty(missionConfigData.missionTypeGuid) && !string.IsNullOrEmpty(missionConfigData.missionTypeClassFullName))
                {
                    // guid 是空 但是 class_full_name 不为空 尝试更新guid
                    string guid = GetGuidByClassFullName(missionConfigData.missionTypeClassFullName);

                    if (string.IsNullOrEmpty(guid))
                    {
                        continue;
                    }

                    missionConfigData.missionTypeGuid = guid;

                    EditorUtility.SetDirty(missionConfigData);
                }
            }
        }

        /// <summary>扫描 MonoScript，仅为 MissionBase 派生类重建 GUID 和类型全名的双向索引。</summary>
        private static void RefreshScript()
        {
            if (_guidClassFullName == null)
            {
                _guidClassFullName = new Dictionary<string, string>();
            }

            _guidClassFullName.Clear();

            if (_classFullNameGuid == null)
            {
                _classFullNameGuid = new Dictionary<string, string>();
            }

            _classFullNameGuid.Clear();

            string[] scripts = AssetDatabase.FindAssets("t:Script");

            foreach (var guid in scripts)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                MonoScript script = AssetDatabase.LoadAssetAtPath<MonoScript>(path);

                if (script == null)
                {
                    continue;
                }

                Type type = script.GetClass();

                if (type == null)
                {
                    continue;
                }

                if (type.IsSubclassOf(typeof(MissionBase)))
                {
                    if (!_guidClassFullName.ContainsKey(guid))
                    {
                        _guidClassFullName.Add(guid, type.FullName);
                    }

                    if (!_classFullNameGuid.ContainsKey(type.FullName))
                    {
                        _classFullNameGuid.Add(type.FullName, guid);
                    }
                }
            }
        }

        /// <summary>在本次扫描索引中按类型全名查找脚本 GUID，未找到时返回空字符串。</summary>
        private static string GetGuidByClassFullName(string fullName)
        {
            if (_classFullNameGuid.ContainsKey(fullName))
            {
                return _classFullNameGuid[fullName];
            }

            return string.Empty;
        }

        /// <summary>在本次扫描索引中按脚本 GUID 查找类型全名，未找到时返回空字符串。</summary>
        private static string GetClassFullNameByGuid(string guid)
        {
            if (_guidClassFullName.ContainsKey(guid))
            {
                return _guidClassFullName[guid];
            }

            return string.Empty;
        }

        /// <summary>扫描并返回 MissionConfigData 及其派生类型的共享候选列表，不排除抽象类型。</summary>
        public static List<Type> GetAllSkillConfigDataTypes()
        {
            if (_skillConfigDataTypes == null)
            {
                _skillConfigDataTypes = new List<Type>();
            }

            _skillConfigDataTypes.Clear();

            string[] scripts = AssetDatabase.FindAssets("t:Script");

            foreach (var guid in scripts)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                MonoScript script = AssetDatabase.LoadAssetAtPath<MonoScript>(path);

                if (script == null)
                {
                    continue;
                }

                Type type = script.GetClass();

                if (type == null)
                {
                    continue;
                }

                if (type.IsSubclassOf(typeof(MissionConfigData)) || type == typeof(MissionConfigData))
                {
                    if (!_skillConfigDataTypes.Contains(type))
                    {
                        _skillConfigDataTypes.Add(type);
                    }
                }
            }

            return _skillConfigDataTypes;
        }




    }

}
