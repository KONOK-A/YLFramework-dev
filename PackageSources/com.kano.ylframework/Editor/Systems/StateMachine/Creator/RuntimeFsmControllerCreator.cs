using UnityEditor;
using UnityEditor.ProjectWindowCallback;
using UnityEngine;
using YLFramework.Editor.Common;
using YLFramework.StateMachine;

namespace YLFramework.Editor.StateMachine
{
    /// <summary>完成配置资产命名后创建配置，并初始化基础层的 Any State 和 Entry 内置节点。</summary>
    public class RuntimeFsmControllerCreator : EndNameEditAction
    {
        /// <summary>创建并选中配置资产，补齐内置节点后保存；普通默认状态仍由用户创建。</summary>
        /// <param name="instanceId">命名流程提供的实例标识，此实现不使用。</param>
        /// <param name="pathName">最终配置资产路径。</param>
        /// <param name="resourceFile">命名流程提供的资源参数，此实现不使用。</param>
        public override void Action(int instanceId, string pathName, string resourceFile)
        {
            RuntimeFsmController runtimeFsm = ScriptableObject.CreateInstance<RuntimeFsmController>();
            AssetDatabase.CreateAsset(runtimeFsm, pathName);
            Selection.activeObject = runtimeFsm;


            Rect rect = new Rect(0, 100, FsmConst.StateNodeWith, FsmConst.StateNodeHeight);
            FsmStateNodeFactory.CreateStateNode(runtimeFsm, FsmConst.AnyState, rect, false, false, null, true, FsmConst.AnyState);
            rect = new Rect(0, 300, FsmConst.StateNodeWith, FsmConst.StateNodeHeight);
            FsmStateNodeFactory.CreateStateNode(runtimeFsm, FsmConst.EntryState, rect, false, false, null, true, FsmConst.EntryState);
            AssetDatabase.SaveAssetIfDirty(runtimeFsm);
        }
    }

}
