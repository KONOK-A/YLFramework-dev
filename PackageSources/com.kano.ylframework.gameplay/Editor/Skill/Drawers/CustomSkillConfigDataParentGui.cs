using YLFramework.Foundation;
using YLFramework.Attributes;
#if UNITY_EDITOR

using UnityEditor;


namespace YLFramework.Editor.Skill
{
    /// <summary>将技能条目的配表归属绘制为只读字段，避免直接改挂归属。</summary>
    public class CustomSkillConfigDataParentGui : CustomPropertyGui
    {


        /// <summary>在禁用编辑状态下显示归属字段，不应用任何修改。</summary>
        public override void OnPropertyGui(SerializedProperty serializedProperty)
        {
            EditorGUI.BeginDisabledGroup(true);
            EditorGUILayout.PropertyField(serializedProperty);
            EditorGUI.EndDisabledGroup();
        }


    }

}


#endif
