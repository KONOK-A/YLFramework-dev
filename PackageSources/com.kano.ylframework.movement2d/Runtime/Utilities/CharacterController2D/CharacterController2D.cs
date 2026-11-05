using System;
using System.Collections.Generic;
using UnityEngine;
using YLFramework.Extensions;

namespace YLFramework.Character2D
{
    /// <summary>通过接触判定、短距离形状投射和排队位移控制二维刚体角色。</summary>
    /// <remarks>Update 刷新接触状态，FixedUpdate 积分竖直速度并提交位移；投射结果不用于裁剪所有移动。</remarks>
    [RequireComponent(typeof(Rigidbody2D))]
    public class CharacterController2D : MonoBehaviour
    {
        #region 常量
        /// <summary>接触状态复核所用的形状投射距离，不是从移动量中扣除的 skin。</summary>
        private const float Distance = 0.1F;
        #endregion


        #region 字段

        // 三向检测共用本实例缓冲区；只有投射返回数量内的元素有效，下次投射会覆盖。
        internal readonly RaycastHit2D[] Hits = new RaycastHit2D[10];

        // GetContacts 会复用此数组，检测器持有的引用不是独立接触快照。
        internal readonly ContactPoint2D[] Contacts = new ContactPoint2D[10];
        private bool _isColliderReady;
        private bool HasValidColliders => _isColliderReady && IsUsable(Top) && IsUsable(Bottom) && IsUsable(Left) && IsUsable(Right);
        /// <summary>停用、碰撞体失效或显式刷新时递增的物理处理版本。</summary>
        internal int PhysicsVersion
        {
            get; private set;
        }
        /// <summary>确认回调后角色仍存活、启用且处于同一轮有效物理状态。</summary>
        /// <param name="version">进入当前处理流程时记录的版本。</param>
        /// <returns>是否可以继续使用本轮状态。</returns>
        internal bool IsPhysicsCurrent(int version) => this && isActiveAndEnabled && HasValidColliders && version == PhysicsVersion;
        /// <summary>判断碰撞体是否存活、启用、非触发器且位于激活层级中。</summary>
        /// <param name="collider">待检查的附属碰撞体。</param>
        private static bool IsUsable(Collider2D collider) => collider && collider.enabled && !collider.isTrigger && collider.gameObject.activeInHierarchy;

        /// <summary>检查已选碰撞体，失效时归还刚体重力并丢弃旧状态。</summary>
        private bool EnsureColliders()
        {
            if (HasValidColliders)
            {
                return true;
            }

            if (_isColliderReady)
            {
                _isColliderReady = false;
                ReleaseGravityAndResetState();
            }
            return false;
        }

        private int _contactCount;

        internal LayerMask CheckLayerMask;

        private int _currentLayer = 0;

        private Rigidbody2D _rigidbody2d;

        internal int CheckCount; // 测试用
#if UNITY_EDITOR
        private int _lastSecond;
#endif

        internal Collider2D Top = null;
        internal Collider2D Bottom = null;
        internal Collider2D Left = null;
        internal Collider2D Right = null;

        /// <summary>是否在启用地面检测时按地面法线调整角色旋转。</summary>
        [Tooltip("是否自动旋转?如果开启，当前角色会根据地面倾斜角度自动旋转,始终保持与地面垂直")]
        public bool autoRotation = false;

        // 是否在空中 用于自动控制旋转角度
        private bool _isInTheAir = false;

        [SerializeField]
        [Tooltip("是否绘制调试的线条?如果开启，可以在场景中看到接触点的法线，角度等信息")]
        private bool showDebugLine = true;
        /// <summary>供调试绘制读取的线条显示开关。</summary>
        public bool ShowDebugLine => showDebugLine;

        [Tooltip("是否检测角色当前是否在地面上(默认检测)")]
        [SerializeField]
        private bool isCheckGround = true;

        [Tooltip("是否检测角色当前是否接触墙面(默认不检测)")]
        [SerializeField]
        private bool isCheckWall = false;

