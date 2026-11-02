namespace YLFramework.FSM
{
    /// <summary>
    /// 状态机宿主标记接口。
    /// 仅用于给状态提供业务侧上下文引用，本身不定义任何行为，允许为 null。
    /// 需要访问具体宿主类型时，可自行转换，或使用 AbstractState.GetOwner&lt;T&gt;() 获取。
    /// </summary>
    public interface IFsmOwner
    {
    }
}
