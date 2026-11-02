

namespace YLFramework.StateMachine
{
    /// <summary>状态机内置节点身份及编辑器默认节点尺寸。</summary>
    public class FsmConst
    {

        /// <summary>同父层任意当前状态的过渡来源标识。</summary>
        public const string AnyState = "Any State";

        /// <summary>编辑器默认状态入口节点的内置标识。</summary>
        public const string EntryState = "Entry";

        /// <summary>编辑器返回上一层节点的内置标识。</summary>
        public const string Up = "up";

        /// <summary>编辑器新建节点的默认宽度。</summary>
        public const int StateNodeWith = 200;
        /// <summary>编辑器新建节点的默认高度。</summary>
        public const int StateNodeHeight = 40;

    }

}