        [Tooltip(" 是否检测角色头顶是否碰到游戏物体(默认不检测)")]
        [SerializeField]
        private bool isCheckTop = false;

        private Vector2 _moveDelta;

        private Vector2 _lastPosition;

        internal float SpeedVertical;

        /// <summary>组件积分竖直速度时使用的重力倍率，仅在开启地面检测时生效。</summary>
        [Tooltip("当前角色受到的重力大小(仅在开启检测地面时有效,如果未开启检测地面,当前角色不会受到重力影响!)")]
        public float gravityScale = 1;
        /// <summary>组件积分下落速度时采用的速度上限大小。</summary>
        [global::UnityEngine.Serialization.FormerlySerializedAs("MaxFallingVelocity")]
        [Tooltip("下落的最大速度(默认:20)")]
        [Range(1, 100)]
        public float maxFallingVelocity = 20;

        private float _rigidbodyGravityScale = 0;

        private CheckGround _checkGround;

        private CheckWall _checkWall;

        private CheckTop _checkTop;

        /// <summary>地面法线相对世界向上的夹角阈值，判定使用严格小于比较。</summary>
        [Tooltip("地面倾斜角度,0代表水平无倾斜,如果为30,则是倾斜角度在30以内的地面,才会被认为是地面!")]
        [Range(1, 45)]
        public float groundAngle = 35;

        /// <summary>墙面法线相对世界水平轴的夹角阈值，判定使用严格小于比较。</summary>
        [Tooltip("墙面倾斜角度,0代表竖直向上,如果为30,则是竖直向上往左或往右偏移30度以内认为是墙面!")]
        [Range(1, 45)]
        public float wallAngle = 20;


        /// <summary>顶部法线相对世界向下的夹角阈值，判定使用严格小于比较。</summary>
        [Tooltip("角色头顶碰到的游戏物体的倾斜角度,在该角度范围内，认为碰到游戏物体!")]
        [Range(1, 60)]
        public float topAngle = 45;

        private bool _isGrounded;

        private bool _isTouchingWall;

        private bool _isTouchingCeiling;

        private Vector2 _velocity;

        /// <summary>落地重置计数前允许成功发起的跳跃次数上限。</summary>
        [global::UnityEngine.Serialization.FormerlySerializedAs("MaxJumpCount")]
        [Tooltip("角色最多连续跳跃次数")]
        public int maxJumpCount = 1;

        private int _currentJumpCount = 0;

        private bool _isJumping = false;
        private float _jumpAbortSpeedReduction = 0;
        #endregion

        #region 属性

        /// <summary>最近的落地判定；读取时若竖直速度向上，会直接清除落地标记而不发事件。</summary>
        public bool IsGrounded
        {
            get
            {
                if (SpeedVertical > 0)
                {
                    _isGrounded = false;
                    return false;
                }

                return _isGrounded;
            }
            internal protected set
            {
                bool changed = _isGrounded != value;
                _isGrounded = value;
                if (changed)
                {
                    onGroundValueChange?.Invoke(value);
                }
            }
        }

        /// <summary>最近成功地面投射的命中信息；未落地时可能仍保留旧值。</summary>
        public RaycastHit2D GroundInfo
        {
            get; internal protected set;
        }

        /// <summary>最近的墙面接触判定，不代表本帧已重新投射。</summary>
        public bool IsTouchingWall
        {
            get
            {
                return _isTouchingWall;
            }
            internal protected set
            {
                bool changed = _isTouchingWall != value;
                _isTouchingWall = value;
                if (changed)
                {
                    onTouchWallValueChange?.Invoke(value);
                }
            }
        }

        /// <summary>最近成功墙面投射的命中信息；未触墙时可能仍保留旧值。</summary>
        public RaycastHit2D WallInfo
        {
            get; internal protected set;
        }

        /// <summary>最近的顶部接触判定，不代表本帧已重新投射。</summary>
        public bool IsTouchingCeiling
        {
            get
            {
                return _isTouchingCeiling;
            }
            internal protected set
            {

                bool changed = _isTouchingCeiling != value;
                _isTouchingCeiling = value;
                if (changed)
                {
                    onTopValueChange?.Invoke(value);
                }
            }
        }

