using YLFramework.Foundation;
using YLFramework.Attributes;
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using YLFramework.Skill;

namespace YLFramework.Editor.Skill
{

    /// <summary>为技能定义选择运行脚本，同步类型全名与 GUID，并提供脚本打开和定位入口。</summary>
    public class CustomSkillConfigDataSkillClassGui : CustomPropertyGui
    {


        //private SkillConfigData currentSkillConfigData;

        private string _currentSkillClassFullName;

        private Type _currentSkillClassType;

        private SerializedProperty _serializedProperty = null;

        private GUIContent _classTypeContent = null;

        /// <summary>解析并显示当前技能类型，选择候选脚本时同步 GUID 与全名，应用修改并保存资产。</summary>
        public override void OnPropertyGui(SerializedProperty skillClassFullNameProperty)
        {
            if (this._serializedProperty != skillClassFullNameProperty)
            {
                this._serializedProperty = skillClassFullNameProperty;
            }



            if (_currentSkillClassFullName != skillClassFullNameProperty.stringValue)
            {
                _currentSkillClassType = AssemblyTool.GetType(skillClassFullNameProperty.stringValue);
                _currentSkillClassFullName = skillClassFullNameProperty.stringValue;
            }


            string message = _currentSkillClassType == null ? "无" : _currentSkillClassType.Name;

            if (_currentSkillClassType != null && !string.IsNullOrEmpty(_currentSkillClassType.Namespace))
            {
                message = string.Format("{0}({1})", message, _currentSkillClassType.Namespace);
            }

            if (_classTypeContent == null)
            {
                _classTypeContent = new GUIContent(EditorGUIUtility.IconContent("d_cs Script Icon"));
            }

            _classTypeContent.text = message;

            GUILayout.BeginHorizontal();

            GUILayout.Label("技能脚本");

            if (EditorGUILayout.DropdownButton(_classTypeContent, FocusType.Passive))
            {
                List<MonoScript> scripts = GetAllStatesType();

                if (scripts.Count != 0)
                {
                    GenericMenu generic = new GenericMenu();

                    foreach (var item in scripts)
                    {
                        GUIContent content = new GUIContent(EditorGUIUtility.IconContent("d_cs Script Icon"));
                        content.text = item.GetClass().Name;
                        if (!string.IsNullOrEmpty(item.GetClass().Namespace))
                        {
                            content.text = string.Format("{0}({1})", content.text, item.GetClass().Namespace);
                        }

                        bool isOn = skillClassFullNameProperty.stringValue == item.GetClass().FullName;

                        generic.AddItem(content, isOn, () =>
                        {
                            string guid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(item));

                            if (string.IsNullOrEmpty(guid))
                            {
                                return;
                            }

                            SerializedProperty skillClassGuidProperty = skillClassFullNameProperty.serializedObject.FindProperty("skillClassGuid");

                            skillClassGuidProperty.stringValue = guid;
                            skillClassFullNameProperty.stringValue = item.GetClass().FullName;
                            skillClassFullNameProperty.serializedObject.ApplyModifiedProperties();
                            AssetDatabase.SaveAssets();
                        });
                    }

                    generic.ShowAsContext();
                }
                else
                {
                    string m = string.Format("未在项目中发现技能脚本,请创建脚本并继承自{0}用来处理技能逻辑!", typeof(SkillBase).FullName);
                    Debug.LogWarning(m);
                    EditorWindow.focusedWindow.ShowNotification(new GUIContent(m));
                }

            }

            EditorGUI.BeginDisabledGroup(_currentSkillClassType == null);

            if (GUILayout.Button("编辑", GUILayout.Width(50)))
            {
                SerializedProperty skillClassGuidProperty = skillClassFullNameProperty.serializedObject.FindProperty("skillClassGuid");


                string guid = GetGuid(skillClassGuidProperty.stringValue, skillClassFullNameProperty.stringValue);

                OpenAsset(guid);
            }

            if (GUILayout.Button("选中", GUILayout.Width(50)))
            {
                SerializedProperty skillClassGuidProperty = skillClassFullNameProperty.serializedObject.FindProperty("skillClassGuid");

                string guid = GetGuid(skillClassGuidProperty.stringValue, skillClassFullNameProperty.stringValue);
                SelectAsset(guid);
            }

            EditorGUI.EndDisabledGroup();

            GUILayout.EndHorizontal();

        }

        /// <summary>扫描 SkillBase 派生类脚本，不排除抽象类型，也不检查公共无参构造器。</summary>
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

                if (type.IsSubclassOf(typeof(SkillBase)))
                {
                    states.Add(script);
                }
            }

            return states;
        }


        /// <summary>已有 GUID 无效时按类型全名扫描替代脚本，未找到则原样返回 GUID。</summary>
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


        /// <summary>判断 GUID 能否解析到可加载资产，不核对其是否为匹配的技能脚本。</summary>
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

        /// <summary>解析非空脚本 GUID 对应路径并请求打开资产。</summary>
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


        /// <summary>按脚本 GUID 设置编辑器选择并高亮对应资产。</summary>
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
