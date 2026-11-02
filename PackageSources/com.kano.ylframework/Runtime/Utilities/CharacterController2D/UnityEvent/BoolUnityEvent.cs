using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;

namespace YLFramework.Character2D
{
    /// <summary>可序列化的角色布尔状态通知，参数为改变后的状态。</summary>
    [Serializable]
    public class BoolUnityEvent : UnityEvent<bool>
    {

    }
}
