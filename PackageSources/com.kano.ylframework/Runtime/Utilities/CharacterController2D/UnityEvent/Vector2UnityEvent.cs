using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;

namespace YLFramework.Character2D
{
    /// <summary>可序列化的角色二维向量通知，用于传递组件计算的移动速度。</summary>
    [Serializable]
    public class Vector2UnityEvent : UnityEvent<Vector2>
    {

    }

}
