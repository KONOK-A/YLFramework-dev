using System;
using System.Collections.Generic;
using UnityEngine;
using YLFramework.Foundation;

namespace YLFramework.Skill
{

    /// <summary>技能释放前的检查结果，失败时表示首先命中的限制。</summary>
    public enum ReleaseSkillStatus
    {
        /// <summary>当前检查允许释放，不表示技能已完成执行。</summary>
        [Tooltip("成功")]
        Success,
        /// <summary>该实例已处于释放中。</summary>
        [Tooltip("该技能正在释放中")]
        Releasing,
        /// <summary>宿主有正在释放且与本技能不允许并行的技能。</summary>
        [Tooltip("有其他的不能同时释放的技能正在释放")]
        OtherReleasing,
        /// <summary>技能自身的 CanRelease 检查未通过。</summary>
        [Tooltip("当前技能释放条件不满足")]
        DonotMeetTheCondition,
        /// <summary>本技能尚未冷却完成。</summary>
        [Tooltip("该技能正在冷却中")]
        InCooling,
        /// <summary>技能未有效挂接，或宿主当前不允许释放。</summary>
        [Tooltip("游戏角色当前的状态不能释放技能")]
        ReleaserCannotRelease
    }

    /// <summary>
    /// 技能基类
    /// </summary>
    /// <remarks>
    /// 管理器为宿主分配池化实例和独立配置副本；本类保存可变计时与释放状态，释放结束后才推进冷却。
    /// 回调同步执行，异常会中断当前通知链；版本检查用于识别移除和复用，不是任意重入的隔离保证。
    /// </remarks>
    public abstract class SkillBase
    {

        #region 字段
        // 能够同时释放的技能
        private List<int> _simultaneousSkillIds = new List<int>();
        // 是否能够主动取消
        private bool _allowCancellation = false;


        private float _fixedTimer = 0;


        private bool _isCooldownComplete = true;

        private float _duration;

        private float _cooldownSeconds;
        private int _version;
        private bool _isRemoving;
        /// <summary>识别实例重置、移除和归还的版本，不随每次释放或中断递增。</summary>
        internal int Version => _version;
        /// <summary>实例尚未进入移除流程且仍有释放者引用。</summary>
        internal bool IsAttached => !_isRemoving && Releaser != null;

        #endregion

        #region 属性

        /// <summary>
        /// 技能释放者(装备当前技能的游戏角色对象)
        /// </summary>
        /// <remarks>由管理器装备时绑定；直接更换引用不会迁移宿主字典或管理器的宿主登记。</remarks>
        public IEquippableSkill Releaser
        {
            get; set;
        }

        /// <summary>
        /// 技能配置数据
        /// </summary>
        /// <remarks>管理器提供当前装备实例的副本；计时属性在 ResetData 时从中复制，之后修改配置不自动同步计时。</remarks>
        public SkillConfigData SkillData
        {
            get; internal set;
        }

        /// <summary>
        /// 技能是否冷却完成(如果想要重置技能cd,可以直接把该属性设置为true)
        /// </summary>
        /// <remarks>标记变化时重置冷却计时；转为 true 后先调用 CooldownCompleted，再按版本检查调用冷却完成钩子。</remarks>
        public bool IsCooldown
        {
            get
            {
                return _isCooldownComplete;
            }
            set
            {
                if (_isCooldownComplete == value)
                {
                    return;
                }

                _isCooldownComplete = value;
                CooldownTimer = value ? Math.Max(0, CoolingTime) : 0;

                if (_isCooldownComplete)
                {
                    var version = _version;
                    CooldownCompleted?.Invoke(this);
                    if (version == _version)
                    {
                        OnCooldownComplete();
                    }
                }

            }
        }

        /// <summary>
        /// 已冷却的时间
        /// </summary>
        public float CooldownTimer
        {
            get; set;
        }

        /// <summary>
        /// 冷却时间
        /// </summary>
        /// <remarks>单位为秒，运行值独立于配置副本；实际冷却仅在非释放状态推进。</remarks>
        public virtual float CoolingTime
        {
            get
            {
                return _cooldownSeconds;
            }
            set
            {
                _cooldownSeconds = value;
            }
        }

        /// <summary>
        /// 是否正在释放中
        /// </summary>
        public bool IsReleasing
        {
            get; protected set;
        }

        /// <summary>
        /// 已释放的时间
        /// </summary>
        public float ReleasedTimer
        {
            get; protected set;
        }

        /// <summary>
        /// 持续时间
        /// </summary>
        /// <remarks>单位为秒；达到该时长后还需 IsComplete 返回 true 才自然结束。</remarks>
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
        /// 冷却进度
        /// </summary>
        public float CooldownProgress
        {
            get
            {
                if (IsCooldown || CoolingTime <= 0)
                {
                    return 1;
                }

                return Mathf.Clamp01(CooldownTimer / CoolingTime);
            }
        }

