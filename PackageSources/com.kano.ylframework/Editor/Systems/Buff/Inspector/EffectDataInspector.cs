using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using YLFramework.Foundation;
using YLFramework.Attributes;
using YLFramework.Buff;
using YLFramework.Editor.Common;


namespace YLFramework.Editor.Buff
{

    /// <summary>普通 Inspector 只读展示效果，效果窗口借用本检查器编辑定义及分类。</summary>
    [CustomEditor(typeof(EffectData))]
    public class EffectDataInspector : UnityEditor.Editor
    {

        private static EffectDataInspector _instance;

        /// <summary>供效果窗口复用的内嵌检查器实例。</summary>
        public static EffectDataInspector Instance
        {
            get
            {
                if (_instance == null)
                {
                    _instance = CreateInstance<EffectDataInspector>();
                }

                return _instance;
            }
        }

        private EffectData _currentEffectData;

        private SerializedObject _currentSerializedObject;

        private List<SerializedProperty> _currentSerializeProperty;

        private Vector2 _currentPosition;

        private EffectConfigsWindow _window;

        private string _effectName;


        private GUIStyle _styleDropdown;

        private GUIContent _typeContent;

        /// <summary>只读展示效果资产，并提供跳转到效果窗口的按钮。</summary>
        public override void OnInspectorGUI()
        {
            EditorGUI.BeginDisabledGroup(true);

            base.OnInspectorGUI();

            EditorGUI.EndDisabledGroup();

            if (GUILayout.Button("编辑效果"))
            {
                EffectData effectData = (EffectData)target;

                EffectConfigsWindow window = EditorWindow.GetWindow<EffectConfigsWindow>();
                window.CurrentEffectData = effectData;
                window.RefreshList();
            }
        }

        /// <summary>在效果窗口内编辑定义，ID、名称与业务分类使用专用控件。</summary>
        internal void OnInspectorGUI(EffectConfigsWindow window, EffectData data)
        {
            this._window = window;

            if (data != null)
            {
                if (_currentEffectData != data)
                {
                    _currentEffectData = data;
                    _currentSerializedObject = null;
                }

                if (_currentSerializedObject == null)
                {
                    _currentSerializedObject = new SerializedObject(this._currentEffectData);
                    _currentSerializeProperty = _currentSerializedObject.GetAllSerializedProperty();
                }


                _currentSerializedObject.Update();

                _currentPosition = GUILayout.BeginScrollView(_currentPosition);



                for (int i = 0; i < _currentSerializeProperty.Count; i++)
                {
                    SerializedProperty property = _currentSerializeProperty[i];

                    //if (property.displayName == "Script")
                    //    continue;

                    if (property.name == "id")
                    {
                        OnInspectorId();
                        continue;
                    }

                    if (property.name == "effectName")
                    {
                        OnInspectorName();
                        continue;
                    }

                    if (property.name == "type")
                    {
                        OnTypeInspector(property);
                        continue;

                    }

                    if (property.IsScript())
                    {
                        EditorGUI.BeginDisabledGroup(true);
                        EditorGUILayout.LabelField(string.Empty, "类型", "BoldLabel");
                        EditorGUILayout.PropertyField(property);
                        EditorGUI.EndDisabledGroup();
                    }
                    else
                    {
                        EditorGUILayout.PropertyField(property);
                    }

                }

                GUILayout.EndScrollView();

                _currentSerializedObject.ApplyModifiedProperties();
            }
            else
            {
                GUILayout.FlexibleSpace();
                EditorGUILayout.BeginHorizontal();
                GUILayout.FlexibleSpace();
                GUILayout.Label("空");
                GUILayout.FlexibleSpace();
                EditorGUILayout.EndHorizontal();
                GUILayout.FlexibleSpace();
            }
        }


        /// <summary>延迟提交效果名称，检查显示名占用后保存并重命名资产、刷新列表。</summary>
        private void OnInspectorName()
        {
            _effectName = _currentEffectData.effectName;
            EditorGUI.BeginChangeCheck();
            EditorGUILayout.LabelField(string.Empty, "名称", "BoldLabel");
            _effectName = EditorGUILayout.DelayedTextField("EffectName", _effectName);
            if (EditorGUI.EndChangeCheck())
            {
                foreach (var item in BuffSystemSettings.Instance.AllEffectDatas)
                {
                    if (!item)
                    {
                        continue;
                    }

                    if (item.effectName.Equals(_effectName))
                    {
                        _window.ShowNotification(new GUIContent("效果名称重复!"));
                        return;
                    }
                }

                _currentEffectData.effectName = _effectName;
                EditorUtility.SetDirty(_currentEffectData);
                AssetDatabase.SaveAssets();
                AssetDatabase.RenameAsset(AssetDatabase.GetAssetPath(_currentEffectData), _effectName);
                _window.RefreshList();
            }
        }

        /// <summary>拒绝零值与已占用 ID，合法修改直接写入资产并保存、刷新列表。</summary>
        private void OnInspectorId()
        {
            int id = _currentEffectData.id;
            EditorGUI.BeginChangeCheck();
            EditorGUILayout.LabelField(string.Empty, "唯一标识", "BoldLabel");
            id = EditorGUILayout.DelayedIntField("Id", id);
            if (EditorGUI.EndChangeCheck())
            {
                if (id == 0)
                {
                    _window.ShowNotification(new GUIContent("id不能为0!"));
                }
                else
                {
                    if (IsContains(id))
                    {
                        _window.ShowNotification(new GUIContent("效果Id重复!"));
                    }
                    else
                    {
                        _currentEffectData.id = id;
                        EditorUtility.SetDirty(_currentEffectData);
                        AssetDatabase.SaveAssets();
                        _window.RefreshList();
                    }

                }

            }
        }

        /// <summary>检查登记的效果资产中是否已有指定 ID。</summary>
        private bool IsContains(int id)
        {
            foreach (var item in BuffSystemSettings.Instance.AllEffectDatas)
            {
                if (!item)
                {
                    continue;
                }

                if (item.id.Equals(id))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>提供业务分类选择并应用序列化修改，另提供项目设置跳转。</summary>
        private void OnTypeInspector(SerializedProperty property)
        {

            if (_styleDropdown == null)
            {
                _styleDropdown = new GUIStyle("DropDown");
                _styleDropdown.richText = true;
            }

            if (_typeContent == null)
            {
                _typeContent = new GUIContent();
            }

            EditorGUILayout.LabelField(string.Empty, "类型", "BoldLabel");

            _typeContent.text = BuffSystemSettings.Instance.GetType(property.intValue);

            GUILayout.BeginHorizontal();

            EditorGUILayout.LabelField(string.Empty, "Type", GUILayout.Width(148));

            if (EditorGUILayout.DropdownButton(_typeContent, FocusType.Passive, _styleDropdown))
            {
                GenericMenu menu = new GenericMenu();

                foreach (var type in BuffSystemSettings.Instance.AllEffectType)
                {
                    GUIContent content = new GUIContent(BuffSystemSettings.Instance.GetType(type.type, false));

                    menu.AddItem(content, property.intValue == type.type, () =>
                    {
                        property.intValue = type.type;
                        property.serializedObject.ApplyModifiedProperties();
                    });
                }

                if (BuffSystemSettings.Instance.AllEffectType.Count > 0)
                {
                    menu.AddSeparator(string.Empty);
                }

                menu.AddItem(new GUIContent("编辑"), false, () =>
                {
                    EffectListTree.OpenProjectSettings("YLFramework/Buff");
                });

                menu.ShowAsContext();
            }


            GUILayout.EndHorizontal();

        }


    }

}
