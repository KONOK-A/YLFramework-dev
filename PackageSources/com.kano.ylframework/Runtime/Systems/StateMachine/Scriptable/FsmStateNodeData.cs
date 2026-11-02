using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Serialization;

namespace YLFramework.StateMachine
{
    /// <summary>节点脚本绑定记录，以类型名供运行时反射，以资产 GUID 供编辑器跟踪脚本。</summary>
    [System.Serializable]
    public class FsmStateScriptInfo
    {
        /// <summary>可解析为具体 FsmState 类型的完整名称。</summary>
        public string className;
        /// <summary>编辑器脚本资产 GUID，不参与 Player 中的类型创建。</summary>
        public string guid;
    }


    /// <summary>可序列化的状态节点定义，同时承载层级、脚本绑定及编辑器布局。</summary>
    [System.Serializable]
    public class FsmStateNodeData
    {

#if UNITY_EDITOR
        /// <summary>节点在图编辑器中的位置与尺寸。</summary>
        public Rect rect;

        /// <summary>通过编辑器桥接请求绑定脚本；数据保存由调用方负责。</summary>
        /// <param name="script">待绑定的脚本资产。</param>
        public void AddStateScript(UnityEngine.Object script) => FsmEditorHooks.AddScript?.Invoke(this, script);
        /// <summary>请求编辑器按脚本 GUID 刷新可解析的类型名，保留暂时失效或缺少 GUID 的记录。</summary>
        /// <param name="controller">所属配置，供刷新后保存。</param>
        public void RefreshStateScripts(RuntimeFsmController controller) => FsmEditorHooks.RefreshNode?.Invoke(this, controller);
        /// <summary>通过编辑器桥接请求移除脚本绑定；数据保存由调用方负责。</summary>
        /// <param name="script">待移除绑定的脚本资产。</param>
        public void RemoveStateScript(UnityEngine.Object script) => FsmEditorHooks.RemoveScript?.Invoke(this, script);

#endif

        /// <summary>
        /// 是否为直接父层的默认进入节点。
        /// </summary>
        [FormerlySerializedAs("defaultState")] public bool isDefaultState;
        /// <summary>
        /// 配置内全局唯一的节点名称，用于过渡和父层引用。
        /// </summary>
        public string name;

        /// <summary>
        /// 按执行顺序排列的状态脚本绑定记录。
        /// </summary>
        [FormerlySerializedAs("StateScripts")] public List<FsmStateScriptInfo> stateScripts = new List<FsmStateScriptInfo>();

        /// <summary>
        /// 从外到内排列的完整祖先名称列表；空列表或 null 表示基础层。
        /// </summary>
        public List<string> parents;

        /// <summary>
        /// 是否为子状态机容器；进入后继续进入其内部默认节点。
        /// </summary>
        public bool isSubStateMachine = false;

        /// <summary>
        /// 是否具有编辑器内置节点身份，需与内置种类共同判定。
        /// </summary>
        [FormerlySerializedAs("isBuildInState")] public bool isBuiltInState = false;

        /// <summary>
        /// 内置节点种类标识，不由显示名称推断。
        /// </summary>
        [FormerlySerializedAs("buildInStateName")] public string builtInStateName = null;

        #region 属性

        /// <summary>
        /// 同时具有内置标记及 Any State 种类时为 true，不依据节点名称推断。
        /// </summary>
        public bool IsAnyState
        {
            get
            {
                if (isBuiltInState && builtInStateName == FsmConst.AnyState)
                {
                    return true;
                }

                return false;
            }
        }

        /// <summary>
        /// 同时具有内置标记及 Entry 种类时为 true，不代表运行时默认节点。
        /// </summary>
        public bool IsEntryState
        {
            get
            {

                if (isBuiltInState && builtInStateName == FsmConst.EntryState)
                {
                    return true;
                }

                return false;
            }
        }

        /// <summary>
        /// 同时具有内置标记及 Up 种类时为 true，用于图编辑器返回上层。
        /// </summary>
        public bool IsUpState
        {
            get
            {

                if (isBuiltInState && builtInStateName == FsmConst.Up)
                {
                    return true;
                }

                return false;
            }
        }

        /// <summary>用于图界面的名称；内置节点按其种类及返回层级显示。</summary>
        public string DisplayName
        {
            get
            {
                if (IsAnyState)
                {
                    return FsmConst.AnyState;
                }

                if (IsEntryState)
                {
                    return FsmConst.EntryState;
                }

                if (IsUpState)
                {
                    if (parents != null && parents.Count > 1)
                    {
                        return string.Format("(Up){0}", parents[parents.Count - 2]);
                    }
                    else
                    {
                        return "(Up)Base Layer";
                    }
                }

                return name;
            }
        }

