using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using YLFramework.Excel;
using YLFramework.Foundation;
using YLFramework.Attributes;
using YLFramework.Buff;
using YLFramework.Editor.Common;

namespace YLFramework.Editor.Buff
{

    /// <summary>编辑 Buff 配表的显示名与数据类型绑定，并提供专用配表窗口入口。</summary>
    [CustomEditor(typeof(BuffsConfig))]
    public class BuffsConfigInspector : UnityEditor.Editor
    {

        private List<SerializedProperty> _serializedProperties = null;

        private GUIStyle _labelStyle;

        /// <summary>缓存当前配表的序列化属性，供后续绘制使用。</summary>
        private void OnEnable()
        {
            _serializedProperties = serializedObject.GetAllSerializedProperty();
        }

        /// <summary>仅开放显示名与类型选择，其余配表字段只读展示，并应用序列化修改。</summary>
        public override void OnInspectorGUI()
        {
            serializedObject.Update();


            foreach (var item in _serializedProperties)
            {
                if (item.name == "typeScriptGuid")
                {
                    continue;
                }

                EditorGUI.BeginDisabledGroup(item.name != "displayName");
                EditorGUILayout.PropertyField(item);
                EditorGUI.EndDisabledGroup();
            }

            OnGuiDataType();


            if (GUILayout.Button("编辑Buff配表"))
            {
                OpenBuffsConfig(target as BuffsConfig);
            }

            serializedObject.ApplyModifiedProperties();




        }


        /// <summary>显示 Buff 配表窗口并切换到指定配表，null 时不处理。</summary>
        public static void OpenBuffsConfig(BuffsConfig buffsConfig)
        {
            if (buffsConfig == null)
            {
                return;
            }

            BuffConfigsWindow window = EditorWindow.GetWindow<BuffConfigsWindow>();
            window.Show();
            string assetPath = AssetDatabase.GetAssetPath(buffsConfig);
            window.SetCurrentConfigs(buffsConfig);
            //BuffConfigsWindow.SelectGUID = AssetDatabase.AssetPathToGUID(asset_path);
        }



        #region 配置类型

        private GUIContent _classTypeContent;

        private List<MonoScript> _configDatas;

        private GUIStyle _dropdownStyle;

        private GUIStyle _labelStyle2;

        // 选择配表创建定义时使用的数据基类并保存脚本 GUID，另提供脚本打开与定位入口。
        private void OnGuiDataType()
        {
            BuffsConfig skillsConfig = target as BuffsConfig;

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
                if (t != null && (t.IsSubclassOf(typeof(BuffConfigData)) || t == typeof(BuffConfigData)))
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


            if (_labelStyle2 == null)
            {
                _labelStyle2 = new GUIStyle("WordWrappedLabel");
                _labelStyle2.normal.textColor = Color.grey;
                _labelStyle2.fontSize = 11;
            }

            GUILayout.Label("说明:如果数据类型为空,则表示该配表中可以创建任意类型的数据。如果不为空,该配表则仅能创建您指定的类型及其子类型的数据!", _labelStyle2);


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

        /// <summary>将配置类型格式化为名称和可选的灰色命名空间。</summary>
        private string GetDisplayName(Type type)
        {

            if (string.IsNullOrEmpty(type.Namespace))
            {
                return type.Name;
            }

            return string.Format("{0}<color=grey>({1})</color>", type.Name, type.Namespace);
        }

        /// <summary>扫描 BuffConfigData 的派生脚本，重填本检查器复用的候选列表。</summary>
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

                if (type.IsSubclassOf(typeof(BuffConfigData)))
                {
                    _configDatas.Add(script);
                }
            }

            return _configDatas;
        }

        /// <summary>按 GUID 打开资产；GUID 或解析路径为空时不处理。</summary>
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

        /// <summary>按 GUID 选中并高亮资产；GUID 或解析路径为空时不处理。</summary>
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
