using YLFramework.Foundation;
using YLFramework.Attributes;
#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using YLFramework.Buff;


namespace YLFramework.Editor.Buff
{
    /// <summary>为 Buff 定义选择行为脚本，同时维护类型全名与编辑器脚本 GUID。</summary>
    public class CustomBuffConfigDataClassGui : CustomPropertyGui
    {

        private string _currentBuffClassFullName;

        private Type _currentBuffClassType;

        private GUIContent _classTypeContent;

        /// <summary>绘制 Buff 脚本选择与定位入口，选中候选后应用绑定并保存资产。</summary>
        public override void OnPropertyGui(SerializedProperty buffClassFullNameProperty)
        {
            GUILayout.Space(10);

            //SerializedProperty buff_class_guid_property = buff_class_full_name_property.serializedObject.FindProperty("buffClassGuid");

            if (_currentBuffClassFullName != buffClassFullNameProperty.stringValue)
            {
                _currentBuffClassType = AssemblyTool.GetType(buffClassFullNameProperty.stringValue);
                _currentBuffClassFullName = buffClassFullNameProperty.stringValue;
            }

            string message = _currentBuffClassType == null ? "无" : _currentBuffClassType.Name;

            if (_currentBuffClassType != null && !string.IsNullOrEmpty(_currentBuffClassType.Namespace))
            {
                message = string.Format("{0}({1})", message, _currentBuffClassType.Namespace);
            }

            if (_classTypeContent == null)
            {
                _classTypeContent = new GUIContent(EditorGUIUtility.IconContent("d_cs Script Icon"));
            }

            _classTypeContent.text = message;

            GUILayout.BeginHorizontal();

            GUILayout.Label("Buff脚本");

            if (EditorGUILayout.DropdownButton(_classTypeContent, FocusType.Passive))
            {
                List<MonoScript> scripts = GetAllStatesType();

                if (scripts.Count != 0)
                {
                    GenericMenu generic = new GenericMenu();

                    generic.AddItem(new GUIContent("无"), false, () =>
                    {

                        SerializedProperty buffClassGuidProperty = buffClassFullNameProperty.serializedObject.FindProperty("buffClassGuid");

                        buffClassFullNameProperty.stringValue = string.Empty;
                        buffClassGuidProperty.stringValue = string.Empty;
                        buffClassGuidProperty.serializedObject.ApplyModifiedProperties();
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

                        bool isOn = buffClassFullNameProperty.stringValue == item.GetClass().FullName;

                        generic.AddItem(content, isOn, () =>
                        {
                            string guid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(item));

                            if (string.IsNullOrEmpty(guid))
                            {
                                return;
                            }

                            SerializedProperty buffClassGuidProperty = buffClassFullNameProperty.serializedObject.FindProperty("buffClassGuid");

                            buffClassGuidProperty.stringValue = guid;
                            buffClassFullNameProperty.stringValue = item.GetClass().FullName;
                            buffClassGuidProperty.serializedObject.ApplyModifiedProperties();

                            AssetDatabase.SaveAssets();
                        });
                    }

                    generic.ShowAsContext();
                }
                else
                {
                    string m = string.Format("未在项目中发现Buff脚本,请创建脚本并继承自{0}用来处理Buff逻辑!", typeof(BuffBase).FullName);
                    Debug.LogWarning(m);
                    EditorWindow.focusedWindow.ShowNotification(new GUIContent(m));
                }

            }

            EditorGUI.BeginDisabledGroup(_currentBuffClassType == null);

            if (GUILayout.Button("编辑", GUILayout.Width(50)))
            {
                SerializedProperty buffClassGuidProperty = buffClassFullNameProperty.serializedObject.FindProperty("buffClassGuid");

                string guid = GetGuid(buffClassGuidProperty.stringValue, buffClassFullNameProperty.stringValue);

                OpenAsset(guid);
            }

            if (GUILayout.Button("选中", GUILayout.Width(50)))
            {
                SerializedProperty buffClassGuidProperty = buffClassFullNameProperty.serializedObject.FindProperty("buffClassGuid");

                string guid = GetGuid(buffClassGuidProperty.stringValue, buffClassFullNameProperty.stringValue);

                SelectAsset(guid);
            }

            EditorGUI.EndDisabledGroup();

            GUILayout.EndHorizontal();
        }

        /// <summary>扫描 BuffBase 派生脚本，不包含基类，也不验证抽象类型或无参构造条件。</summary>
        public static List<MonoScript> GetAllStatesType()
        {
            List<MonoScript> states = new List<MonoScript>();

            string[] scripts = AssetDatabase.FindAssets("t:Script");

            foreach (var item in scripts)
            {
                string path = AssetDatabase.GUIDToAssetPath(item);
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
                    states.Add(script);
                }
            }

            return states;
        }

        /// <summary>已有 GUID 无效时按类型全名扫描脚本，未命中则保留原 GUID。</summary>
        private static string GetGuid(string guid, string classFullName)
        {
            if (!IsValid(guid) && !string.IsNullOrEmpty(classFullName))
            {
                string[] paths = AssetDatabase.FindAssets("t:Script");
                foreach (var item in paths)
                {
                    string assetPath = AssetDatabase.GUIDToAssetPath(item);
                    MonoScript script = AssetDatabase.LoadAssetAtPath<MonoScript>(assetPath);
                    if (script != null && script.GetClass() != null && script.GetClass().FullName.Equals(classFullName))
                    {
                        return item;
                    }
                }
            }

            return guid;
        }


        /// <summary>检查 GUID 能否解析为可加载资产，不要求该资产一定是 Buff 脚本。</summary>
        private static bool IsValid(string guid)
        {
            string assetPath = AssetDatabase.GUIDToAssetPath(guid);
            if (string.IsNullOrEmpty(assetPath))
            {
                return false;
            }

            if (AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath) == null)
            {
                return false;
            }

            return true;
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



    }

}

#endif
