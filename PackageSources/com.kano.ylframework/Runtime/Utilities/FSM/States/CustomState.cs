using System;

namespace YLFramework.FSM
{
    /// <summary>
    /// 通过委托快速定义状态行为的轻量状态实现，适合普通业务代码即时拼装简单状态。
    /// 各 OnXxx 方法链式返回自身；未设置的委托不会执行；
    /// 同一委托重复设置时以后一次为准（覆盖而非叠加）；Condition 未设置时默认允许切入。
    /// 状态对象由调用方创建并持有，FSM 回收时会清空全部委托（DeInit），之后可重新配置复用。
    /// CustomState 不接收 FSM / 状态 ID / Owner 注入（Init 为空实现）；委托中需要这些引用时请自行捕获外部变量。
    /// </summary>
    public sealed class CustomState : IState
    {
        private Func<bool> _onCondition;
        private Action _onEnter;
        private Action _onUpdate;
        private Action _onFixedUpdate;
        private Action _onLateUpdate;
        private Action _onGui;
        private Action _onExit;

        /// <summary>设置切入条件；未设置时默认允许切入。重复设置以后一次为准。</summary>
        public CustomState OnCondition(Func<bool> onCondition)
        {
            _onCondition = onCondition;
            return this;
        }

        /// <summary>设置切入回调。重复设置以后一次为准。</summary>
        public CustomState OnEnter(Action onEnter)
        {
            _onEnter = onEnter;
            return this;
        }

        /// <summary>设置 Update 回调。重复设置以后一次为准。</summary>
        public CustomState OnUpdate(Action onUpdate)
        {
            _onUpdate = onUpdate;
            return this;
        }

        /// <summary>设置 LateUpdate 回调。重复设置以后一次为准。</summary>
        public CustomState OnLateUpdate(Action onLateUpdate)
        {
            _onLateUpdate = onLateUpdate;
            return this;
        }

        /// <summary>设置 FixedUpdate 回调。重复设置以后一次为准。</summary>
        public CustomState OnFixedUpdate(Action onFixedUpdate)
        {
            _onFixedUpdate = onFixedUpdate;
            return this;
        }

        /// <summary>设置 OnGUI 回调。重复设置以后一次为准。</summary>
        public CustomState OnGUI(Action onGui)
        {
            _onGui = onGui;
            return this;
        }

        /// <summary>设置切出回调。重复设置以后一次为准。</summary>
        public CustomState OnExit(Action onExit)
        {
            _onExit = onExit;
            return this;
        }

        /// <inheritdoc />
        public bool Condition()
        {
            var result = _onCondition?.Invoke();
            return result == null || result.Value;
        }

        /// <inheritdoc />
        public void Init(params object[] args)
        {
        }

        /// <inheritdoc />
        public void DeInit()
        {
            _onCondition = null;
            _onEnter = null;
            _onExit = null;
            _onUpdate = null;
            _onFixedUpdate = null;
            _onLateUpdate = null;
            _onGui = null;
        }

        /// <inheritdoc />
        public void Enter()
        {
            _onEnter?.Invoke();
        }

        /// <inheritdoc />
        public void Update()
        {
            _onUpdate?.Invoke();
        }

        /// <inheritdoc />
        public void LateUpdate()
        {
            _onLateUpdate?.Invoke();
        }

        /// <inheritdoc />
        public void FixedUpdate()
        {
            _onFixedUpdate?.Invoke();
        }

        /// <inheritdoc />
        public void OnGUI()
        {
            _onGui?.Invoke();
        }

        /// <inheritdoc />
        public void Exit()
        {
            _onExit?.Invoke();
        }
    }
}
