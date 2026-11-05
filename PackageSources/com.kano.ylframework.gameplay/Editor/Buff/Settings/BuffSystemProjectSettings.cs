using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;
using YLFramework.Foundation;
using YLFramework.Attributes;
using YLFramework.Editor.Common;

namespace YLFramework.Editor.Buff
{

    /// <summary>在 Project Settings 中编辑 Buff 效果分类，并展示其余只读设置。</summary>
    public class BuffSettingsProvider : SettingsProvider
    {

        private static BuffSettingsProvider _instance;

        /// <summary>按需创建的项目级 Buff 设置页实例。</summary>
        public static BuffSettingsProvider Instance
        {
            get
            {
                if (_instance == null)
                {
                    _instance = new BuffSettingsProvider("YLFramework/Buff", SettingsScope.Project);
                }

                return _instance;
            }
        }

        /// <summary>为给定路径、范围与搜索词构造设置页。</summary>
        /// <param name="path">设置窗口中的页面路径。</param>
        /// <param name="scopes">设置页所属范围。</param>
        /// <param name="keywords">供设置窗口搜索的关键词，可为 null。</param>
        public BuffSettingsProvider(string path, SettingsScope scopes, IEnumerable<string> keywords = null) : base(path, scopes, keywords)
        {
        }

        private SerializedObject _serializedObject;

        private List<SerializedProperty> _serializedProperties;

        /// <summary>绑定当前设置对象并应用界面修改，发生文件冲突时显示迁移提示而不覆盖文件。</summary>
        public override void OnGUI(string searchContext)
        {
            try
            {
                var settings = BuffSystemSettings.Instance;
                if (_serializedObject != null && _serializedObject.targetObject != settings)
                {
                    Reload();
                }

                if (_serializedObject == null)
                {
                    _serializedObject = new SerializedObject(settings);
                }

                if (_serializedProperties == null)
                {
                    _serializedProperties = _serializedObject.GetAllSerializedProperty();
                }

                _serializedObject.Update();

                EditorGUI.BeginChangeCheck();

                foreach (var item in _serializedProperties)
                {
                    if (item.name == "allEffectDatas")
                    {
                        continue;
                    }

                    if (item.name == "allEffectTypes")
                    {
                        OnTypeGui(item);
                        continue;
                    }

                    EditorGUI.BeginDisabledGroup(true);

                    EditorGUILayout.PropertyField(item);

                    EditorGUI.EndDisabledGroup();
                }

                _serializedObject.ApplyModifiedProperties();
                if (EditorGUI.EndChangeCheck())
                {
                    BuffSystemSettings.Save();
                }
            }
            catch (IOException exception)
            {
                Reload();
                EditorGUILayout.HelpBox(exception.Message + "\nMigration menu: " + BuffSettingsMigration.MenuPath +
                    ". Resolve any old/new file conflict without overwriting either file.", MessageType.Error);
                return;
            }
        }

        private ReorderableList _reorderableListType;

        /// <summary>释放旧设置的序列化包装与列表，下一次绘制重新绑定。</summary>
        internal void Reload()
        {
            _serializedProperties = null;
            _reorderableListType = null;
            _serializedObject?.Dispose();
            _serializedObject = null;
        }



        /// <summary>以不可拖动的列表编辑效果分类名称，并接入新增分类操作。</summary>
        private void OnTypeGui(SerializedProperty property)
        {

            if (_reorderableListType == null)
            {
                _reorderableListType = new ReorderableList(property.serializedObject, property);
                _reorderableListType.draggable = false;
                _reorderableListType.drawElementCallback += ElementCallbackDelegate;
                _reorderableListType.onAddCallback += AddCallbackDelegate;
                _reorderableListType.drawHeaderCallback += HeaderCallbackDelegate;
            }

            _reorderableListType.DoLayoutList();
        }


        /// <summary>绘制单行分类编号与名称，编号只读、名称可编辑。</summary>
        private void ElementCallbackDelegate(Rect rect, int index, bool isActive, bool isFocused)
        {
            if (index < 0 || index >= _reorderableListType.serializedProperty.arraySize)
            {
                return;
            }

            SerializedProperty property = _reorderableListType.serializedProperty.GetArrayElementAtIndex(index);

            SerializedProperty type = property.FindPropertyRelative("type");
            SerializedProperty name = property.FindPropertyRelative("name");

            rect.width = rect.width / 2;
            EditorGUI.BeginDisabledGroup(true);
            EditorGUI.IntField(rect, type.intValue);
            EditorGUI.EndDisabledGroup();

            rect.x += rect.width;
            name.stringValue = EditorGUI.TextField(rect, name.stringValue);

        }


        /// <summary>在末尾插入分类并以槽位数加一赋值编号，不扫描已有编号去重。</summary>
        private void AddCallbackDelegate(ReorderableList list)
        {
            int index = list.serializedProperty.arraySize;
            list.serializedProperty.InsertArrayElementAtIndex(index);
            SerializedProperty property = list.serializedProperty.GetArrayElementAtIndex(index);
            property.FindPropertyRelative("type").intValue = index + 1;
        }

        /// <summary>绘制分类列表的编号与名称列头。</summary>
        private void HeaderCallbackDelegate(Rect rect)
        {
            rect.width /= 2;
            GUI.Label(rect, "类型");

            rect.x += rect.width;
            GUI.Label(rect, "名称");
        }

    }



    /// <summary>向 Unity 注册 Buff 的项目设置页。</summary>
    public static class BuffSystemProjectSettings
    {

        /// <summary>返回供 Unity 设置窗口使用的 Buff 设置页。</summary>
        [SettingsProvider]
        public static SettingsProvider Settings()
        {
            return BuffSettingsProvider.Instance;
        }

    }




}
