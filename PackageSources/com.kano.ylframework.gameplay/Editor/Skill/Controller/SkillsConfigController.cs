using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.PackageManager.UI;
using UnityEngine;
using YLFramework.Foundation;
using YLFramework.Attributes;
using YLFramework.Editor.Common;
using YLFramework.Skill;

namespace YLFramework.Editor.Skill
{

    /// <summary>检索技能源配表，维护条目创建、ID 分配、受管路径和副本名称。</summary>
    public class SkillsConfigController
    {

        private const string SkillConfigDataPath = "Assets/YLFrameworkData/Skill/Skills/{0}";

        private static List<SkillsConfig> _allConfigs;


        /// <summary>重新扫描技能配表资产，返回会在下次调用时被清空并重填的共享列表。</summary>
        public static List<SkillsConfig> GetAllConfigs()
        {
            if (_allConfigs == null)
            {
                _allConfigs = new List<SkillsConfig>();
            }

            _allConfigs.Clear();

            string[] guids = AssetDatabase.FindAssets(string.Format("t:{0}", typeof(SkillsConfig).FullName));
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

                SkillsConfig config = AssetDatabase.LoadAssetAtPath<SkillsConfig>(assetpath);
                if (config == null)
                {
                    continue;
                }

                _allConfigs.Add(config);
            }

            return _allConfigs;
        }

        /// <summary>以指定数据类型创建技能条目；未提供配表时返回 -1。</summary>
        public static int CreateSkillConfigData(Type type, SkillsConfig skillsConfig)
        {
            if (skillsConfig == null)
            {
                return -1;
            }

            SkillConfigData skill = ScriptableObject.CreateInstance(type) as SkillConfigData;
            return CreateSkillConfigData(skill, skillsConfig);
        }

        /// <summary>为技能对象分配 ID、名称与配表归属，创建独立资产并登记，标脏后保存和刷新资产库。</summary>
        /// <remarks>未取得受管路径时返回 -1；各项资产操作顺序执行，不提供整批 Undo 或原子回滚。</remarks>
        public static int CreateSkillConfigData(SkillConfigData skillConfigData, SkillsConfig skillsConfig)
        {
            string path = GetSkillConfigDataPath(skillsConfig);

            if (string.IsNullOrEmpty(path))
            {
                Debug.LogErrorFormat("技能创建失败,未查询到技能配表路径:{0}", skillsConfig.name);
                return -1;
            }

            int id = GenerateId(skillsConfig);
            skillConfigData.id = id;
            skillConfigData.skillName = string.Format("技能:{0}", id);
            skillConfigData.parent = skillsConfig;

            CreateAsset(skillConfigData, path, false);

            skillsConfig.skills.Add(skillConfigData);
            EditorUtility.SetDirty(skillsConfig);
            skillsConfig.ClearCache();
            skillConfigData.Rename();
            EditorUtility.SetDirty(skillConfigData);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            return id;
        }

        /// <summary>返回超过当前配表起始 ID 和已有最大技能 ID 的下一个值，不核查其他配表。</summary>
        public static int GenerateId(SkillsConfig config)
        {
            int maxId = config.startId;

            foreach (var item in config.skills)
            {
                if (item.id > maxId)
                {
                    maxId = item.id;
                }
            }

            maxId++;

            return maxId;
        }

        /// <summary>由已保存配表的资产 GUID 推导技能条目受管目录。</summary>
        public static string GetSkillConfigDataPath(SkillsConfig skillsConfig)
        {
            string assetPath = AssetDatabase.GetAssetPath(skillsConfig);
            string guid = AssetDatabase.AssetPathToGUID(assetPath);
            return GetSkillConfigDataPath(guid);
        }

        /// <summary>按配表 GUID 生成条目目录；GUID 为空时返回空字符串。</summary>
        public static string GetSkillConfigDataPath(string guid)
        {
            if (string.IsNullOrEmpty(guid))
            {
                return string.Empty;
            }

            return string.Format(SkillConfigDataPath, guid);
        }

        /// <summary>必要时创建目录并以类型名创建不重名资产，总会选中对象，ping 仅控制高亮定位。</summary>
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

        /// <summary>为副本生成带数字后缀且不与当前配表技能名称重复的名称。</summary>
        public static string GetCopyName(string skillName, SkillsConfig skillsConfig)
        {

            int count = 1;

            if (Regex.IsMatch(skillName, "\\([0-9]+\\)$"))
            {
                for (int i = skillName.Length - 1; i >= 0; i--)
                {
                    if (skillName[i] == '(')
                    {
                        string c = skillName.Substring(i + 1, skillName.Length - i - 2);
                        count = int.Parse(c);

                        skillName = skillName.Substring(0, i);
                        break;
                    }
                }
            }

            skillName = skillName.TrimEnd();

            string name = string.Empty;

            do
            {
                name = string.Format("{0} ({1})", skillName, count);
                count++;
            } while (IsContainsBuffName(name, skillsConfig));

            return name;
        }

        /// <summary>按名称相等比较检查配表是否已包含该技能名称。</summary>
        private static bool IsContainsBuffName(string skillName, SkillsConfig skillsConfig)
        {

            foreach (var item in skillsConfig.skills)
            {
                if (item.skillName == skillName)
                {
                    return true;
                }
            }

            return false;
        }

    }
}
