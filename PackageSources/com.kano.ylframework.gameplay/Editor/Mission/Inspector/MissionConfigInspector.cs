using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using YLFramework.Excel;
using YLFramework.Foundation;
using YLFramework.Attributes;
using YLFramework.Editor.Common;
using YLFramework.Mission;

namespace YLFramework.Editor.Mission
{

    /// <summary>任务配表资产 Inspector，编辑显示名与数据类型限制，并提供专用窗口入口。</summary>
    [CustomEditor(typeof(MissionsConfig))]
    public class MissionConfigInspector : UnityEditor.Editor
    {

        private List<SerializedProperty> _serializedProperties = null;

        //private SerializedProperty m_Script = null;

        /// <summary>缓存当前配表的序列化属性，供 Inspector 逐项绘制。</summary>
        private void OnEnable()
        {
            _serializedProperties = serializedObject.GetAllSerializedProperty();
            //m_Script = serializedObject.FindProperty("m_Script");
        }


        /// <summary>只允许常规字段中的显示名编辑，另外绘制类型绑定并应用序列化修改。</summary>
        public override void OnInspectorGUI()
        {
            serializedObject.Update();


            foreach (var item in _serializedProperties)
            {
                //if (item.displayName == "Script") continue;
                if (item.name == "startId")
                {
                    continue;
                }

                if (item.name == "typeScriptGuid")
                {
                    continue;
                }

                EditorGUI.BeginDisabledGroup(item.name != "displayName");
                EditorGUILayout.PropertyField(item);
                EditorGUI.EndDisabledGroup();
            }

            OnGuiDataType();

            serializedObject.ApplyModifiedProperties();

            if (GUILayout.Button("编辑任务配表"))
            {
                OpenMissionsConfig(target as MissionsConfig);
            }



        }

        /// <summary>显示任务配表窗口并绑定指定源配表；空引用不处理。</summary>
        public static void OpenMissionsConfig(MissionsConfig missionsConfig)
        {
            if (missionsConfig == null)
            {
                return;
            }

            ConfigsWindow window = EditorWindow.GetWindow<ConfigsWindow>();
            window.Show();
            window.SetCurrentConfigs(missionsConfig);
        }



        #region 配置类型

        private GUIContent _classTypeContent;

        private List<MonoScript> _configDatas;

        private GUIStyle _dropdownStyle;

        private GUIStyle _labelStyle;

        /// <summary>显示并设置数据类型脚本 GUID，菜单修改会标脏、保存；不转换配表中已有条目。</summary>
        private void OnGuiDataType()
        {
            MissionsConfig skillsConfig = target as MissionsConfig;

            if (skillsConfig == null)
            {
                return;
            }

            GUILayout.Space(10);

            Type type = null;

            if (!string.IsNullOrEmpty(skillsConfig.typeScriptGuid))
            {
                string assetPath = AssetDatabase.GUIDToAssetPath(skillsConfig.typeScriptGuid);
                MonoScript script = AssetDatabase.LoadAssetAtPath<MonoScript>(assetPath);
                Type t = script ? script.GetClass() : null;
                if (t != null && (t.IsSubclassOf(typeof(MissionConfigData)) || t == typeof(MissionConfigData)))
                {
                    type = t;
                }
            }

            string message = type == null ? "空" : GetDisplayName(type);

            if (_classTypeContent == null)
            {
                _classTypeContent = new GUIContent(EditorGUIUtility.IconContent("d_cs Script Icon"));
            }

            _classTypeContent.text = message;

            GUILayout.BeginHorizontal();

            GUILayout.Label("数据类型");

            GUILayout.Space(50);

            if (_dropdownStyle == null)
            {
                _dropdownStyle = new GUIStyle("DropDown");
                _dropdownStyle.richText = true;
            }

            if (EditorGUILayout.DropdownButton(_classTypeContent, FocusType.Passive, _dropdownStyle, GUILayout.MinWidth(0)))
            {
                List<MonoScript> scripts = GetAllConfigDatas();

                GenericMenu generic = new GenericMenu();

                generic.AddItem(new GUIContent("空"), false, () =>
                {
                    skillsConfig.typeScriptGuid = string.Empty;
                    EditorUtility.SetDirty(skillsConfig);
                    AssetDatabase.SaveAssets();
                });

                generic.AddSeparator(string.Empty);

                foreach (var item in scripts)
                {
                    GUIContent content = new GUIContent(EditorGUIUtility.IconContent("d_cs Script Icon"));
                    content.text = item.GetClass().Name;
                    if (!string.IsNullOrEmpty(item.GetClass().Namespace))
                    {
                        content.text = string.Format("{0}({1})", content.text, item.GetClass().Namespace);
                    }

                    string guid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(item));

                    bool isOn = skillsConfig.typeScriptGuid == guid;

                    generic.AddItem(content, isOn, () =>
                    {
                        skillsConfig.typeScriptGuid = guid;
                        EditorUtility.SetDirty(skillsConfig);
                        AssetDatabase.SaveAssets();
                    });
                }

                generic.ShowAsContext();

            }