        /// <summary>
        /// 释放进度(0:开始释放 1:释放结束)
        /// </summary>
        /// <remarks>持续时间为 0 时固定为 0；自然结束和中断都会清零已释放时间，不能以本值判定是否结束。</remarks>
        public float ReleasingProgress
        {
            get
            {

                if (Duration == 0)
                {
                    return 0;
                }

                return Mathf.Clamp01(ReleasedTimer / Duration);
            }
        }

        /// <summary>
        /// 能够同时释放的技能的id集合
        /// </summary>
        /// <remarks>检查时任一方列表允许对方 ID 即可并行；这里直接暴露运行列表，不修改源配置。</remarks>
        public List<int> CanbeReleasedAtTheSameTime
        {
            get
            {
                return _simultaneousSkillIds;
            }
            set
            {
                this._simultaneousSkillIds = value;
            }
        }

        /// <summary>
        /// 是否能够主动取消
        /// </summary>
        public bool ActiveCancellation
        {
            get
            {
                return _allowCancellation;
            }
            set
            {
                _allowCancellation = value;
            }
        }

        /// <summary>
        /// 获取当前技能释放状态, Success表示该技能可以释放
        /// </summary>
        /// <remarks>依次检查挂接与宿主、已在释放、冷却、自身条件和并行限制；读取本属性也会调用业务检查。</remarks>
        public ReleaseSkillStatus ReleaseSkillStatus
        {
            get
            {
                if (!IsAttached || !Releaser.CanRelease())
                {
                    return ReleaseSkillStatus.ReleaserCannotRelease;
                }

                if (IsReleasing)
                {
                    return ReleaseSkillStatus.Releasing;
                }

                if (!IsCooldown)
                {
                    return ReleaseSkillStatus.InCooling;
                }

                if (!CanRelease())
                {
                    return ReleaseSkillStatus.DonotMeetTheCondition;
                }

                if (!IsCanbeReleaseAtSameTime())
                {
                    return ReleaseSkillStatus.OtherReleasing;
                }

                return ReleaseSkillStatus.Success;
            }
        }

        #endregion

        #region 事件

        /// <summary>非释放状态开始推进冷却且计时为零时调用，不在 Release 内立即调用。</summary>
        [Tooltip("开始冷却的事件回调")]
        public Action<SkillBase> CooldownStarted;

        /// <summary>冷却计时增加后同步通知实例与当前进度，先于 OnCooling。</summary>
        [Tooltip("冷却中的事件回调")]
        public Action<SkillBase, float> Cooling;

        /// <summary>冷却标记转为完成后同步调用，先于 OnCooldownComplete。</summary>
        [Tooltip("冷却完成的事件回调")]
        public Action<SkillBase> CooldownCompleted;

        /// <summary>OnRelease 返回且实例未移除或复用时调用，此时释放状态已预先提交。</summary>
        [Tooltip("开始释放的事件回调")]
        public Action<SkillBase> ReleaseStarted;

        /// <summary>释放计时增加后通知实例与当前进度，先于 OnUpdate 和自然结束判定。</summary>
        [Tooltip("释放中的事件回调")]
        public Action<SkillBase, float> Releasing;

        /// <summary>自然结束并清零释放计时后调用，先于 OnReleaseComplete；中断不调用本委托。</summary>
        [Tooltip("释放完成的事件回调")]
        public Action<SkillBase> ReleaseCompleted;



        /// <summary>清除释放状态与计时后调用，随后依次尝试 OnInterrupt 和 OnReleaseComplete。</summary>
        [Tooltip("中断的事件回调")]
        public Action<SkillBase> Interrupted;

        #endregion

        #region 方法

        /// <summary>使用当前帧增量推进释放或冷却。</summary>
        internal void Update() => Advance(Time.deltaTime);

