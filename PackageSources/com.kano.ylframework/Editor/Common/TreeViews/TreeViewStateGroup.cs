using System;
using System.Collections.Generic;
using UnityEditor.IMGUI.Controls;
using UnityEngine;
using UnityEngine.Scripting.APIUpdating;

#if UNITY_6000_2_OR_NEWER 
using TreeView = UnityEditor.IMGUI.Controls.TreeView<int>;
using TreeViewState = UnityEditor.IMGUI.Controls.TreeViewState<int>;
using TreeViewItem = UnityEditor.IMGUI.Controls.TreeViewItem<int>;
#endif


namespace YLFramework.Editor.Common
{
    /// <summary>把调用方提供的整数标识与一份 TreeView 界面状态一起序列化。</summary>
    [Serializable]
    [MovedFrom(true, sourceNamespace: "YLFramework.Editor.Utilities")]
    public class TreeViewStateInfo
    {
        /// <summary>调用方用于查找此状态的整数标识。</summary>
        public int id;


        /// <summary>对应树的选择、展开及滚动等界面状态，不包含业务列表数据。</summary>
        public TreeViewState treeViewState;

        /// <summary>登记已有树状态及其标识，不复制状态对象。</summary>
        /// <param name="id">状态所属的分组标识。</param>
        /// <param name="treeViewState">由分组持有的树状态引用。</param>
        public TreeViewStateInfo(int id, TreeViewState treeViewState)
        {
            this.id = id;
            this.treeViewState = treeViewState;
        }
    }

    /// <summary>为同一窗口中的多份列表按整数标识保留各自的树状态，由宿主决定是否持久化。</summary>
    [Serializable]
    [MovedFrom(true, sourceNamespace: "YLFramework.Editor.Utilities")]
    public class TreeViewStateGroup
    {

        [SerializeField]
        private List<TreeViewStateInfo> treeViewStates = new List<TreeViewStateInfo>();

        /// <summary>返回首个标识匹配的状态；未找到时新建并加入分组，相同整数始终复用同一份状态。</summary>
        /// <param name="hashCode">调用方负责避免冲突的分组标识。</param>
        /// <returns>分组内保存的状态引用，不是副本。</returns>
        public TreeViewState Get(int hashCode)
        {

            foreach (var item in treeViewStates)
            {
                if (item.id == hashCode)
                {
                    return item.treeViewState;
                }
            }

            TreeViewState treeViewState = new TreeViewState();

            treeViewStates.Add(new TreeViewStateInfo(hashCode, treeViewState));
            return treeViewState;
        }

    }

}
