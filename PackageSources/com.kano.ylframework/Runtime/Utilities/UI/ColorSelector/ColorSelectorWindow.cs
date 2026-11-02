using System;
using UnityEngine;
using UnityEngine.UI;
using YLFramework.Core;

namespace YLFramework.UI
{
    /// <summary>按需打开的取色工具窗口；复用 Core 的 Module/Panel 生命周期，不创建全局 UI Manager。</summary>
    public sealed class ColorSelectorWindow : Panel
    {
        [SerializeField] private ColorSelector selector;
        [SerializeField] private Button closeButton;
        /// <summary>读取已绑定选择器的当前颜色，不启动会话或触发颜色通知。</summary>
        public Color CurrentColor => selector.CurrentColor;

        /// <summary>在主线程借用并填充本次打开参数，不创建窗口，也不立即校验初始颜色。</summary>
        /// <param name="initial">窗口 OnOpen 时交给选择器的初始颜色，届时按 BeginSession 的范围校验。</param>
        /// <param name="changed">本次取色会话的同步颜色回调，可为 null。</param>
        /// <returns>直接交给本次 Panel 加载操作的参数；由加载操作接管后不得跨请求保存或复用。</returns>
        public static LoadViewArgs Arguments(Color initial, Action<Color> changed = null)
        {
            var args = LoadViewArgs.New();
            args.SetObject("initialColor", initial);
            args.SetObject("colorChanged", changed);
            return args;
        }

        /// <summary>在每次打开回调中借读本次参数，重置选择器的初始颜色与通知对象。</summary>
        protected override void OnOpen(LoadViewArgs args) => Configure(args);
        /// <summary>校验必要的组件绑定，并用打开参数或白色默认值开始取色会话。</summary>
        private void Configure(LoadViewArgs args)
        {
            if (!selector || !closeButton)
            {
                throw new InvalidOperationException("ColorSelectorWindow requires selector and close button bindings.");
            }

            selector.BeginSession(args.GetObject("initialColor") is Color initial ? initial : Color.white,
                args.GetObject("colorChanged") as Action<Color>);
        }

        /// <summary>仅在窗口已加载且输入未被阻挡时响应关闭按钮。</summary>
        private void CloseFromButton()
        {
            if (IsLoaded && !IsInputBlocked)
            {
                Close();
            }
        }
        /// <summary>开放表现事件时订阅关闭按钮，与停止表现时的退订配对。</summary>
        protected override void OnViewEnabled() => closeButton.onClick.AddListener(CloseFromButton);
        /// <summary>停止表现事件时撤销关闭按钮订阅，不在此结束取色会话。</summary>
        protected override void OnViewDisabled()
        {
            if (closeButton)
            {
                closeButton.onClick.RemoveListener(CloseFromButton);
            }
        }
        /// <summary>窗口会话退出或加载回滚时结束取色会话，解除选择器对调用方回调的持有。</summary>
        public override void OnRemove()
        {
            if (selector)
            {
                selector.EndSession();
            }
        }
    }
}
