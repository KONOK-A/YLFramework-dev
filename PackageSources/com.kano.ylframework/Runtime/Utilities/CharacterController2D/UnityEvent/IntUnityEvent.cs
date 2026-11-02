using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;

namespace YLFramework.Character2D
{
    /// <summary>可序列化的角色整数通知，用于传递改变后的跳跃计数。</summary>
    [Serializable]
    public class IntUnityEvent : UnityEvent<int>
    {

    }

}
