using YLFramework.Foundation;
using YLFramework.Attributes;
#if UNITY_EDITOR

using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using YLFramework.Skill;

namespace YLFramework.Editor.Skill
{
    /// <summary>跨全部源技能配表校验 ID 占用，并在有效改动后同步条目资产名称。</summary>
    public class CustomSkillConfigDataIdGui : CustomPropertyGui
    {

        /// <summary>创建 ID 绘制器，请求接入 Inspector 在值改变后重载技能树。</summary>
        public CustomSkillConfigDataIdGui() : base(true) { }

        /// <summary>延迟提交未占用的 ID，应用字段修改后请求资产改名并清空父配表查找缓存。</summary>
        public override void OnPropertyGui(SerializedProperty serializedProperty)
        {

            EditorGUILayout.LabelField(string.Empty, "技能id(唯一标识,不可重复)", "BoldLabel");
            int id = serializedProperty.intValue;
            EditorGUI.BeginChangeCheck();

            id = EditorGUILayout.DelayedIntField("Id", id);

            if (EditorGUI.EndChangeCheck())
            {
                SkillConfigData skillData = null;

                // 判断方式不需要调整，这个是所有的配置都不能重复
                List<SkillsConfig> allConfig = GetAllConfigs();

                foreach (var item in allConfig)
                {
                    skillData = item.GetSkillConfigData(id);
                    if (skillData != null)
                    {
                        break;
                    }
                }

                if (skillData == null)
                {
                    serializedProperty.intValue = id;
                    serializedProperty.serializedObject.ApplyModifiedProperties();
                    SkillConfigData skillConfigData = serializedProperty.serializedObject.targetObject as SkillConfigData;
                    if (skillConfigData != null)
                    {
                        skillConfigData.Rename();
                    }

                    if (skillConfigData != null && skillConfigData.parent != null)
                    {
                        skillConfigData.parent.ClearCache();
                    }
                }
                else
                {
                    string message = string.Format("技能id:{0}已被占用,请使用其他数字来作为id!", id);
                    EditorWindow.focusedWindow.ShowNotification(new GUIContent(message));
                }
            }

        }


        /// <summary>扫描并返回新的可加载技能源配表列表，用于全局 ID 占用检查。</summary>
        internal static List<SkillsConfig> GetAllConfigs()
        {
            List<SkillsConfig> allConfigs = new List<SkillsConfig>();

            string[] guids = AssetDatabase.FindAssets(string.Format("t:{0}", typeof(SkillsConfig).FullName));
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

                SkillsConfig config = AssetDatabase.LoadAssetAtPath<SkillsConfig>(assetpath);
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
