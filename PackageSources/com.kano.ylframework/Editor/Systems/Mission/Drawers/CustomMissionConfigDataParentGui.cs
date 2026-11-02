using YLFramework.Foundation;
using YLFramework.Attributes;
#if UNITY_EDITOR

using System.Collections;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;




namespace YLFramework.Editor.Mission
{
    /// <summary>将任务条目的配表归属显示为只读字段，避免从详情中直接改挂归属。</summary>
    public class CustomMissionConfigDataParentGui : CustomPropertyGui
    {
        /// <summary>以禁用状态绘制归属字段，不修改或应用其序列化值。</summary>
        public override void OnPropertyGui(SerializedProperty serializedProperty)
        {

            EditorGUI.BeginDisabledGroup(true);

            EditorGUILayout.PropertyField(serializedProperty);

            EditorGUI.EndDisabledGroup();

        }
    }

}

#endif
