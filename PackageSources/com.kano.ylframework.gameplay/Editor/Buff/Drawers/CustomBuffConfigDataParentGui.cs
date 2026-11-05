using YLFramework.Foundation;
using YLFramework.Attributes;
#if UNITY_EDITOR

using System.Collections;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace YLFramework.Editor.Buff
{
    /// <summary>只读展示 Buff 定义所属的配表，避免通过字段编辑任意改换归属。</summary>
    public class CustomBuffConfigDataParentGui : CustomPropertyGui
    {
        /// <summary>在禁用编辑的控件组内绘制所属配表引用。</summary>
        public override void OnPropertyGui(SerializedProperty serializedProperty)
        {
            EditorGUI.BeginDisabledGroup(true);
            EditorGUILayout.PropertyField(serializedProperty);
            EditorGUI.EndDisabledGroup();
        }
    }

}



#endif
