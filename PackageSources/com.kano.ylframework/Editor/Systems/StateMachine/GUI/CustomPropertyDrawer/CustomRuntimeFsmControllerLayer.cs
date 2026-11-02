using System.Collections;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using YLFramework.StateMachine;

namespace YLFramework.Editor.StateMachine
{

    /// <summary>绘制宿主的命名配置层，通过序列化属性提交名称并修正新增层造成的重名。</summary>
    [CustomPropertyDrawer(typeof(RuntimeFsmControllerLayer))]
    public class CustomRuntimeFsmControllerLayer : PropertyDrawer
    {

        private GUIContent _empty;

        private int _count;

        /// <summary>绘制层名及配置引用，层数变化时修正重复名称，名称修改通过所属 SerializedObject 提交。</summary>
        /// <param name="rect">属性绘制区域。</param>
        /// <param name="property">指向宿主配置层的序列化属性。</param>
        /// <param name="label">Unity 提供的属性标签，此布局不直接绘制。</param>
        public override void OnGUI(Rect rect, SerializedProperty property, GUIContent label)
        {
            rect.height = base.GetPropertyHeight(property, label);

            var name = property.FindPropertyRelative("name");
            var controller = property.FindPropertyRelative("controller");

            FsmController fsmController = property.serializedObject.targetObject as FsmController;

            if (fsmController.RuntimeFsmControllerLayers.Count != _count)
            {
                _count = fsmController.RuntimeFsmControllerLayers.Count;

                for (int i = _count - 1; i >= 0; i--)
                {

                    bool isRepeat = false;

                    for (int j = 0; j < _count; j++)
                    {
                        if (i == j)
                        {
                            continue;
                        }

                        if (fsmController.RuntimeFsmControllerLayers[i].name == fsmController.RuntimeFsmControllerLayers[j].name)
                        {
                            isRepeat = true;
                        }
                    }

                    if (isRepeat)
                    {
                        var layers = property.serializedObject.FindProperty("runtimeFsmControllerLayers");
                        var layerName = layers.GetArrayElementAtIndex(i).FindPropertyRelative("name");
                        layerName.stringValue = GetLayerName(fsmController, i);
                        layerName.serializedObject.ApplyModifiedProperties();
                    }

                }

            }

            if (fsmController.RuntimeFsmControllerLayers.Count == 1)
            {
                if (string.IsNullOrEmpty(fsmController.RuntimeFsmControllerLayers[0].name))
                {
                    var layers = property.serializedObject.FindProperty("runtimeFsmControllerLayers");
                    var layerName = layers.GetArrayElementAtIndex(0).FindPropertyRelative("name");
                    layerName.stringValue = "Layer 0";
                    layerName.serializedObject.ApplyModifiedProperties();
                }
            }

            rect.width /= 4;

            string nameValue = name.stringValue;
            EditorGUI.BeginChangeCheck();
            nameValue = EditorGUI.DelayedTextField(rect, nameValue);
            if (EditorGUI.EndChangeCheck())
            {
                if (IsContainName(fsmController, nameValue))
                {
                    EditorWindow.focusedWindow.ShowNotification(new GUIContent(string.Format("名称:{0}已经存在!", nameValue)));
                }
                else
                {
                    name.stringValue = nameValue;
                    name.serializedObject.ApplyModifiedProperties();
                }
            }

            rect.x += rect.width + 3;
            rect.width *= 3;
            rect.width -= 2;

            if (_empty == null)
            {
                _empty = new GUIContent();
            }

            EditorGUI.PropertyField(rect, controller, _empty);

        }

        /// <summary>按完整名称查询当前层列表，用于重名拒绝和候选层名生成。</summary>
        private bool IsContainName(FsmController controller, string name)
        {
            foreach (var item in controller.RuntimeFsmControllerLayers)
            {
                if (item.name == name)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>在默认单行属性高度上追加一像素间距。</summary>
        /// <param name="property">当前配置层属性。</param>
        /// <param name="label">属性标签。</param>
        /// <returns>该层在 Inspector 中占用的高度。</returns>
        public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
        {
            return base.GetPropertyHeight(property, label) + 1;
        }

        /// <summary>从指定序号起递增，生成当前层列表尚未使用的层名称。</summary>
        private string GetLayerName(FsmController controller, int index = 0)
        {
            string name = string.Empty;

            do
            {
                name = string.Format("Layer {0}", index);
                index++;
            } while (IsContainName(controller, name));

            return name;

        }

    }

}