        /// <summary>最近成功顶部投射的命中信息；未触顶时可能仍保留旧值。</summary>
        public RaycastHit2D TopInfo
        {
            get; internal protected set;
        }


        /// <summary>提交移动时按已观测刚体位置差计算的速度，不是当前排队位移。</summary>
        /// <remarks>排队位移各分量都不超过 0.01 时直接置零；它不等同于刚体自身的速度属性。</remarks>
        public Vector2 Velocity
        {
            get
            {
                return _velocity;
            }
            internal protected set
            {
                bool changed = _velocity != value;
                _velocity = value;
                if (changed)
                {
                    onVelocityValueChange?.Invoke(value);
                }
            }
        }

        /// <summary>取得并缓存同一对象上的二维刚体。</summary>
        public Rigidbody2D Rigidbody
        {
            get
            {
                if (_rigidbody2d == null)
                {
                    _rigidbody2d = GetComponent<Rigidbody2D>();
                }

                return _rigidbody2d;
            }
        }


        /// <summary>是否检测地面并由组件管理竖直运动；关闭时清除落地状态、竖直速度并恢复原重力。</summary>
        public bool ChecksGround
        {
            get
            {
                return isCheckGround;
            }
            set
            {
                isCheckGround = value;
                if (isCheckGround == false)
                {
                    IsGrounded = false;
                    ResetSpeedVertical();
                    Rigidbody.gravityScale = _rigidbodyGravityScale;
                }
            }
        }

        /// <summary>是否刷新墙面判定；关闭时清除触墙标记，但不清除最近命中信息。</summary>
        public bool ChecksWalls
        {
            get
            {
                return isCheckWall;
            }
            set
            {
                isCheckWall = value;
                if (isCheckWall == false)
                {
                    IsTouchingWall = false;
                }
            }
        }

        /// <summary>是否刷新顶部判定；关闭时清除触顶标记，移动时仍可用接触信息截停上升。</summary>
        public bool ChecksCeiling
        {
            get
            {
                return isCheckTop;
            }
            set
            {
                isCheckTop = value;
                if (isCheckTop == false)
                {
                    IsTouchingCeiling = false;
                }
            }
        }

        /// <summary>当前连续跳跃计数；设置新值后才通知监听者，设置本身不校验次数范围。</summary>
        public int CurrentJumpCount
        {
            get
            {
                return _currentJumpCount;
            }

            set
            {
                bool changed = _currentJumpCount != value;
                _currentJumpCount = value;
                if (changed)
                {
                    onJumpCountValueChange?.Invoke(value);
                }
            }

        }

        #endregion


        #region 事件

        /// <summary>通过落地状态 setter 改变值后触发，参数为新状态。</summary>
        [Tooltip("是否在地面的值发生改变时触发")]
        [Header("是否在地面的值发生改变时触发")]
        public BoolUnityEvent onGroundValueChange;
        /// <summary>通过触墙状态 setter 改变值后触发，参数为新状态。</summary>
        [Tooltip("是否接触墙面的值发生改变时触发")]
        [Header("是否接触墙面的值发生改变时触发")]
        public BoolUnityEvent onTouchWallValueChange;
        /// <summary>通过触顶状态 setter 改变值后触发，参数为新状态。</summary>
        [Tooltip("头顶是否接触游戏物体的值发生改变时触发")]
        [Header("头顶是否接触游戏物体的值发生改变时触发")]
        public BoolUnityEvent onTopValueChange;
        /// <summary>组件计算的速度改变后触发，参数为新速度。</summary>
        [Tooltip("移动速度改变时触发")]
        [Header("移动速度改变时触发")]
        public Vector2UnityEvent onVelocityValueChange;
        /// <summary>跳跃计数 setter 提交新值后触发，参数为新计数。</summary>
        [Tooltip("已跳跃的数量发生改变时触发")]
        [Header("已跳跃的数量发生改变时触发")]
        public IntUnityEvent onJumpCountValueChange;

        #endregion

