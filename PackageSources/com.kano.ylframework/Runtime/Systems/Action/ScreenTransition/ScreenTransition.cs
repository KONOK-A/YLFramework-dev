using System;
using UnityEngine;

namespace YLFramework.Actions
{
    public partial class ActionKit
    {
        /// <summary>创建使用共享遮罩画布的屏幕过渡动作。</summary>
        /// <remarks>需要可用的画布与 colorImage 引用；无场景画布时按资源键 <c>ActionKit/ScreenTransitionCanvas</c> 加载。</remarks>
        public static class ScreenTransition
        {
            /// <summary>创建默认黑色遮罩在一秒内由透明变为不透明的动作。</summary>
            public static ScreenTransitionFade FadeIn() => ScreenTransitionFade.Allocate()
                .FromAlpha(0).ToAlpha(1).Duration(1.0f).Color(Color.black);

            /// <summary>创建默认黑色遮罩在一秒内由不透明变为透明的动作。</summary>
            public static ScreenTransitionFade FadeOut() => ScreenTransitionFade.Allocate()
                .FromAlpha(1).ToAlpha(0).Duration(1.0f).Color(Color.black);

            /// <summary>创建由父序列统一等待、暂停和取消的一秒淡入、回调、一秒淡出组合。</summary>
            /// <param name="onInFinish">遮罩完全淡入后同步执行的可选回调；异常按普通序列和所选执行器处理。</param>
            /// <returns>尚未启动的普通序列；调用方选择执行宿主，自定义时序时显式组合 FadeIn 与 FadeOut。</returns>
            /// <remarks>所有子动作由序列拥有，不隐式创建全局宿主；取消不恢复共享遮罩的透明度。</remarks>
            public static ISequence FadeInOut(Action onInFinish = null) =>
                Sequence().Append(FadeIn()).Callback(onInFinish).Append(FadeOut());
        }
    }
}
