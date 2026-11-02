using YLFramework.Foundation;
using YLFramework.Attributes;
#if UNITY_EDITOR
using System.Collections;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using YLFramework.Buff;

namespace YLFramework.Editor.Buff
{


    /// <summary>延迟编辑 Buff ID，查询全项目配表占用并同步条目资产命名。</summary>
    public class CustomBuffConfigDataIdGui : CustomPropertyGui
    {


        /// <summary>启用字段变化后重载配表树的通知。</summary>
        public CustomBuffConfigDataIdGui() : base(true) { }


        /// <summary>确认 ID 未被配表查询命中后应用修改、重命名条目并清除所属表缓存。</summary>
        public override void OnPropertyGui(SerializedProperty serializedProperty)
        {
            EditorGUILayout.LabelField(string.Empty, "Buff id(唯一标识,不可重复)", "BoldLabel");
            int id = serializedProperty.intValue;
            EditorGUI.BeginChangeCheck();
            id = EditorGUILayout.DelayedIntField("Buff_Id", id);
            if (EditorGUI.EndChangeCheck())
            {

                BuffConfigData buffData = null;

                foreach (var item in GetAllConfigs())
                {
                    buffData = item.GetBuffData(id);
                    if (buffData != null)
                    {
                        break;
                    }
                }


                if (buffData == null)
                {
                    serializedProperty.intValue = id;
                    serializedProperty.serializedObject.ApplyModifiedProperties();
                    BuffConfigData buffConfigData = serializedProperty.serializedObject.targetObject as BuffConfigData;
                    if (buffConfigData != null)
                    {
                        buffConfigData.Rename();
                    }

                    if (buffConfigData != null && buffConfigData.parent != null)
                    {
                        buffConfigData.parent.ClearCache();
                    }
                }
                else
                {
                    string message = string.Format("Buffid:{0}已被占用,请使用其他数字来作为id!", id);
                    EditorWindow.focusedWindow.ShowNotification(new GUIContent(message));
                }
            }
        }

        /// <summary>扫描并返回项目内可加载的全部 Buff 配表，用于 ID 占用查询。</summary>
        public static List<BuffsConfig> GetAllConfigs()
        {
            List<BuffsConfig> allConfigs = new List<BuffsConfig>();

            string filter = string.Format("t:{0}", typeof(BuffsConfig).FullName);

            string[] guids = AssetDatabase.FindAssets(filter);
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

                BuffsConfig config = AssetDatabase.LoadAssetAtPath<BuffsConfig>(assetpath);
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