        #region 生命周期

        /// <summary>缓存初始刚体重力，并按当前对象层的二维碰撞矩阵建立检测掩码。</summary>
        private void Awake()
        {
            if (Rigidbody == null)
            {
                throw new System.Exception("未查询到刚体组件!");
            }

            _rigidbodyGravityScale = Rigidbody.gravityScale;

            _currentLayer = gameObject.layer;
            CheckLayerMask.Physics2DSetting(gameObject.layer);
        }

        /// <summary>重置组件时从刚体读取默认的组件重力倍率。</summary>
        private void Reset()
        {
            if (Rigidbody == null)
            {
                throw new System.Exception("未查询到刚体组件!");
            }

            gravityScale = Rigidbody.gravityScale;
        }

        /// <summary>依次积分竖直速度、提交排队位移，再处理提前松开跳跃后的额外减速。</summary>
        private void FixedUpdate()
        {
            if (!EnsureColliders())
            {
                return;
            }

            int version = PhysicsVersion;
            UpdateSpeedVertical();
            if (!IsPhysicsCurrent(version))
            {
                return;
            }

            Move();
            if (!IsPhysicsCurrent(version))
            {
                return;
            }

            JumpHeld();
        }

        /// <summary>重新记录刚体重力、选择附属碰撞体，并重置位置差的起点。</summary>
        private void OnEnable()
        {
            _rigidbodyGravityScale = Rigidbody.gravityScale;
            RefreshColliders();
            _lastPosition = Rigidbody.position;
        }

        /// <summary>停用时使当前处理版本失效，并归还重力、清除运动与检测缓存。</summary>
        private void OnDisable()
            => ReleaseGravityAndResetState();

        /// <summary>使旧流程失效并恢复刚体重力，清除排队位移、跳跃和接触状态且不发送业务事件。</summary>
        private void ReleaseGravityAndResetState()
        {
            PhysicsVersion++;
            if (Rigidbody)
            {
                Rigidbody.gravityScale = _rigidbodyGravityScale;
            }
            _moveDelta = Vector2.zero;
            _velocity = Vector2.zero;
            SpeedVertical = 0;
            _isJumping = false;
            _jumpAbortSpeedReduction = 0;
            _currentJumpCount = 0;
            _isGrounded = _isTouchingWall = _isTouchingCeiling = false;
            _checkGround = null;
            _checkWall = null;
            _checkTop = null;
            GroundInfo = WallInfo = TopInfo = default;
            Array.Clear(Hits, 0, Hits.Length);
            Array.Clear(Contacts, 0, Contacts.Length);
        }

        /// <summary>按已有地面信息调整旋转、更新层掩码，再刷新启用的接触检测。</summary>
        private void Update()
        {
            if (!EnsureColliders())
            {
                return;
            }

            if (autoRotation)
            {
                AutoRotation();
            }

            if (_currentLayer != gameObject.layer)
            {
                CheckLayerMask.Physics2DSetting(gameObject.layer);
                _currentLayer = gameObject.layer;
            }

            PhysicsCheck();


        }


        #endregion


        #region 方法

        /// <summary>累加待提交的世界坐标位移，请在 FixedUpdate 中调用；停用或碰撞体无效时忽略。</summary>
        /// <param name="delta">本次位移而非速度；空中触墙限制可能清除其水平分量。</param>
        public void Move(Vector2 delta)
        {
            if (!isActiveAndEnabled || !HasValidColliders)
            {
                return;
            }

            if (IsIgnoreMoveX(delta))
            {
                delta.x = 0;
            }

            _moveDelta += delta;
        }


        /// <summary>将水平速度换算为固定步长位移；开启地面检测且落地时沿地面切线移动。</summary>
        /// <param name="speed">带方向的移动速度，按 Time.fixedDeltaTime 换算位移。</param>
        public void MoveHorizontal(float speed)
        {
            if (IsGrounded && ChecksGround)
            {
                Vector3 dir = Vector3.Cross(GroundInfo.normal, Vector3.forward);
                Move(dir.normalized * speed * Time.fixedDeltaTime);
            }
            else
            {
                Move(Vector2.right * speed * Time.fixedDeltaTime);
            }
        }

