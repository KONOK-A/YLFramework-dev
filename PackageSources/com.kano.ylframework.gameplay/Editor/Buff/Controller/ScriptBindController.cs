using System;
using System.Collections;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using YLFramework.Foundation;
using YLFramework.Attributes;
using YLFramework.Buff;

namespace YLFramework.Editor.Buff
{
    /// <summary>扫描 Buff 脚本映射，并在显式刷新时维护定义中的脚本绑定。</summary>
    public class ScriptBindController
    {

        // 脚本 GUID 到类型全名的映射。
        private static Dictionary<string, string> _guidClassFullName = new Dictionary<string, string>();

        // 类型全名到脚本 GUID 的反向映射。
        private static Dictionary<string, string> _classFullNameGuid = new Dictionary<string, string>();

        // 保存扫描得到的 BuffConfigData 及其派生类型。
        private static List<Type> _skillConfigDataTypes = null;

        /// <summary>从菜单重建脚本映射，刷新全部 Buff 配表的类型绑定并保存资产。</summary>
        [MenuItem("Tools/YLFramework/Buff/Refresh Script Bindings")]
        static void RefreshAllSkillScripts()
        {
            RefreshScript();

            foreach (BuffsConfig buffsConfig in BuffsConfigController.GetAllConfigs())
            {
                RefreshScriptClassFullName(buffsConfig);
            }

            // 保存一下
            AssetDatabase.SaveAssets();
        }

        /// <summary>遍历配表，按已有 GUID 或类型全名尝试更新脚本绑定并标脏。</summary>
        private static void RefreshScriptClassFullName(BuffsConfig skillsConfig)
        {
            foreach (BuffConfigData buffConfigData in skillsConfig.buffs)
            {
                if (buffConfigData == null)
                {
                    continue;
                }

                if (!string.IsNullOrEmpty(buffConfigData.buffClassGuid))
                {
                    // guid 不为空 尝试更新 class_full_name
                    string classFullName = GetClassFullNameByGuid(buffConfigData.buffClassGuid);
                    if (string.IsNullOrEmpty(classFullName))
                    {
                        // 未命中时再次查询 GUID 映射；反向映射仅在下面 GUID 为空的分支使用。
                        string guid = GetClassFullNameByGuid(buffConfigData.buffClassFullName);
                        if (!string.IsNullOrEmpty(guid) && buffConfigData.buffClassGuid != guid)
                        {
                            buffConfigData.buffClassGuid = guid;
                            EditorUtility.SetDirty(buffConfigData);
                        }
                    }
                    else
                    {
                        if (buffConfigData.buffClassFullName != classFullName)
                        {
                            buffConfigData.buffClassFullName = classFullName;
                            EditorUtility.SetDirty(buffConfigData);
                        }
                    }


                }
                else if (string.IsNullOrEmpty(buffConfigData.buffClassGuid) && !string.IsNullOrEmpty(buffConfigData.buffClassFullName))
                {
                    // guid 是空 但是 class_full_name 不为空 尝试更新guid
                    string guid = GetGuidByClassFullName(buffConfigData.buffClassFullName);
                    if (string.IsNullOrEmpty(guid))
                    {
                        continue;
                    }

                    buffConfigData.buffClassGuid = guid;
                    EditorUtility.SetDirty(buffConfigData);
                }
            }
        }

        /// <summary>从项目脚本重建 BuffBase 派生类的 GUID 与类型全名双向映射。</summary>
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

                if (type.IsSubclassOf(typeof(BuffBase)))
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

        /// <summary>从反向映射查找类型对应的脚本 GUID，未找到时返回空字符串。</summary>
        private static string GetGuidByClassFullName(string fullName)
        {
            if (_classFullNameGuid.ContainsKey(fullName))
            {
                return _classFullNameGuid[fullName];
            }

            return string.Empty;
        }

        /// <summary>从 GUID 映射查找脚本类型全名，未找到时返回空字符串。</summary>
        private static string GetClassFullNameByGuid(string guid)
        {
            if (_guidClassFullName.ContainsKey(guid))
            {
                return _guidClassFullName[guid];
            }

            return string.Empty;
        }

        /// <summary>扫描 BuffConfigData 及其派生类型并返回共用列表，不过滤抽象类型。</summary>
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

                if (type.IsSubclassOf(typeof(BuffConfigData)) || type == typeof(BuffConfigData))
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