            GUILayout.EndHorizontal();


            if (_labelStyle == null)
            {
                _labelStyle = new GUIStyle("WordWrappedLabel");
                _labelStyle.normal.textColor = Color.grey;
                _labelStyle.fontSize = 11;
            }

            GUILayout.Label("说明:如果数据类型为空,则表示该配表中可以创建任意类型的数据。如果不为空,该配表则仅能创建您指定的类型及其子类型的数据!", _labelStyle);


            GUILayout.BeginHorizontal();


            EditorGUI.BeginDisabledGroup(type == null);

            GUILayout.FlexibleSpace();

            if (GUILayout.Button("编辑", GUILayout.Width(50)))
            {
                OpenAsset(skillsConfig.typeScriptGuid);
            }

            if (GUILayout.Button("选中", GUILayout.Width(50)))
            {
                SelectAsset(skillsConfig.typeScriptGuid);
            }

            EditorGUI.EndDisabledGroup();

            GUILayout.EndHorizontal();

            GUILayout.Space(10);

        }

        /// <summary>生成带灰色命名空间附注的数据类型显示名称。</summary>
        private string GetDisplayName(Type type)
        {

            if (string.IsNullOrEmpty(type.Namespace))
            {
                return type.Name;
            }

            return string.Format("{0}<color=grey>({1})</color>", type.Name, type.Namespace);
        }

        /// <summary>重新扫描 MissionConfigData 派生类脚本作为候选，不包含基础类型，也不排除抽象类。</summary>
        private List<MonoScript> GetAllConfigDatas()
        {
            if (_configDatas == null)
            {
                _configDatas = new List<MonoScript>();
            }

            _configDatas.Clear();

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

                if (type.IsSubclassOf(typeof(MissionConfigData)))
                {
                    _configDatas.Add(script);
                }
            }

            return _configDatas;
        }

        /// <summary>通过有效 GUID 解析路径并请求 Unity 打开对应资产。</summary>
        public static void OpenAsset(string guid)
        {
            if (string.IsNullOrEmpty(guid))
            {
                return;
            }

            string assetPath = AssetDatabase.GUIDToAssetPath(guid);
            if (string.IsNullOrEmpty(assetPath))
            {
                return;
            }

            AssetDatabase.OpenAsset(AssetDatabase.LoadAssetAtPath(assetPath, typeof(UnityEngine.Object)));
        }

        /// <summary>按 GUID 取得资产，设置编辑器选择并高亮定位。</summary>
        public static void SelectAsset(string guid)
        {
            if (string.IsNullOrEmpty(guid))
            {
                return;
            }

            string assetPath = AssetDatabase.GUIDToAssetPath(guid);
            if (string.IsNullOrEmpty(assetPath))
            {
                return;
            }

            UnityEngine.Object obj = AssetDatabase.LoadAssetAtPath(assetPath, typeof(UnityEngine.Object));
            Selection.activeObject = obj;
            EditorGUIUtility.PingObject(obj);
        }

        #endregion

    }
}