        /// <summary>提交本轮排队位移、更新观测速度，并在触顶上升时重置竖直速度。</summary>
        private void Move()
        {
            int version = PhysicsVersion;
            if (Mathf.Abs(_moveDelta.x) <= 0.01f && Mathf.Abs(_moveDelta.y) <= 0.01f)
            {
                Velocity = Vector2.zero;
                if (!IsPhysicsCurrent(version))
                {
                    return;
                }

                _moveDelta = Vector2.zero;
                _lastPosition = Rigidbody.position;
                return;
            }

            // MovePosition 请求物理移动；下面的位置差读取的是此刻刚体可观测的位置，不直接用排队位移充当速度。
            Rigidbody.MovePosition(Rigidbody.position + _moveDelta);

            Velocity = (Rigidbody.position - _lastPosition) / Time.fixedDeltaTime;
            if (!IsPhysicsCurrent(version))
            {
                return;
            }

            _lastPosition = Rigidbody.position;
            _moveDelta = Vector2.zero;

            if (ChecksCeiling)
            {
                if (IsTouchingCeiling && SpeedVertical > 0)
                {
                    ResetSpeedVertical();
                }
            }
            else
            {
                if (_checkTop == null)
                {
                    _checkTop = new CheckTop();
                }

                int count = Rigidbody.GetContacts(Contacts);
                _checkTop.Check(Contacts, this, count, topAngle);
                if (!IsPhysicsCurrent(version))
                {
                    return;
                }

                if (_checkTop.IsTopByContact && SpeedVertical > 0)
                {
                    ResetSpeedVertical();
                }
            }


        }

        /// <summary>在地面检测开启且次数允许时发起跳跃，清除之前排队的位移与观测速度。</summary>
        /// <param name="speed">竖直初速度，正值表示向上。</param>
        public void Jump(float speed)
        {
            if (!isActiveAndEnabled || !HasValidColliders)
            {
                return;
            }

            if (!ChecksGround)
            {
                Debug.LogWarning("是否检测地面的选项未开启，无法跳跃!");
                return;
            }

            if (CurrentJumpCount >= maxJumpCount)
            {
                return;
            }

            int version = PhysicsVersion;
            CurrentJumpCount++;
            if (!IsPhysicsCurrent(version))
            {
                return;
            }

            SpeedVertical = speed;
            if (SpeedVertical > 0)
            {
                IsGrounded = false;
            }

            if (!IsPhysicsCurrent(version))
            {
                return;
            }

            Velocity = Vector2.zero;
            if (!IsPhysicsCurrent(version))
            {
                return;
            }

            _moveDelta = Vector2.zero;
            _lastPosition = Rigidbody.position;
        }

        /// <summary>发起可提前松开的跳跃，松开后在仍上升时施加额外减速。</summary>
        /// <param name="speed">竖直初速度，正值表示向上。</param>
        /// <param name="jumpAbortSpeedReduction">松开后的额外减速度，负值按零处理。</param>
        public void JumpStart(float speed, float jumpAbortSpeedReduction)
        {
            if (!isActiveAndEnabled || !HasValidColliders || !ChecksGround)
            {
                return;
            }

            if (CurrentJumpCount >= maxJumpCount)
            {
                return;
            }

            int version = PhysicsVersion;
            Jump(speed);
            if (!IsPhysicsCurrent(version))
            {
                return;
            }

            _isJumping = true;

            if (jumpAbortSpeedReduction < 0)
            {
                jumpAbortSpeedReduction = 0;
            }

            this._jumpAbortSpeedReduction = jumpAbortSpeedReduction;
        }

        /// <summary>跳跃松开后按固定步长减少上升速度，到达非正速度时结束该减速状态。</summary>
        private void JumpHeld()
        {
            if (_jumpAbortSpeedReduction == 0)
            {
                return;
            }

            if (!_isJumping && SpeedVertical > 0)
            {
                SpeedVertical -= _jumpAbortSpeedReduction * Time.fixedDeltaTime;
            }

            if (SpeedVertical <= 0)
            {
                _jumpAbortSpeedReduction = 0;
                _isJumping = false;
            }

        }

