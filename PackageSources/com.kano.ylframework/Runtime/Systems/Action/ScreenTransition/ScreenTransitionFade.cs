using UnityEngine;

namespace YLFramework.Actions
{
    /// <summary>通过共享画布遮罩调整透明度的屏幕过渡动作。</summary>
    /// <remarks>
    /// 请在 Unity 主线程执行，提供可用的画布及 colorImage 绑定；无场景画布时按资源键 <c>ActionKit/ScreenTransitionCanvas</c> 加载。
    /// 所有过渡共用同一遮罩，不保证并行执行互不影响。取消不触发正常完成，也不会自动应用目标透明度。
    /// 池分配只重置执行状态，不恢复颜色、透明度或时长配置；直接 Allocate 后应设置本轮所需配置。
    /// </remarks>
    public class ScreenTransitionFade : AbstractAction<ScreenTransitionFade>
    {
        private Color _color = UnityEngine.Color.black;
        private float _fromAlpha = 0;
        private float _toAlpha = 0;
        private float _duration = 1.0f;
        private float _currentSeconds = 0;

        /// <summary>设置遮罩颜色，并返回当前动作。</summary>
        public ScreenTransitionFade Color(Color color)
        {
            _color = color;
            return this;
        }

        /// <summary>设置起始透明度，并返回当前动作。</summary>
        public ScreenTransitionFade FromAlpha(float alpha)
        {
            _fromAlpha = alpha;
            return this;
        }

        /// <summary>设置目标透明度，并返回当前动作。</summary>
        public ScreenTransitionFade ToAlpha(float alpha)
        {
            _toAlpha = alpha;
            return this;
        }

        /// <summary>设置过渡时长（秒），并返回当前动作。</summary>
        public ScreenTransitionFade Duration(float duration)
        {
            _duration = duration;
            return this;
        }

        /// <summary>启动时应用遮罩颜色与起始透明度，并重置累计时间。</summary>
        public override void OnStart()
        {
            ScreenTransitionCanvas.Instance.colorImage.color = _color;
            SetAlpha(_fromAlpha);
            _currentSeconds = 0;
        }

        /// <summary>按时间增量推进透明度，到达时长时标记完成。</summary>
        /// <param name="dt">本次执行的时间增量，单位为秒。</param>
        public override void OnExecute(float dt)
        {
            _currentSeconds += dt;
            var alpha = Mathf.Lerp(_fromAlpha, _toAlpha, _currentSeconds / _duration);
            SetAlpha(alpha);
            if (_currentSeconds >= _duration)
            {
                this.Finish();
            }
        }

        /// <summary>正常完成时将遮罩透明度设为目标值。</summary>
        public override void OnFinish() => SetAlpha(_toAlpha);

        /// <summary>保留共享遮罩的 RGB 颜色，只写入本次透明度。</summary>
        private static void SetAlpha(float alpha)
        {
            var image = ScreenTransitionCanvas.Instance.colorImage;
            var color = image.color;
            color.a = alpha;
            image.color = color;
        }
    }
}