        /// <summary>以斜杠连接的完整祖先路径，基础层为空字符串。</summary>
        public string ParentPath
        {
            get
            {
                if (parents == null || parents.Count == 0)
                {
                    return string.Empty;
                }

                StringBuilder sb = new StringBuilder();
                for (int i = 0; i < parents.Count; i++)
                {
                    sb.Append(parents[i]);
                    if (i < parents.Count - 1)
                    {
                        sb.Append("/");
                    }
                }

                return sb.ToString();
            }
        }

        /// <summary>祖先路径追加自身名称后的路径，用于表示进入此容器的层级。</summary>
        public string NextParentPath
        {
            get
            {
                List<string> nextParents = new List<string>();

                if (parents != null && parents.Count != 0)
                {
                    nextParents.AddRange(parents);
                }

                nextParents.Add(name);

                StringBuilder sb = new StringBuilder();
                for (int i = 0; i < nextParents.Count; i++)
                {
                    sb.Append(nextParents[i]);
                    if (i < nextParents.Count - 1)
                    {
                        sb.Append("/");
                    }
                }

                return sb.ToString();
            }
        }

        /// <summary>直接父节点名称，基础层为空字符串。</summary>
        public string Parent
        {
            get
            {
                if (parents == null || parents.Count == 0)
                {
                    return string.Empty;
                }

                return parents[parents.Count - 1];
            }
        }

        #endregion

        /// <summary>比较两个节点的完整祖先序列，判断是否属于同一直接层级。</summary>
        /// <param name="node">待比较的非空节点。</param>
        /// <returns>双方均为基础层或祖先逐项相等时为 true。</returns>
        public bool CompareParent(FsmStateNodeData node)
        {
            if (BaseLayer() && node.BaseLayer())
            {
                return true;
            }

            if (parents == null || node.parents == null)
            {
                return false;
            }

            if (node.parents.Count != this.parents.Count)
            {
                return false;
            }

            for (int i = 0; i < parents.Count; i++)
            {
                if (!parents[i].Equals(node.parents[i]))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>比较指定祖先序列是否与当前节点的层级一致。</summary>
        /// <param name="parents">从外到内的祖先名称，null 或空表示基础层。</param>
        /// <returns>祖先层级一致时为 true。</returns>
        public bool CompareParent(List<string> parents)
        {

            if (BaseLayer())
            {
                return parents == null || parents.Count == 0;
            }

            if (parents == null || this.parents == null)
            {
                return false;
            }

            if (parents.Count != this.parents.Count)
            {
                return false;
            }

            for (int i = 0; i < this.parents.Count; i++)
            {
                if (!this.parents[i].Equals(parents[i]))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>判断节点是否没有祖先、直接属于基础层。</summary>
        /// <returns>祖先列表为空或 null 时为 true。</returns>
        public bool BaseLayer()
        {
            return this.parents == null || this.parents.Count == 0;
        }


        /// <summary>判断完整祖先链中是否包含指定名称，不限于直接父节点。</summary>
        /// <param name="parent">要查询的祖先名称。</param>
        /// <returns>祖先链包含此名称时为 true。</returns>
        public bool ContainsParent(string parent)
        {
            if (BaseLayer())
            {
                return false;
            }

            return parents.Contains(parent);
        }

        /// <summary>判断节点是否直接属于指定父节点。</summary>
        /// <param name="parent">要查询的直接父节点名称。</param>
        /// <returns>非基础层且直接父节点同名时为 true。</returns>
        public bool BelongToParent(string parent)
        {
            if (BaseLayer())
            {
                return false;
            }

            return parents[parents.Count - 1].Equals(parent);
        }

        /// <summary>替换祖先链中首个匹配名称；调用方须保证旧名称存在并同步其他引用。</summary>
        /// <param name="oldParent">已存在于祖先列表中的名称。</param>
        /// <param name="newName">替换后的祖先名称。</param>
        public void RenameParent(string oldParent, string newName)
        {
            int index = parents.IndexOf(oldParent);
            parents[index] = newName;
        }

        /// <summary>取得祖先列表的独立副本，修改副本不会改变节点层级。</summary>
        /// <returns>从外到内的祖先名称副本，基础层返回空列表。</returns>
        public List<string> GetParent()
        {
            List<string> parents = new List<string>();
            if (this.parents == null)
            {
                return parents;
            }

            parents.AddRange(this.parents);
            return parents;
        }

    }

}
