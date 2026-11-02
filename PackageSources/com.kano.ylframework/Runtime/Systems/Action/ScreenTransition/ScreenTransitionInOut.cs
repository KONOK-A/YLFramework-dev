using System;

namespace YLFramework.Actions
{
    /// <summary>配置并启动入场、出场动作的过渡组合。</summary>
    /// <typeparam name="TIn">入场动作类型。</typeparam>
    /// <typeparam name="TOut">出场动作类型。</typeparam>
    /// <remarks>
    /// 内部通过 StartGlobal 启动独立序列，需要播放模式下的全局宿主。
    /// 父动作暂停或取消不会控制已启动的子序列；取消不触发正常完成，子序列结束也不会自动完成父动作。
    /// 当前类型不在回收时清除配置回调与子动作引用；带参 Allocate 仅覆盖传入的两个子动作，不重置已有回调。
    /// </remarks>
    public class ScreenTransitionInOut<TIn, TOut> : AbstractAction<ScreenTransitionInOut<TIn, TOut>>
        where TIn : IAction
        where TOut : IAction
    {
        private Action<TIn> _onSetIn;
        private Action<TOut> _onSetOut;
        private TIn _in;
        private TOut _out;
        private Action _onInFinish;

        /// <summary>从池分配组合，并保存传入的入场、出场动作。</summary>
        /// <param name="transitionIn">入场动作，由启动时另建的全局序列执行。</param>
        /// <param name="transitionOut">出场动作，在入场动作和 OnInFinish 回调之后执行。</param>
        /// <returns>尚未启动、已绑定两个子动作的过渡组合。</returns>
        public static ScreenTransitionInOut<TIn, TOut> Allocate(TIn transitionIn, TOut transitionOut)
        {
            var transitionInOut = Allocate();
            transitionInOut._in = transitionIn;
            transitionInOut._out = transitionOut;
            return transitionInOut;
        }

        /// <summary>设置入场完成、出场开始前调用的回调，并返回当前组合。</summary>
        public ScreenTransitionInOut<TIn, TOut> OnInFinish(Action onInFinish)
        {
            _onInFinish = onInFinish;
            return this;
        }

        /// <summary>设置启动时配置入场动作的回调，并返回当前组合。</summary>
        public ScreenTransitionInOut<TIn, TOut> In(Action<TIn> onSetIn)
        {
            _onSetIn = onSetIn;
            return this;
        }

        /// <summary>设置启动时配置出场动作的回调，并返回当前组合。</summary>
        public ScreenTransitionInOut<TIn, TOut> Out(Action<TOut> onSetOut)
        {
            _onSetOut = onSetOut;
            return this;
        }

        /// <summary>依次应用两个配置回调，并在全局宿主上启动独立子序列。</summary>
        public override void OnStart()
        {
            _onSetIn?.Invoke(_in);
            _onSetOut?.Invoke(_out);
            ActionKit.Sequence().Append(_in).Callback(() => _onInFinish?.Invoke()).Append(_out).StartGlobal();
        }
    }
}
