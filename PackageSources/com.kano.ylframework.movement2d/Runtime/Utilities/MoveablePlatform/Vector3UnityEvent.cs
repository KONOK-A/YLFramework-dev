using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;

namespace YLFramework.Platform2D
{
    /// <summary>可序列化的平台向量通知，用于在提交前传递目标世界位置。</summary>
    [Serializable]
    public class Vector3UnityEvent : UnityEvent<Vector3>
    {
    }

}