        /// <summary>标记跳跃已松开，不直接清零竖直速度；JumpStart 配置的减速在后续固定更新中生效。</summary>
        public void JumpEnd()
        {
            _isJumping = false;
        }

        /// <summary>仅将组件维护的竖直速度归零，不清除已排队的位移。</summary>
        public void ResetSpeedVertical()
        {
            SpeedVertical = 0;
        }

        /// <summary>在地面接触且非上升时归还重力，否则由组件积分竖直速度并排队位移。</summary>
        private void UpdateSpeedVertical()
        {
            if (!ChecksGround)
            {
                return;
            }

            if (_checkGround != null && _checkGround.IsGroundedByContacts && SpeedVertical <= 0)
            {
                SpeedVertical = 0;
                Rigidbody.gravityScale = _rigidbodyGravityScale;
            }
            else
            {
                Rigidbody.gravityScale = 0;

                // 空中积分会关闭刚体重力；已有合格下方接触且正在下落时，本步不再追加下落位移。
                if (_checkGround != null)
                {
                    _contactCount = Rigidbody.GetContacts(Contacts);
                    if (_checkGround.HasColliderBelow(Contacts, _contactCount) && SpeedVertical < 0)
                    {
                        return;
                    }
                }

                SpeedVertical = CharacterMotorMath.IntegrateGravity(SpeedVertical, Physics2D.gravity.y, gravityScale, Time.fixedDeltaTime, maxFallingVelocity);

                Move(Vector2.up * SpeedVertical * Time.fixedDeltaTime);
            }
        }

        /// <summary>重新选择本刚体的四向碰撞体；运行时添加或移除碰撞体后显式调用。</summary>
        /// <returns>是否找到可用于四向检测的已启用、非触发 Box、Circle 或 Capsule 碰撞体。</returns>
        /// <remarks>按世界包围盒极值选择，允许四个方向共用同一碰撞体；失败时恢复重力并清空运动状态。</remarks>
        public bool RefreshColliders()
        {
            PhysicsVersion++;
            Top = Bottom = Left = Right = null;
            List<Collider2D> colliders = new List<Collider2D>();
            Rigidbody.GetAttachedColliders(colliders);

            for (int i = 0; i < colliders.Count; i++)
            {
                Collider2D collider = colliders[i];
                if (!IsUsable(collider))
                {
                    continue;
                }

                bool iscollider2d = !(collider is BoxCollider2D);
                bool iscirclecollider2d = !(collider is CircleCollider2D);
                bool iscapsulecollider2d = !(collider is CapsuleCollider2D);

                if (iscollider2d && iscirclecollider2d && iscapsulecollider2d)
                {
                    continue;
                }

                if (Top == null)
                {
                    Top = collider;
                }
                else
                {
                    if (collider.bounds.max.y > Top.bounds.max.y)
                    {
                        Top = collider;
                    }
                }

                if (Bottom == null)
                {
                    Bottom = collider;
                }
                else
                {
                    if (collider.bounds.min.y < Bottom.bounds.min.y)
                    {
                        Bottom = collider;
                    }
                }

                if (Right == null)
                {
                    Right = collider;
                }
                else
                {
                    if (collider.bounds.max.x > Right.bounds.max.x)
                    {
                        Right = collider;
                    }
                }

                if (Left == null)
                {
                    Left = collider;
                }
                else
                {
                    if (collider.bounds.min.x < Left.bounds.min.x)
                    {
                        Left = collider;
                    }
                }


            }

            _isColliderReady = Left && Right && Bottom && Top;
            if (!_isColliderReady)
            {
                ReleaseGravityAndResetState();
                Debug.LogError($"{nameof(CharacterController2D)} on '{name}': Rigidbody2D requires an attached, non-trigger BoxCollider2D, CircleCollider2D or CapsuleCollider2D. Add one and call RefreshColliders().", this);
            }
            return _isColliderReady;

        }