        /// <summary>推进释放与完成判定，或在非释放状态推进冷却，并在回调后检查实例版本。</summary>
        internal void Advance(float deltaTime)
        {
            if (!IsAttached)
            {
                return;
            }

            var version = _version;
            if (IsReleasing)
            {
                // 释放
                ReleasedTimer += deltaTime;

                Releasing?.Invoke(this, ReleasingProgress);
                if (version != _version || !IsAttached)
                {
                    return;
                }

                OnUpdate();
                if (version != _version || !IsAttached)
                {
                    return;
                }

                if (ReleasedTimer >= Duration && IsComplete())
                {
                    ReleasedTimer = 0;
                    IsReleasing = false;
                    ReleaseCompleted?.Invoke(this);
                    if (version != _version || !IsAttached)
                    {
                        return;
                    }
                    // 释放结束
                    OnReleaseComplete();
                }

            }
            else
            {
                // 判断是否正在冷却

                if (!IsCooldown)
                {
                    if (CooldownTimer == 0)
                    {
                        CooldownStarted?.Invoke(this);
                        if (version != _version || !IsAttached)
                        {
                            return;
                        }
                    }

                    CooldownTimer += deltaTime;
                    Cooling?.Invoke(this, CooldownProgress);
                    if (version != _version || !IsAttached)
                    {
                        return;
                    }
                    // 冷却中
                    OnCooling();
                    if (version != _version || !IsAttached)
                    {
                        return;
                    }

                    if (CooldownTimer >= CoolingTime)
                    {
                        IsCooldown = true;

                    }
                }

            }
        }


        /// <summary>限制固定步计时领先于释放计时的程度，再调用固定步业务钩子。</summary>
        internal void FixedUpdate()
        {
            // 卡顿补固定步时限制其领先幅度，避免固定步表现过度超前；并不保证与 Update 一一对应。
            if (_fixedTimer - ReleasedTimer > Time.fixedDeltaTime * 2)
            {
                return;
            }

            _fixedTimer += Time.fixedDeltaTime;
            OnFixedUpdate();
        }

        /// <summary>
        /// 释放技能
        /// </summary>
        /// <returns>未通过检查时返回限制原因；已接受释放则返回 Success，即使回调中移除了技能。</returns>
        /// <remarks>先重置计时、标记未冷却与释放中，再调用 OnRelease 和开始通知；本调用不推进冷却时间。</remarks>
        public ReleaseSkillStatus Release()
        {
            if (ReleaseSkillStatus != ReleaseSkillStatus.Success)
            {
                return ReleaseSkillStatus;
            }

            CooldownTimer = 0;
            IsCooldown = false;
            IsReleasing = true;
            ReleasedTimer = 0;
            _fixedTimer = 0;
            var version = _version;
            OnRelease();
            if (version != _version || !IsAttached)
            {
                return ReleaseSkillStatus.Success;
            }
            // 触发事件
            ReleaseStarted?.Invoke(this);

            return ReleaseSkillStatus.Success;
        }

        /// <summary>
        /// 取消释放(前提是该技能可以主动取消)
        /// </summary>
        /// <remarks>满足主动取消开关且正在释放时走 Interrupt，不重置冷却状态。</remarks>
        public void Cancel()
        {
            if (ActiveCancellation && IsReleasing)
            {
                Interrupt();
            }
        }

        /// <summary>
        /// 中断技能
        /// </summary>
        /// <remarks>不受主动取消开关限制；保留冷却状态，不发自然完成通知。回调异常或版本变化会阻止后续钩子。</remarks>
        public void Interrupt()
        {
            if (!IsReleasing)
            {
                return;
            }

            IsReleasing = false;
            ReleasedTimer = 0;
            var version = _version;

            Interrupted?.Invoke(this);
            if (version != _version)
            {
                return;
            }

            OnInterrupt();
            if (version != _version)
            {
                return;
            }

            OnReleaseComplete();
        }

        /// <summary>
        /// 判断当前技能是否能够释放(默认:true,如果该技能在某种特殊情况下不能释放,可以重写此方法,返回false即可!)
        /// </summary>
        /// <returns>当前技能自身的释放条件是否满足；基类为 true。</returns>
        protected virtual bool CanRelease()
        {

            return true;
        }


        /// <summary>
        /// 判断技能是否释放完成(适用于不是根据持续时间来决定是否结束的技能,返回true代表结束!)
        /// </summary>
        /// <returns>达到 Duration 后是否允许自然结束；基类为 true，不会提前绕过持续时间。</returns>
        protected virtual bool IsComplete()
        {
            return true;
        }


        /// <summary>
        /// 检查宿主正在释放的技能，双方任一列表允许对方 ID 即可并行，同 ID 不允许重复释放。
        /// </summary>
        private bool IsCanbeReleaseAtSameTime()
        {
            foreach (var item in Releaser.Skills.Values)
            {
                // 如果技能没在释放中 直接判断下一个
                if (!item.IsReleasing)
                {
                    continue;
                }
                // 如果正在释放中的技能是自己 说明当前这个技能正在释放中 直接return false;
                if (item.SkillData.id == this.SkillData.id)
                {
                    return false;
                }

                bool release = CanbeReleasedAtTheSameTime.Contains(item.SkillData.id);
                release |= item.CanbeReleasedAtTheSameTime.Contains(SkillData.id);

                // 如果不能同时释放 直接return false;
                if (!release)
                {
                    return false;
                }
            }

            return true;
        }


