using UnityEditor;
using UnityEngine;
using YLFramework.Platform2D;

namespace YLFramework.Editor.Platform2D
{

    /// <summary>为平台捕获区域提供 Scene 端点操作柄，区域数据仍保存为捕获器的局部坐标。</summary>
    [CustomEditor(typeof(PlatformCatcher2D))]
    public class PlatformCatcher2DInspector : UnityEditor.Editor
    {
        private PlatformCatcher2D _catcher;

        private Vector3 _snap = new Vector3(0.1f, 0.1f, 0.01f);

        /// <summary>缓存当前 Inspector 所编辑的平台捕获器。</summary>
        private void OnEnable()
        {
            _catcher = target as PlatformCatcher2D;
        }

        /// <summary>把有效区域端点转换到世界坐标供拖动，再通过序列化属性应用回局部坐标。</summary>
        private void OnSceneGUI()
        {

            if (_catcher.areas == null || _catcher.areas.Length == 0)
            {
                return;
            }

            serializedObject.Update();

            for (int i = 0; i < _catcher.areas.Length; i++)
            {
                Area item = _catcher.areas[i];

                Vector3 from = _catcher.transform.localToWorldMatrix.MultiplyPoint(item.from);
                Vector3 to = _catcher.transform.localToWorldMatrix.MultiplyPoint(item.to);

                float size = HandleUtility.GetHandleSize(to) * 0.1f;

                Handles.color = Color.green;

                EditorGUI.BeginChangeCheck();

                from = Handles.FreeMoveHandle(from, size, _snap, Handles.CubeHandleCap);


                if (EditorGUI.EndChangeCheck())
                {
                    // 操作柄返回世界坐标，写回局部端点才能继续随平台的平移、旋转和缩放移动。
                    serializedObject.FindProperty("areas").GetArrayElementAtIndex(i).FindPropertyRelative("from").vector3Value = _catcher.transform.worldToLocalMatrix.MultiplyPoint(from);
                    serializedObject.ApplyModifiedProperties();
                }

                EditorGUI.BeginChangeCheck();

                to = Handles.FreeMoveHandle(to, size, Vector3.one * 0.1f, Handles.CubeHandleCap);

                if (EditorGUI.EndChangeCheck())
                {
                    serializedObject.FindProperty("areas").GetArrayElementAtIndex(i).FindPropertyRelative("to").vector3Value = _catcher.transform.worldToLocalMatrix.MultiplyPoint(to);
                    serializedObject.ApplyModifiedProperties();
                }

            }


        }

    }
}
