namespace YLFramework.Events
{
    /// <summary>
    /// 收到 OnDisable 时尝试注销已托管句柄；失败会记录日志，重新启用不会自动重订。
    /// 通常由 <see cref="UnregisterExtension.UnregisterWhenDisabled(IUnregister, UnityEngine.GameObject)"/> 自动挂载。
    /// </summary>
    public class OnDisableUnregisterTrigger : UnregisterTriggerBase
    {
        /// <summary>响应实际收到的禁用消息，尝试本轮全部清理并记录失败，不阻断剩余句柄。</summary>
        private void OnDisable()
        {
            TriggerUnregistersAndLog();
        }
    }
}
