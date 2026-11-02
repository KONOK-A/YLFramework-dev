using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using YLFramework.Character2D;

namespace YLFramework.Editor.Character2D
{
    /// <summary>编辑角色控制器的检测、重力与事件配置，并展示控制器已有的接触和移动诊断。</summary>
    [CustomEditor(typeof(CharacterController2D))]
    public class CharacterController2DInspector : UnityEditor.Editor
    {

        private SerializedProperty _autoRotation = null;
        private SerializedProperty _showDebugLine = null;
        private SerializedProperty _maxJumpCount = null;
        private SerializedProperty _isCheckGround = null;
        private SerializedProperty _isCheckWall = null;
        private SerializedProperty _isCheckTop = null;
        private SerializedProperty _gravityScale = null;
        private SerializedProperty _maxFallingVelocity = null;
        private SerializedProperty _groundAngle = null;
        private SerializedProperty _wallAngle = null;
        private SerializedProperty _topAngle = null;
        private SerializedProperty _onGroundValueChange = null;
        private SerializedProperty _onTouchWallValueChange = null;
        private SerializedProperty _onTopValueChange = null;
        private SerializedProperty _onVelocityValueChange = null;
        private SerializedProperty _onJumpCountValueChange = null;


        private CharacterController2D _character;

        private bool _showEvents = true;

        private FieldInfo _checkCountInfo = null;

        /// <summary>为当前控制器缓存序列化属性及诊断计数字段的反射入口。</summary>
        private void OnEnable()
        {
            _autoRotation = serializedObject.FindProperty("autoRotation");
            _showDebugLine = serializedObject.FindProperty("showDebugLine");
            _maxJumpCount = serializedObject.FindProperty("maxJumpCount");
            _isCheckGround = serializedObject.FindProperty("isCheckGround");
            _isCheckWall = serializedObject.FindProperty("isCheckWall");
            _isCheckTop = serializedObject.FindProperty("isCheckTop");
            _gravityScale = serializedObject.FindProperty("gravityScale");
            _maxFallingVelocity = serializedObject.FindProperty("maxFallingVelocity");
            _groundAngle = serializedObject.FindProperty("groundAngle");
            _wallAngle = serializedObject.FindProperty("wallAngle");
            _topAngle = serializedObject.FindProperty("topAngle");
            _onGroundValueChange = serializedObject.FindProperty("onGroundValueChange");
            _onTouchWallValueChange = serializedObject.FindProperty("onTouchWallValueChange");
            _onTopValueChange = serializedObject.FindProperty("onTopValueChange");
            _onVelocityValueChange = serializedObject.FindProperty("onVelocityValueChange");
            _onJumpCountValueChange = serializedObject.FindProperty("onJumpCountValueChange");

            _character = target as CharacterController2D;


            _checkCountInfo = _character.GetType().GetField("CheckCount", BindingFlags.NonPublic | BindingFlags.Instance);
        }

        /// <summary>提示碰撞体和 Layer 配置问题，按检测开关绘制配置并应用序列化修改，再显示诊断。</summary>
        public override void OnInspectorGUI()
        {

            serializedObject.Update();

            var colliders = new List<Collider2D>();
            _character.Rigidbody.GetAttachedColliders(colliders);
            if (!colliders.Exists(c => c && !c.isTrigger && (c is BoxCollider2D || c is CircleCollider2D || c is CapsuleCollider2D)))
            {
                EditorGUILayout.HelpBox("Rigidbody2D 缺少非 Trigger 的 Box/Circle/CapsuleCollider2D。运行时添加后需调用 RefreshColliders()。", MessageType.Error);
            }

            if (string.IsNullOrEmpty(LayerMask.LayerToName(_character.gameObject.layer)))
            {
                EditorGUILayout.HelpBox("当前对象使用未命名 Layer；请检查对象 Layer 与 Physics2D 碰撞矩阵。", MessageType.Warning);
            }

            EditorGUILayout.PropertyField(_autoRotation);
            EditorGUILayout.PropertyField(_showDebugLine);
            EditorGUILayout.PropertyField(_maxJumpCount);
            EditorGUILayout.PropertyField(_isCheckGround);
            if (_isCheckGround.boolValue)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Space(15);
                GUILayout.BeginVertical();

                EditorGUILayout.PropertyField(_gravityScale);
                EditorGUILayout.PropertyField(_groundAngle);
                EditorGUILayout.PropertyField(_maxFallingVelocity);
                EditorGUILayout.PropertyField(_onGroundValueChange);

                GUILayout.EndVertical();

                GUILayout.EndHorizontal();
            }



            EditorGUILayout.PropertyField(_isCheckWall);
            if (_isCheckWall.boolValue)
            {


                GUILayout.BeginHorizontal();
                GUILayout.Space(15);
                GUILayout.BeginVertical();

                EditorGUILayout.PropertyField(_wallAngle);
                EditorGUILayout.PropertyField(_onTouchWallValueChange);

                GUILayout.EndVertical();

                GUILayout.EndHorizontal();

            }


            EditorGUILayout.PropertyField(_isCheckTop);
            if (_isCheckTop.boolValue)
            {

                GUILayout.BeginHorizontal();
                GUILayout.Space(15);
                GUILayout.BeginVertical();

                EditorGUILayout.PropertyField(_topAngle);
                EditorGUILayout.PropertyField(_onTopValueChange);

                GUILayout.EndVertical();

                GUILayout.EndHorizontal();

            }

            EditorGUILayout.PropertyField(_onVelocityValueChange);
            EditorGUILayout.PropertyField(_onJumpCountValueChange);

            serializedObject.ApplyModifiedProperties();

            DrawResult(ref _showEvents);
        }



        /// <summary>只读显示控制器当前接触状态、速度、跳跃次数及射线计数，不触发新的检测。</summary>
        /// <param name="resultFoldout">由调用方保存并接收本次变化的折叠状态。</param>
        public void DrawResult(ref bool resultFoldout)
        {
            resultFoldout = EditorGUILayout.Foldout(resultFoldout, "Infos");
            if (resultFoldout)
            {
                var oldColor = GUI.color;
                GUI.color = Color.gray;


                EditorGUILayout.LabelField("IsGrounded:", _character.IsGrounded.ToString());
                EditorGUILayout.LabelField("IsTouchWall:", _character.IsTouchingWall.ToString());
                EditorGUILayout.LabelField("IsTop:", _character.IsTouchingCeiling.ToString());
                EditorGUILayout.LabelField("Velocity:", _character.Velocity.ToString());

                EditorGUILayout.LabelField("CurrentJumpCount:", _character.CurrentJumpCount.ToString());


                int checkCount = (int)_checkCountInfo.GetValue(_character);
                EditorGUILayout.LabelField("每秒射线检测次数:", checkCount.ToString());
                GUI.color = oldColor;
            }
        }



    }

}
