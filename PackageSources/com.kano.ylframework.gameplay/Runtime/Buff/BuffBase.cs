using System;
using UnityEngine;
using YLFramework.Foundation;

namespace YLFramework.Buff
{
    /// <summary>
    /// 承载单个 Buff 的运行状态与生命周期钩子，由 BuffManager 推进时间并回收。
    /// </summary>
    public class BuffBase
    {
        #region 字段

        /// <summary>本次添加传入的业务数据；实例归还时清空引用，不负责释放该对象。</summary>
        [Tooltip("用户自定义数据")]
        public object UserData;

        private float _fixedUpdateTimer;

        private float _duration;
        private int _version;
        internal int Version => _version;

        #endregion

        #region 属性

        /// <summary>
        /// 拥有当前Buff的游戏角色对象
        /// </summary>
        public IAddableBuff Owner
        {
            get; internal set;
        }

        /// <summary>
        /// 本次 Buff 的配置副本，效果也已独立复制；不是配表中的共享定义。
        /// </summary>
        public BuffConfigData BuffData
        {
            get; internal set;
        }

        /// <summary>
        /// Buff 已累计的 Update 时间，单位为秒；宿主停用而暂停分发时不增长。
        /// </summary>
        public float ExistedTime
        {
            get; private set;
        }

        /// <summary>
        /// 本次实例的持续秒数；小于等于零时不因时间到期而自动移除。
        /// </summary>
        public virtual float Duration
        {
            get
            {
                return _duration;
            }
            set
            {
                _duration = value;
            }
        }

        /// <summary>
        /// 已存在时间与持续时间的比值，不限制上界；持续时间小于等于零时返回 -1。
        /// </summary>
        public float Progress
        {
            get
            {
                if (Duration <= 0)
                {
                    return -1;
                }

                return ExistedTime / Duration;
            }
        }

        #endregion

        #region 方法

        /// <summary>
        /// 当Buff被添加时调用
        /// </summary>
        public virtual void OnAdd()
        {
        }

        /// <summary>
        /// 当Buff被移除时调用
        /// </summary>
        public virtual void OnRemove()
        {
        }

        internal void Update() => Advance(UnityEngine.Time.deltaTime);
        internal void Advance(float deltaTime)
        {
            ExistedTime += deltaTime;
            OnUpdate();
        }

        /// <summary>按固定步长调用钩子；固定计时已比 Update 计时领先两个步长以上时跳过。</summary>
        internal void FixedUpdate()
        {
            if (_fixedUpdateTimer - ExistedTime > UnityEngine.Time.fixedDeltaTime * 2)
            {
                return;
            }

            _fixedUpdateTimer += UnityEngine.Time.fixedDeltaTime;
            OnFixedUpdate();
        }

        /// <summary>为一次新的添加重置计时和版本；宿主与外部数据随后由管理器设置。</summary>
        internal void ResetData()
        {
            _version++;
            _duration = BuffData.duration;
            ExistedTime = 0;
            Owner = null;
            _fixedUpdateTimer = 0;
        }

        /// <summary>
        /// 在本次 Update 时间累计后执行派生逻辑，随后由管理器判断是否到期。
        /// </summary>
        protected virtual void OnUpdate()
        {
        }

        /// <summary>
        /// 固定更新钩子，由管理器经实例的计时门限调用。
        /// </summary>
        public virtual void OnFixedUpdate()
        {
        }

        /// <summary>
        /// 在宿主参与 LateUpdate 分发时执行派生逻辑，不额外累计存在时间。
        /// </summary>
        public virtual void OnLateUpdate()
        {
        }

        /// <summary>改变版本，使移除前取得的分发快照失效。</summary>
        internal void Detach()
        {
            _version++;
        }
        /// <summary>归还对象池时清空框架持有的运行状态；派生类自有状态仍须自行管理。</summary>
        internal void ClearRuntime()
        {
            _version++;
            Owner = null;
            BuffData = null;
            UserData = null;
            ExistedTime = _fixedUpdateTimer = _duration = 0;
        }

        /// <summary>
        /// 移除当前buff
        /// </summary>
        public void Remove()
        {
            BuffManager.RemoveBuff(Owner, this);
        }

        /// <summary>
        /// 判断当前buff是否能够添加
        /// </summary>
        /// <returns>返回 true 允许添加；默认允许，调用时宿主、配置副本和业务数据已设置。</returns>
        public virtual bool CanAdd()
        {
            return true;
        }

        #endregion

    }
}
