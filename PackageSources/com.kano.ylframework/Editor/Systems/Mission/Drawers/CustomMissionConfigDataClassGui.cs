using YLFramework.Foundation;
using YLFramework.Attributes;
#if UNITY_EDITOR

using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using YLFramework.Mission;


namespace YLFramework.Editor.Mission
{
    /// <summary>为任务定义选择运行脚本，维护类型全名与脚本 GUID，并提供打开和定位入口。</summary>
    public class CustomMissionConfigDataClassGui : CustomPropertyGui
    {

        private string _currentMissionTypeClassFullname;
        private Type _currentMissionType;
        private GUIContent _classTypeContent;
        private SerializedProperty _missionTypeGuidProperty;

        /// <summary>解析并显示当前任务类型，菜单切换时同步 GUID 与全名；选择脚本后应用并保存资产。</summary>
        public override void OnPropertyGui(SerializedProperty classFullNameProperty)
        {
            _missionTypeGuidProperty = classFullNameProperty.serializedObject.FindProperty("missionTypeGuid");
            GUILayout.Space(10);


            if (_currentMissionTypeClassFullname != classFullNameProperty.stringValue)
            {
                _currentMissionType = AssemblyTool.GetType(classFullNameProperty.stringValue);
                _currentMissionTypeClassFullname = classFullNameProperty.stringValue;

            }

            string message = _currentMissionType == null ? "无" : _currentMissionType.Name;

            if (_currentMissionType != null && !string.IsNullOrEmpty(_currentMissionType.Namespace))
            {
                message = string.Format("{0}({1})", message, _currentMissionType.Namespace);
            }

            if (_classTypeContent == null)
            {
                _classTypeContent = new GUIContent(EditorGUIUtility.IconContent("d_cs Script Icon"));
            }

            _classTypeContent.text = message;

            GUILayout.BeginHorizontal();

            GUILayout.Label("任务脚本");

            if (EditorGUILayout.DropdownButton(_classTypeContent, FocusType.Passive))
            {
                List<MonoScript> scripts = GetAllMissionTypes();

                GenericMenu generic = new GenericMenu();

                generic.AddItem(new GUIContent("无"), false, () =>
                {
                    _missionTypeGuidProperty.stringValue = string.Empty;
                    classFullNameProperty.stringValue = string.Empty;

                    classFullNameProperty.serializedObject.ApplyModifiedProperties();
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

                    bool isOn = classFullNameProperty.stringValue == item.GetClass().FullName;

                    generic.AddItem(content, isOn, () =>
                    {

                        _missionTypeGuidProperty.stringValue = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(item));
                        classFullNameProperty.stringValue = item.GetClass().FullName;
                        classFullNameProperty.serializedObject.ApplyModifiedProperties();

                        AssetDatabase.SaveAssets();
                    });
                }

                generic.ShowAsContext();
            }


            EditorGUI.BeginDisabledGroup(string.IsNullOrEmpty(_missionTypeGuidProperty.stringValue));

            if (GUILayout.Button("编辑", GUILayout.Width(50)))
            {
                string guid = GetGuid(_missionTypeGuidProperty.stringValue, classFullNameProperty.stringValue);
                OpenAsset(guid);
            }

            if (GUILayout.Button("选中", GUILayout.Width(50)))
            {
                string guid = GetGuid(_missionTypeGuidProperty.stringValue, classFullNameProperty.stringValue);
                SelectAsset(guid);
            }

            EditorGUI.EndDisabledGroup();

            GUILayout.EndHorizontal();
        }

        /// <summary>扫描 MissionBase 派生类脚本作为候选，不检查抽象性或公共无参构造器。</summary>
        public static List<MonoScript> GetAllMissionTypes()
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

                if (type.IsSubclassOf(typeof(MissionBase)))
                {
                    states.Add(script);
                }
            }

            return states;
        }

        /// <summary>现有 GUID 无效时按类型全名扫描脚本，未找到替代项则原样返回 GUID。</summary>
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


        /// <summary>判断 GUID 是否能解析到可加载资产，不核验其类型与任务脚本是否一致。</summary>
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

        /// <summary>解析脚本 GUID 的资产路径，并请求 Unity 打开该资产。</summary>
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


        /// <summary>按 GUID 定位资产，设置当前编辑器选择并高亮。</summary>
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
