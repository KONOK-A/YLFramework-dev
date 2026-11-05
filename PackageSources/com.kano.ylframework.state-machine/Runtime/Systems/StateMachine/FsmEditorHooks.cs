#if UNITY_EDITOR
using System;
using UnityEngine;

namespace YLFramework.StateMachine
{
    /// <summary>仅在编辑器中传递作者操作的委托桥接，编辑器专属类型和实现留在 Editor 程序集。</summary>
    public static class FsmEditorHooks
    {
        /// <summary>请求保存指定配置；当前编辑器实现仅标脏，不负责立即落盘或回写源资产。</summary>
        public static Action<RuntimeFsmController> Save;
        /// <summary>按脚本 GUID 刷新节点绑定的类型名，参数依次为节点、所属配置。</summary>
        public static Action<FsmStateNodeData, RuntimeFsmController> RefreshNode;
        /// <summary>请求向节点绑定脚本资产，参数依次为节点、脚本对象。</summary>
        public static Action<FsmStateNodeData, UnityEngine.Object> AddScript;
        /// <summary>请求按脚本资产移除节点绑定，参数依次为节点、脚本对象。</summary>
        public static Action<FsmStateNodeData, UnityEngine.Object> RemoveScript;
        /// <summary>配置克隆建立后的通知，参数依次为源配置、运行时克隆。</summary>
        public static Action<RuntimeFsmController, RuntimeFsmController> CloneCreated;
    }
}
#endif
