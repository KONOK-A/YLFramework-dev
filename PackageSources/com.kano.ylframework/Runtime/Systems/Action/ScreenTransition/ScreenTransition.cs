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

            /// <summary>创建先显示黑色遮罩、再移除遮罩的过渡组合。</summary>
            /// <remarks>内部序列不受父动作取消控制，结束后也不会自动完成父动作。</remarks>
            public static ScreenTransitionInOut<ScreenTransitionFade, ScreenTransitionFade> FadeInOut() =>
                ScreenTransitionInOut<ScreenTransitionFade, ScreenTransitionFade>.Allocate(FadeIn(), FadeOut());
        }
    }
}
