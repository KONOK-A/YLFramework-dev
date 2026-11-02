using UnityEngine;
using YLFramework.StateMachine;

namespace YLFramework.Editor.StateMachine
{
    /// <summary>条件值绘制策略基类；空实现用于不需要额外比较控件的 Trigger 条件。</summary>
    public class FsmConditionInspector
    {
        /// <summary>绘制参数类型对应的比较与目标值，基类不绘制也不修改数据。</summary>
        /// <param name="rect">可用的条件值绘制区域。</param>
        /// <param name="condtion">待编辑条件。</param>
        /// <param name="controller">拥有条件的配置，供具体策略记录 Undo。</param>
        public virtual void OnGUI(Rect rect, FsmConditionData condtion, RuntimeFsmController controller)
        {

        }
    }

}
