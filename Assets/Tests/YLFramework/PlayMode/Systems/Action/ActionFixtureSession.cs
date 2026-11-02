using System.Reflection;
using YLFramework.Actions;
using YLFramework.Events;
using YLFramework.Foundation;
using Object = UnityEngine.Object;
using YLFramework.Update;

namespace YLFramework.Tests.Actions
{
    internal static class ActionFixtureSession
    {
        // 只清本 fixture 新建且已无任何公共消费者的宿主；不接管已有宿主。
        internal static void ReleaseIdleUpdateHost(UpdateManager previous)
        {
            if (previous)
            {
                return;
            }

            var host = Object.FindObjectOfType<UpdateManager>(true);
            if (!host)
            {
                return;
            }

            foreach (var phase in new[] { "_update", "_fixedUpdate", "_lateUpdate" })
            {
                var channel = (EventChannel)typeof(UpdateManager).GetField(phase,
                    BindingFlags.Instance | BindingFlags.NonPublic).GetValue(host);
                if (channel.HasListener)
                {
                    return;
                }
            }
            Object.Destroy(host.gameObject);
        }
        internal static bool CanOwnUninitializedSession()
        {
            if (ActionKit.IsInitialized || ActionKit.IsShuttingDown ||
                Object.FindObjectOfType<MonoUpdateActionExecutor>(true) ||
                Object.FindObjectOfType<ActionGlobalHost>(true) ||
                Object.FindObjectOfType<ActionKitCurrentScene>(true) ||
                Object.FindObjectOfType<ActionRecycleDriver>(true) ||
                Object.FindObjectOfType<ScreenTransitionCanvas>(true) ||
                ActionQueue.PendingCount != 0)
            {
                return false;
            }

            return true;
        }
    }
}
