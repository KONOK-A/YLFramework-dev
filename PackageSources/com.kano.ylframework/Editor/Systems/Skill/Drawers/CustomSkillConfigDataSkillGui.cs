using YLFramework.Foundation;
using YLFramework.Attributes;
#if UNITY_EDITOR
using System.Collections.Generic;
#if UNITY_EDITOR
using UnityEditor;
using UnityEditorInternal;
#endif
using UnityEngine;
using YLFramework.Skill;

namespace YLFramework.Editor.Skill
{
    /// <summary>编辑技能并行释放 ID 列表，候选限于当前源配表，排除自身和已列入的技能。</summary>
    public class CustomSkillConfigDataSkillGui : CustomPropertyGui
    {

        private bool _showSkill = true;

        private ReorderableList _skillList = null;

        private SerializedProperty _serializedProperty;

        private Dictionary<int, GUIContent> _sameContents = new Dictionary<int, GUIContent>();

        private SkillsConfig _currentSkillConfig;
        private SkillConfigData _currentSkillConfigData;

        private List<SkillConfigData> _skills = new List<SkillConfigData>();

        /// <summary>字段上下文变化时重建列表绑定，绘制可折叠的并行技能列表。</summary>
        public override void OnPropertyGui(SerializedProperty simultaneousSkillIdsProperty)
        {
            if (this._serializedProperty != simultaneousSkillIdsProperty)
            {
                _skillList = null;
                this._serializedProperty = simultaneousSkillIdsProperty;

                _currentSkillConfigData = simultaneousSkillIdsProperty.serializedObject.targetObject as SkillConfigData;
                _currentSkillConfig = _currentSkillConfigData.parent;
            }


            EditorGUILayout.LabelField(string.Empty, "能够同时释放的技能(默认所有的技能都不能同时释放)", "BoldLabel");
            _showSkill = EditorGUILayout.Foldout(_showSkill, "CanbeReleasedAtTheSameTime");
            if (_showSkill)
            {
                if (_skillList == null)
                {
                    _skillList = new ReorderableList(simultaneousSkillIdsProperty.serializedObject, simultaneousSkillIdsProperty, true, false, true, true);
                    _skillList.drawElementCallback += OnDrawRow;
                    _skillList.onRemoveCallback += OnRemove;
                    _skillList.headerHeight = 1;
                }
                GUILayout.BeginHorizontal();

                GUILayout.Space(15);
                GUILayout.BeginVertical();

                EditorGUI.BeginChangeCheck();
                _skillList.DoLayoutList();

                GUILayout.EndVertical();
                GUILayout.EndHorizontal();
            }
        }


        /// <summary>绘制一项技能 ID 及候选菜单，菜单选择后写入并应用序列化修改。</summary>
        private void OnDrawRow(Rect rect, int index, bool isActive, bool isFocused)
        {
            if (index < 0 || index >= _serializedProperty.arraySize)
            {
                return;
            }

            SerializedProperty skillIdProperty = _serializedProperty.GetArrayElementAtIndex(index);

            int v = skillIdProperty.intValue;

            if (!_sameContents.ContainsKey(index))
            {
                _sameContents.Add(index, new GUIContent());
            }

            GUIContent content = _sameContents[index];

            SkillConfigData skillData = _currentSkillConfig.GetSkillConfigData(v);


            if (skillData == null)
            {
                content.text = "空";
            }
            else
            {
                content.text = string.Format("{0}({1})", skillData.skillName, skillData.id);
            }

            if (EditorGUI.DropdownButton(rect, content, FocusType.Passive))
            {
                GenericMenu menu = new GenericMenu();

                _skills.Clear();

                foreach (var item in _currentSkillConfig.skills)
                {
                    if (item == null)
                    {
                        continue;
                    }

                    if (item.id == _currentSkillConfigData.id)
                    {
                        continue;
                    }

                    if (_currentSkillConfigData.simultaneousSkillIds.Contains(item.id))
                    {
                        continue;
                    }

                    _skills.Add(item);
                }

                if (_skills.Count == 0)
                {
                    menu.AddDisabledItem(new GUIContent("无"), false);
                }
                else
                {
                    foreach (var item in _skills)
                    {
                        string display = string.Format("{0}({1})", item.skillName, item.id);
                        menu.AddItem(new GUIContent(display), false, () =>
                        {
                            skillIdProperty.intValue = item.id;
                            _serializedProperty.serializedObject.ApplyModifiedProperties();
                        });
                    }
                }

                menu.ShowAsContext();
            }
        }

        /// <summary>删除有效选中索引的技能 ID，并应用列表修改。</summary>
        private void OnRemove(ReorderableList list)
        {
            if (list.index < 0 || list.index >= list.serializedProperty.arraySize)
            {
                return;
            }

            int id = list.serializedProperty.GetArrayElementAtIndex(list.index).intValue;
            list.serializedProperty.DeleteArrayElementAtIndex(list.index);
            list.serializedProperty.serializedObject.ApplyModifiedProperties();
        }



    }

}

#endif
