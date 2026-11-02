namespace YLFramework.Events
{
    /// <summary>
    /// 使用基类 OnDestroy 尝试注销已托管句柄；失败需持有者显式重试，从未激活的宿主须显式清理。
    /// 通常由 <see cref="UnregisterExtension.UnregisterWhenGameObjectDestroyed(IUnregister, UnityEngine.GameObject)"/> 自动挂载。
    /// </summary>
    public class OnDestroyUnregisterTrigger : UnregisterTriggerBase
    {
    }
}
