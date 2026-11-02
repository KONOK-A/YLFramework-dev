using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using YLFramework.Events;
using UpdateManager = YLFramework.Update.UpdateManager;

namespace YLFramework.Core.Tests
{
    // 仅清理本 fixture 首次创建、且已无公共责任的宿主，不能接管先前对象或其他订阅。
    internal sealed class CoreUpdateHostScope
    {
        private readonly HashSet<int> _previous = new();
        private static readonly FieldInfo[] _channels =
        {
            typeof(UpdateManager).GetField("_update", BindingFlags.Instance | BindingFlags.NonPublic),
            typeof(UpdateManager).GetField("_fixedUpdate", BindingFlags.Instance | BindingFlags.NonPublic),
            typeof(UpdateManager).GetField("_lateUpdate", BindingFlags.Instance | BindingFlags.NonPublic)
        };

        internal CoreUpdateHostScope()
        {
            foreach (var host in FindHosts())
            {
                _previous.Add(host.GetInstanceID());
            }
        }

        internal void ReleaseOwnedIdleHosts()
        {
            foreach (var host in FindHosts())
            {
                if (_previous.Contains(host.GetInstanceID()))
                {
                    continue;
                }

                var idle = true;
                foreach (var field in _channels)
                {
                    if (((EventChannel)field.GetValue(host)).HasListener)
                    {
                        idle = false;
                        break;
                    }
                }

                if (idle)
                {
                    Object.Destroy(host.gameObject);
                }
            }
        }

        private static UpdateManager[] FindHosts() => Object.FindObjectsByType<UpdateManager>(
            FindObjectsInactive.Include, FindObjectsSortMode.None);
    }
}
