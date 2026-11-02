using System;
using System.Collections;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using YLFramework.Foundation;
using YLFramework.Attributes;
using YLFramework.Skill;

namespace YLFramework.Editor.Skill
{
    /// <summary>通过菜单扫描技能运行脚本并刷新绑定，为作者工具提供数据类型候选。</summary>
    public class ScriptBindController
    {
        // 从技能脚本 GUID 到类型全名的索引，由菜单刷新入口重建。
        private static Dictionary<string, string> _guidClassFullName = new Dictionary<string, string>();

        // 从技能类型全名到脚本 GUID 的反向索引。
        private static Dictionary<string, string> _classFullNameGuid = new Dictionary<string, string>();

        // 保存所有的继承自 SkillConfigData 的类型
        private static List<Type> _skillConfigDataTypes = null;

        /// <summary>重建脚本索引，遍历所有技能配表尝试同步类型绑定并保存资产。</summary>
        [MenuItem("Tools/YLFramework/Skill/Refresh Script Bindings")]
        static void RefreshAllSkillScripts()
        {
            RefreshScript();

            foreach (SkillsConfig skillsConfig in SkillsConfigController.GetAllConfigs())
            {
                RefreshScriptClassFullName(skillsConfig);
            }

            // 保存一下
            AssetDatabase.SaveAssets();
        }

        /// <summary>根据当前脚本索引尝试更新技能条目的类型全名和 GUID，并对变更条目标脏。</summary>
        private static void RefreshScriptClassFullName(SkillsConfig skillsConfig)
        {
            foreach (SkillConfigData skillConfigData in skillsConfig.skills)
            {
                if (skillConfigData == null)
                {
                    continue;
                }

                if (!string.IsNullOrEmpty(skillConfigData.skillClassGuid))
                {
                    // guid 不为空 尝试更新 class_full_name
                    string classFullName = GetClassFullNameByGuid(skillConfigData.skillClassGuid);
                    if (string.IsNullOrEmpty(classFullName))
                    {
                        // 此分支仍查 GUID 到类型全名的索引，并非按类型全名查 GUID，不能保证修复失效绑定。
                        string guid = GetClassFullNameByGuid(skillConfigData.skillClassFullName);
                        if (!string.IsNullOrEmpty(guid) && skillConfigData.skillClassGuid != guid)
                        {
                            skillConfigData.skillClassGuid = guid;
                            EditorUtility.SetDirty(skillConfigData);
                        }
                    }
                    else
                    {
                        if (skillConfigData.skillClassFullName != classFullName)
                        {
                            skillConfigData.skillClassFullName = classFullName;
                            EditorUtility.SetDirty(skillConfigData);
                        }
                    }


                }
                else if (string.IsNullOrEmpty(skillConfigData.skillClassGuid) && !string.IsNullOrEmpty(skillConfigData.skillClassFullName))
                {
                    // guid 是空 但是 class_full_name 不为空 尝试更新guid
                    string guid = GetGuidByClassFullName(skillConfigData.skillClassFullName);

                    if (string.IsNullOrEmpty(guid))
                    {
                        continue;
                    }

                    skillConfigData.skillClassGuid = guid;

                    EditorUtility.SetDirty(skillConfigData);
                }
            }
        }

        /// <summary>扫描 MonoScript，为 SkillBase 派生类重建 GUID 与类型全名的双向索引。</summary>
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

                if (type.IsSubclassOf(typeof(SkillBase)))
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

        /// <summary>按类型全名从扫描索引取得脚本 GUID，未命中时返回空字符串。</summary>
        private static string GetGuidByClassFullName(string fullName)
        {
            if (_classFullNameGuid.ContainsKey(fullName))
            {
                return _classFullNameGuid[fullName];
            }

            return string.Empty;
        }

        /// <summary>按脚本 GUID 从扫描索引取得类型全名，未命中时返回空字符串。</summary>
        private static string GetClassFullNameByGuid(string guid)
        {
            if (_guidClassFullName.ContainsKey(guid))
            {
                return _guidClassFullName[guid];
            }

            return string.Empty;
        }

        /// <summary>扫描指定数据基类及其派生类型，返回会被后续调用重填的共享列表，不排除抽象类型。</summary>
        public static List<Type> GetAllSkillConfigDataTypes(Type parent)
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

                if (type.IsSubclassOf(parent) || type == parent)
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
