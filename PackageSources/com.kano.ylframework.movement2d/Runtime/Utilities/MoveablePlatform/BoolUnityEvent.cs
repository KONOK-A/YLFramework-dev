using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;

namespace YLFramework.Platform2D
{
    /// <summary>可序列化的平台暂停状态通知，参数表示是否暂停。</summary>
    [Serializable]
    public class BoolUnityEvent : UnityEvent<bool>
    {

    }
}