        /// <summary>复用本轮刚体接触缓冲区更新地面、墙面与顶部状态，落地时重置跳跃计数。</summary>
        private void PhysicsCheck()
        {
            int version = PhysicsVersion;

            if (ChecksGround || ChecksWalls || ChecksCeiling)
            {
                _contactCount = Rigidbody.GetContacts(Contacts);
            }

            if (ChecksGround)
            {
                if (_checkGround == null)
                {
                    _checkGround = new CheckGround();
                }

                _checkGround.Check(Contacts, this, _contactCount, groundAngle);

                if (IsGrounded)
                {
                    CurrentJumpCount = 0;
                }
            }

            if (!IsPhysicsCurrent(version))
            {
                return;
            }

            if (ChecksWalls)
            {
                if (_checkWall == null)
                {
                    _checkWall = new CheckWall();
                }

                _checkWall.Check(Contacts, this, _contactCount, wallAngle);
            }

            if (!IsPhysicsCurrent(version))
            {
                return;
            }

            if (ChecksCeiling)
            {
                if (_checkTop == null)
                {
                    _checkTop = new CheckTop();
                }

                _checkTop.Check(Contacts, this, _contactCount, topAngle);
            }

#if UNITY_EDITOR
            if (Mathf.FloorToInt(Time.time) != _lastSecond)
            {
                _lastSecond = Mathf.FloorToInt(Time.time);
                CheckCount = 0;
            }
#endif

        }

        /// <summary>按层掩码、深度和全局触发器查询开关构造二维查询过滤器。</summary>
        /// <param name="layerMask">允许查询的层位掩码。</param>
        /// <param name="minDepth">允许的最小深度。</param>
        /// <param name="maxDepth">允许的最大深度。</param>
        internal static ContactFilter2D CreateLegacyFilter(int layerMask, float minDepth, float maxDepth)
        {
            ContactFilter2D result = default(ContactFilter2D);
            result.useTriggers = Physics2D.queriesHitTriggers;
            result.SetLayerMask(layerMask);
            result.SetDepth(minDepth, maxDepth);
            return result;
        }


        /// <summary>沿指定方向短距离投射圆形，结果写入本实例共用的 Hits 缓冲区。</summary>
        /// <param name="circle">提供中心及缩放后半径的碰撞体。</param>
        /// <param name="dir">世界坐标投射方向。</param>
        /// <returns>缓冲区中有效命中的数量；触发器及自身层级由检测器继续过滤。</returns>
        internal int Raycast(CircleCollider2D circle, Vector2 dir)
        {

            Vector2 point = circle.bounds.center;

            float r = circle.radius * Mathf.Max(Mathf.Abs(circle.transform.lossyScale.x), Mathf.Abs(circle.transform.lossyScale.y));

#if UNITY_6000_0_OR_NEWER
            ContactFilter2D contactFilter = CreateLegacyFilter(CheckLayerMask.value, float.NegativeInfinity, float.PositiveInfinity);
            return Physics2D.defaultPhysicsScene.CircleCast(point, r, dir, Distance, contactFilter, Hits);

#else
            return Physics2D.CircleCastNonAlloc(point, r, dir, Hits, Distance, CheckLayerMask.value);
#endif
        }

        /// <summary>沿指定方向投射缩放和旋转后的矩形，不使用旋转后的包围盒尺寸。</summary>
        /// <param name="box">提供中心、局部尺寸及物理姿态的碰撞体。</param>
        /// <param name="dir">世界坐标投射方向。</param>
        /// <returns>共用 Hits 缓冲区中本次有效命中的数量。</returns>
        internal int Raycast(BoxCollider2D box, Vector2 dir)
        {
            Vector2 point = box.bounds.center;
            Vector2 size = CharacterMotorMath.ScaledColliderSize(box.size, box.transform.lossyScale);
            float angle = GetColliderCastAngle(box);

#if UNITY_6000_0_OR_NEWER
            ContactFilter2D contactFilter = CreateLegacyFilter(CheckLayerMask.value, float.NegativeInfinity, float.PositiveInfinity);
            return Physics2D.defaultPhysicsScene.BoxCast(point, size, angle, dir, Distance, contactFilter, Hits);
#else
            return Physics2D.BoxCastNonAlloc(point, size, angle, dir, Hits, Distance, CheckLayerMask.value);
#endif

        }