        /// <summary>
        /// 重置能够同时释放的技能id集合
        /// </summary>
        public void ResetCanbeReleasedAtTheSameTime()
        {
            if (_simultaneousSkillIds == null)
            {
                _simultaneousSkillIds = new List<int>();
            }

            _simultaneousSkillIds.Clear();
            _simultaneousSkillIds.AddRange(SkillData.simultaneousSkillIds);
        }

        /// <summary>
        /// 重置是否能够主动取消
        /// </summary>
        public void ResetActiveCancellation()
        {
            ActiveCancellation = SkillData.allowCancellation;
        }

        /// <summary>从配置副本重建运行设置，清空全部基类回调与计时，并开启新的挂接版本。</summary>
        internal void ResetData()
        {
            _version++;
            _isRemoving = false;
            _duration = SkillData.duration;
            _cooldownSeconds = SkillData.cooldownSeconds;
            ResetCanbeReleasedAtTheSameTime();
            ResetActiveCancellation();
            _isCooldownComplete = true;
            CooldownTimer = 0;
            IsReleasing = false;
            ReleasedTimer = 0;
            _fixedTimer = 0;

            CooldownStarted = null;
            Cooling = null;
            CooldownCompleted = null;

            ReleaseStarted = null;
            Releasing = null;
            ReleaseCompleted = null;

            Interrupted = null;
        }

        /// <summary>标记开始移除并使旧版本失效，在中断和移除业务钩子前阻止再次释放。</summary>
        internal void BeginRemove()
        {
            _isRemoving = true;
            _version++;
        }

        /// <summary>归还池时解除宿主和配置引用，清空运行状态及基类回调，不代替派生类资源清理。</summary>
        internal void ClearRuntime()
        {
            _version++;
            _isRemoving = true;
            Releaser = null;
            SkillData = null;
            IsReleasing = false;
            _isCooldownComplete = true;
            CooldownTimer = ReleasedTimer = _fixedTimer = 0;
            _simultaneousSkillIds.Clear();
            CooldownStarted = null;
            Cooling = null;
            CooldownCompleted = null;
            ReleaseStarted = null;
            Releasing = null;
            ReleaseCompleted = null;
            Interrupted = null;
        }

        #endregion



        #region 生命周期

        /// <summary>
        /// 添加技能时调用
        /// </summary>
        /// <remarks>宿主字典、配置副本、运行设置和更新订阅均已准备；派生类可在此登记自身资源。</remarks>
        internal protected virtual void OnAdd()
        {
        }

        /// <summary>
        /// 移除技能时调用
        /// </summary>
        /// <remarks>先从宿主字典移除并尝试中断，再调用本钩子；归还池前仍可读取宿主和配置，应在此清理派生类资源。</remarks>
        internal protected virtual void OnRemove()
        {
        }

        /// <summary>
        /// 当技能释放的时候调用
        /// </summary>
        /// <remarks>运行计时和状态已重置，开始释放通知尚未发送。</remarks>
        protected virtual void OnRelease()
        {
        }

        /// <summary>
        /// 当技能释放结束时调用(中断技能时也会触发该回调)
        /// </summary>
        /// <remarks>自然结束在 ReleaseCompleted 之后调用；中断在 OnInterrupt 之后调用，均受前序异常和版本检查影响。</remarks>
        protected virtual void OnReleaseComplete()
        {
        }

        /// <summary>
        /// 当技能正在冷却中调用
        /// </summary>
        /// <remarks>在 Cooling 通知之后、当次冷却完成判定之前调用。</remarks>
        protected virtual void OnCooling()
        {
        }

        /// <summary>
        /// 当技能冷却完成时调用
        /// </summary>
        /// <remarks>在 CooldownCompleted 返回且实例版本未变时调用。</remarks>
        protected virtual void OnCooldownComplete()
        {
        }

        /// <summary>
        /// 当技能中断时调用
        /// </summary>
        /// <remarks>在 Interrupted 通知之后、结束钩子之前调用。</remarks>
        protected virtual void OnInterrupt()
        {
        }

        /// <summary>
        /// Update回调(仅在技能正在释放中时才会触发)
        /// </summary>
        protected virtual void OnUpdate()
        {
        }

        /// <summary>
        /// FixedUpdate回调(仅在技能正在释放中时才会触发)
        /// </summary>
        internal protected virtual void OnFixedUpdate()
        {
        }
        /// <summary>
        /// LateUpdate回调(仅在技能正在释放中时才会触发)
        /// </summary>
        internal protected virtual void OnLateUpdate()
        {
        }


        #endregion

    }
}
