using System;
using System.Collections.Generic;
using UnityEngine;

namespace YLFramework.StateMachine
{
    /// <summary>
    /// 运行时状态节点，持有按配置顺序创建的状态脚本，并协调逐脚本创建和进入、退出回调。
    /// </summary>
    public class FsmStateNode
    {
        #region 字段
        /// <summary>
        /// 所属配置克隆中的节点数据。
        /// </summary>
        public FsmStateNodeData Data;
        internal RuntimeFsmControllerInstance Instance;
        private List<FsmState> _states = new List<FsmState>();

        internal string LastState;
        internal string NextState;
        /// <summary>
        /// 是否正在执行中
        /// </summary>
        internal bool IsRunning = false;

        private readonly FsmCreationTracker _creation;
        private int _activationVersion;

        /// <summary>构造时由节点名称计算的哈希。</summary>
        [Tooltip("当前状态名称Hash,可通过方法FSMController.StringToHash()转换!")]
        public int NameHash;

        #endregion

        #region 方法
        /// <summary>反射构造节点的普通状态对象，并绑定宿主、共享数据和节点引用，暂不调用生命周期。</summary>
        /// <param name="data">配置克隆中的节点定义。</param>
        /// <param name="instance">拥有此节点的运行时实例。</param>
        internal FsmStateNode(FsmStateNodeData data, RuntimeFsmControllerInstance instance)
        {
            this.Data = data;
            this.Instance = instance;
            NameHash = FsmController.StringToHash(data.name);

            _states.Clear();
            foreach (var item in this.Data.stateScripts)
            {
                Type type = YLFramework.Foundation.AssemblyTool.GetType(item.className);
                FsmState state = null;
                if (type != null)
                {
                    state = Activator.CreateInstance(type) as FsmState;
                }

                if (state != null)
                {
                    state.Controller = instance.FsmController;
                    state.UserData = instance.FsmController.UserDataValue;
                    state.CurrentStateInfo = this;
                    _states.Add(state);
                }
            }
            _creation = new FsmCreationTracker(_states.Count);

        }

        /// <summary>按脚本顺序执行首次创建及本轮进入；激活版本变化后停止旧轮次。</summary>
        internal void OnEnter()
        {
            IsRunning = true;
            int version = ++_activationVersion;
            for (int i = 0; i < _states.Count; i++)
            {
                var item = _states[i];
                if (!IsRunning || version != _activationVersion || !Instance.HasStarted)
                {
                    break;
                }

                item.LastState = LastState;
                item.NextState = NextState;

                if (_creation.TryMarkCreated(i))
                {
                    item.OnCreate();
                }

                if (!IsRunning || version != _activationVersion || !Instance.HasStarted)
                {
                    break;
                }

                item.OnEnter();
            }

        }

        /// <summary>先终止当前激活，再退出所有已创建脚本；收集首个异常并在其余脚本退出后重抛。</summary>
        internal void OnExit()
        {
            IsRunning = false;
            _activationVersion++;
            Exception firstError = null;
            for (int i = 0; i < _states.Count; i++)
            {
                if (!_creation.IsCreated(i))
                {
                    continue;
                }

                var item = _states[i];
                item.LastState = LastState;
                item.NextState = NextState;
                try
                {
                    item.OnExit();
                }
                catch (Exception error)
                {
                    firstError ??= error;
                }
            }
            if (firstError != null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(firstError).Throw();
            }
        }

        /// <summary>按配置顺序转发逻辑更新，节点或实例停止后不再访问后续脚本。</summary>
        internal void OnUpdate()
        {
            foreach (var item in this._states)
            {
                if (!IsRunning || !Instance.HasStarted)
                {
                    break;
                }

                item.OnUpdate();
            }
        }

        /// <summary>向仍在运行的节点脚本转发固定步长更新。</summary>
        internal void OnFixedUpdate()
        {
            foreach (var item in this._states)
            {
                if (!IsRunning || !Instance.HasStarted)
                {
                    break;
                }

                item.OnFixedUpdate();
            }
        }

        /// <summary>向仍在运行的节点脚本转发后置更新。</summary>
        internal void OnLateUpdate()
        {
            foreach (var item in this._states)
            {
                if (!IsRunning || !Instance.HasStarted)
                {
                    break;
                }

                item.OnLateUpdate();
            }
        }

        #endregion
    }

}