        /// <summary>沿指定方向投射缩放和旋转后的胶囊，保留碰撞体自身的胶囊轴向。</summary>
        /// <param name="capsule">提供中心、局部尺寸、轴向及物理姿态的碰撞体。</param>
        /// <param name="dir">世界坐标投射方向。</param>
        /// <returns>共用 Hits 缓冲区中本次有效命中的数量。</returns>
        internal int Raycast(CapsuleCollider2D capsule, Vector2 dir)
        {
            Vector2 point = capsule.bounds.center;
            Vector2 size = CharacterMotorMath.ScaledColliderSize(capsule.size, capsule.transform.lossyScale);
            float angle = GetColliderCastAngle(capsule);
            CapsuleDirection2D d = capsule.direction;

#if UNITY_6000_0_OR_NEWER
            ContactFilter2D contactFilter = CreateLegacyFilter(CheckLayerMask.value, float.NegativeInfinity, float.PositiveInfinity);
            return Physics2D.defaultPhysicsScene.CapsuleCast(point, size, d, angle, dir, Distance, contactFilter, Hits);
#else
            return Physics2D.CapsuleCastNonAlloc(point, size, d, angle, dir, Hits, Distance, CheckLayerMask.value);
#endif
        }

        /// <summary>取得包含刚体物理旋转差值和子碰撞体相对旋转的投射角度。</summary>
        /// <param name="collider">待取得投射姿态的碰撞体。</param>
        internal static float GetColliderCastAngle(Collider2D collider)
        {
            float angle = collider.transform.eulerAngles.z;
            var body = collider.attachedRigidbody;
            // 刚体物理位姿可先于Transform更新；保留子Collider的相对旋转，不能只读旧Transform。
            return body ? angle + Mathf.DeltaAngle(body.transform.eulerAngles.z, body.rotation) : angle;
        }

        /// <summary>落地时按地面法线调整 Z 轴角度，首次离地时恢复为零。</summary>
        private void AutoRotation()
        {

            if (!ChecksGround)
            {
                return;
            }

            if (IsGrounded)
            {
                _isInTheAir = false;
                float angle = -Vector2.SignedAngle(GroundInfo.normal, Vector2.up);

                if (transform.right.x < 0)
                {
                    angle = -angle;
                }

                transform.eulerAngles = new Vector3(transform.eulerAngles.x, transform.eulerAngles.y, angle);
            }
            else
            {
                if (!_isInTheAir)
                {
                    transform.eulerAngles = new Vector3(transform.eulerAngles.x, transform.eulerAngles.y, 0);
                }

                _isInTheAir = true;
            }

        }

        /// <summary>同时启用地面和墙面检测时，判断空中水平位移是否与墙面或接触法线反向。</summary>
        /// <param name="delta">待提交的世界坐标位移，仅检查水平分量。</param>
        private bool IsIgnoreMoveX(Vector2 delta)
        {
            if (!ChecksWalls)
            {
                return false;
            }

            if (!ChecksGround)
            {
                return false;
            }

            if (IsTouchingWall && !IsGrounded)
            {
                if (CharacterMotorMath.BlocksHorizontal(delta.x, WallInfo.normal.x))
                {
                    return true;
                }
            }

            if (!IsTouchingWall && !IsGrounded)
            {
                int count = Rigidbody.GetContacts(Contacts);
                for (int i = 0; i < count; i++)
                {
                    if (Contacts[i].collider == null)
                    {
                        continue;
                    }

                    if (Contacts[i].collider.isTrigger)
                    {
                        continue;
                    }

                    if (CharacterMotorMath.BlocksHorizontal(delta.x, Contacts[i].normal.x))
                    {
                        return true;
                    }
                }
            }


            return false;
        }

        #endregion

    }
}
