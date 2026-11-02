using YLFramework.Foundation;
using YLFramework.Attributes;
#if UNITY_EDITOR

using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;
using YLFramework.Mission;


namespace YLFramework.Editor.Mission
{

    /// <summary>条件参数选择树，以配表参数索引作为行 ID，并直接编辑绑定条件的序列化字段。</summary>
    public class ParamPopupTreeView : TreeView
    {
        private MissionsConfig _config;
        private SerializedProperty _condition;
        private MissionConfigData _missionConfigData;

        private GUIStyle _labelStyle;

        private Dictionary<int, GUIContent> _contents = new Dictionary<int, GUIContent>();

        /// <summary>保存候选配表与目标条件字段，启用交替行背景。</summary>
        public ParamPopupTreeView(TreeViewState state, MissionsConfig controller, SerializedProperty condition) : base(state)
        {
            this._config = controller;
            this._condition = condition;

            showAlternatingRowBackgrounds = true;
        }

        /// <summary>按配表参数顺序构建候选行，以名称和描述供树搜索。</summary>
        protected override TreeViewItem BuildRoot()
        {

            TreeViewItem root = new TreeViewItem(-1, -1);

            if (_config != null)
            {
                for (int i = 0; i < _config.parameters.Count; i++)
                {
                    root.AddChild(new TreeViewItem(i, 0, string.Format("{0}{1}", _config.parameters[i].name, _config.parameters[i].description)));
                }
            }

            return root;
        }

        /// <summary>沿用基类的行构建及搜索过滤。</summary>
        protected override IList<TreeViewItem> BuildRows(TreeViewItem root)
        {
            return base.BuildRows(root);
        }

        /// <summary>选中参数后写入名称，数值类型默认大于、布尔默认等于，再应用条件修改，不重置目标值。</summary>
        protected override void SingleClickedItem(int id)
        {
            base.SingleClickedItem(id);

            if (id < 0 || id >= _config.parameters.Count)
            {
                return;
            }

            MissionParameter p = _config.parameters[id];

            if (p != null)
            {
                SerializedProperty paramName = _condition.FindPropertyRelative("paramName");
                paramName.stringValue = p.name;

                SerializedProperty compareType = _condition.FindPropertyRelative("compareType");


                switch (p.type)
                {
                    case MissionParamType.Float:
                    case MissionParamType.Int:
                        //condition.compareType = CompareType.Greater;

                        for (int i = 0; i < compareType.enumNames.Length; i++)
                        {
                            if (compareType.enumNames[i] == CompareType.Greater.ToString())
                            {
                                compareType.enumValueIndex = i;
                                break;
                            }
                        }

                        break;
                    case MissionParamType.Bool:

                        for (int i = 0; i < compareType.enumNames.Length; i++)
                        {
                            if (compareType.enumNames[i] == CompareType.Equals.ToString())
                            {
                                compareType.enumValueIndex = i;
                                break;
                            }
                        }


                        break;
                }

                // 保存
                _condition.serializedObject.ApplyModifiedProperties();
            }

        }

        /// <summary>绘制候选参数名称、说明与类型，并标出当前条件绑定的参数。</summary>
        protected override void RowGUI(RowGUIArgs args)
        {


            if (args.item.id < 0 || args.item.id >= _config.parameters.Count)
            {
                return;
            }

            SerializedProperty paramName = _condition.FindPropertyRelative("paramName");

            MissionParameter parameter = _config.parameters[args.item.id];

            if (parameter.name.Equals(paramName.stringValue))
            {
                GUI.Label(args.rowRect, "√");
            }

            args.rowRect.x += 15;
            args.rowRect.width -= 15;

            args.rowRect.width -= 35;

            if (_labelStyle == null)
            {
                _labelStyle = new GUIStyle(GUI.skin.label);
                _labelStyle.richText = true;
            }

            if (!_contents.ContainsKey(args.item.id))
            {
                _contents.Add(args.item.id, new GUIContent());
            }

            if (string.IsNullOrEmpty(parameter.description))
            {
                _contents[args.item.id].text = parameter.name;
            }
            else
            {
                _contents[args.item.id].text = string.Format("{0}<color=grey>({1})</color>", parameter.name, parameter.description.Replace("\n", string.Empty));
            }

            _contents[args.item.id].tooltip = parameter.description;

            EditorGUI.LabelField(args.rowRect, _contents[args.item.id], _labelStyle);

            args.rowRect.x += args.rowRect.width;

            Color c = GUI.color;

            GUI.color = Color.yellow;
            EditorGUI.LabelField(args.rowRect, parameter.type.ToString());

            GUI.color = c;
        }

    }

    /// <summary>带搜索框的条件参数选择弹窗，持有调用方传入的序列化条件字段。</summary>
    public class SelectParamPopWindow : PopupWindowContent
    {
        #region 字段

        private float _width;

        private MissionsConfig _controller;
        private SerializedProperty _condition;
        private MissionConfigData _missionConfigData;

        // 搜索框
        private SearchField _searchField;
        private Rect _searchRect;
        const float SearchHeight = 25f;

        // 标签
        private Rect _labelRect;
        const float LabelHeight = 30f;

        // 参数列表
        private ParamPopupTreeView _paramTree;
        private TreeViewState _paramState;
        private Rect _paramRect;

        #endregion


        /// <summary>记录弹窗宽度、候选配表和要修改的条件字段。</summary>
        public SelectParamPopWindow(float width, MissionsConfig controller, SerializedProperty condition)
        {
            this._width = width;
            this._controller = controller;
            this._condition = condition;
        }


        /// <summary>返回传入宽度与固定 240 像素高度。</summary>
        public override Vector2 GetWindowSize()
        {
            return new Vector2(this._width, 240);
        }

        /// <summary>按需建立参数树和搜索框，绘制当前参数名称及过滤后的候选列表。</summary>
        public override void OnGUI(Rect rect)
        {

            if (_paramTree == null)
            {
                if (_paramState == null)
                {
                    _paramState = new TreeViewState();
                }

                _paramTree = new ParamPopupTreeView(_paramState, _controller, _condition);
                _paramTree.Reload();
            }

            // 搜索框
            if (_searchField == null)
            {
                _searchField = new SearchField();
            }
            _searchRect.Set(rect.x + 5, rect.y + 5, rect.width - 10, SearchHeight);
            _paramTree.searchString = _searchField.OnGUI(_searchRect, _paramTree.searchString);

            // 标签
            _labelRect.Set(rect.x, rect.y + SearchHeight, rect.width, LabelHeight);

            SerializedProperty paramName = _condition.FindPropertyRelative("paramName");

            EditorGUI.LabelField(_labelRect, paramName.stringValue, GUI.skin.GetStyle("AC BoldHeader"));

            // 参数列表

            _paramRect.Set(rect.x, rect.y + SearchHeight + LabelHeight - 5, rect.width, rect.height - SearchHeight - LabelHeight);
            _paramTree.OnGUI(_paramRect);

        }
    }
}


#endif
