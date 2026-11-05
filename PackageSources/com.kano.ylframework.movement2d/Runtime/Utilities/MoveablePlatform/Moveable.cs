using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.Events;
using static UnityEngine.GraphicsBuffer;

namespace YLFramework.Platform2D
{


    /// <summary>在固定更新中沿世界坐标折线路径移动对象。</summary>
    /// <remarks>
    /// 路径初始化时复制运行快照，直接修改公开集合或节点 Transform 不会同步当前快照。
    /// UnscaledTime 只改变固定步长的取值，不改变更新回调；停用组件不会自动 Stop 或重置计时。
    /// </remarks>
    public class Moveable : MonoBehaviour
    {
        #region 字段

        [SerializeField]
        [Tooltip("移动速度")]
        private float speed = 4;

        [SerializeField]
        [Tooltip("更新模式,Normal:普通模式 UnscaledTime:忽略时间缩放")]
        private UpdateMode updateMode = UpdateMode.Normal;

        [SerializeField]
        [Tooltip("移动类型")]
        private MoveType moveType;

        [SerializeField]
        [Tooltip("路径类型,Positions:使用坐标点作为路径 Transforms:使用transform作为路径")]
        private PositionType positionType;

        [SerializeField]
        private List<Transform> transforms = new List<Transform>();
        [SerializeField]
        private List<Vector3> positions = new List<Vector3>();

        private readonly MoveablePath _path = new MoveablePath();
        private readonly List<Vector3> _pathPoints = new List<Vector3>();
        private bool _hasStarted;
        private int _pathVersion;

        private float _timer;

        [SerializeField]
        [Tooltip("延迟时间")]
        private float delay = 0;
        private float _delayTimer = 0;

        [SerializeField]
        [Tooltip("是否自动运行?")]
        private bool runOnAwake = true;

        private bool _isRunning = false;

        #endregion

        #region 属性

        /// <summary>沿路径移动的每秒距离；运行中变为非正数或非有限值会停止移动。</summary>
        public float Speed
        {
            get
            {
                return speed;
            }
            set
            {
                speed = value;
            }
        }

        /// <summary>当前采用的往返、循环或单次进度模式。</summary>
        public MoveType MoveType
        {
            get
            {
                return moveType;
            }
            set
            {
                moveType = value;
            }
        }

        /// <summary>直接可写的 Transform 路径源，初始化时读取节点的世界位置。</summary>
        public List<Transform> Transforms => transforms;
        /// <summary>直接可写的世界坐标路径源，不是当前运行快照的只读副本。</summary>
        public List<Vector3> Positions => positions;

        /// <summary>下一次路径初始化采用的节点来源，修改后不自动重建运行快照。</summary>
        public PositionType PositionType
        {
            get => positionType; set => positionType = value;
        }
        /// <summary>固定更新中累计移动和延迟时间时使用的时间步来源。</summary>
        public UpdateMode UpdateMode
        {
            get => updateMode; set => updateMode = value;
        }

        /// <summary>每次 Run 后首次移动前等待的秒数，暂停期间不累计。</summary>
        public float Delay
        {
            get
            {
                return delay;
            }
            set
            {
                delay = value;
            }
        }

        /// <summary>是否处于运行状态，包含启动延迟期间；暂停或停止时为 false。</summary>
        public bool IsRunning => _isRunning;

        /// <summary>按当前路径源重新计算的相邻节点总长度，不一定等于已建立运行快照的长度。</summary>
        /// <remarks>路径源无效时返回零；读取不会替换运行快照。</remarks>
        public float Distance
        {
            get
            {
                if (!TryGetPathPoints(out _))
                {
                    return 0;
                }

                float distance = 0;
                for (int i = 1; i < _pathPoints.Count; i++)
                {
                    distance += Vector3.Distance(_pathPoints[i - 1], _pathPoints[i]);
                }

                return distance;
            }
        }

        /// <summary>按当前路径源长度和速度估算的单程秒数，不含启动延迟；速度无效时返回零。</summary>
        public float Time => speed > 0 && !float.IsInfinity(speed) ? Distance / speed : 0;


        #endregion

        #region 事件

        /// <summary>Run 成功建立路径并标记运行后触发，不表示已经提交了首个位置。</summary>
        [Header("开始移动时触发")]
        [Tooltip("开始移动时触发")]
        public UnityEvent onStartRun;

        /// <summary>提交位置前触发，参数是目标世界位置，不是速度或位移。</summary>
        [Header("移动时触发")]
        [Tooltip("移动时触发")]
        public Vector3UnityEvent onMove;

        /// <summary>已启动的移动切换暂停状态后触发，参数为是否暂停。</summary>
        [Header("暂停时或取消暂停时触发")]
        [Tooltip("暂停时或取消暂停时触发")]
        public BoolUnityEvent onPause;

        /// <summary>Stop 清除运行状态与计时后触发，包括单次路径完成或运行速度失效导致的停止。</summary>
        [Header("结束移动时触发")]
        [Tooltip("结束移动时触发")]
        public UnityEvent onStop;

        #endregion

        #region 生命周期

        /// <summary>启用自动运行配置时，在唤醒阶段请求启动路径。</summary>
        protected virtual void Awake()
        {
            if (runOnAwake)
            {
                Run();
            }
        }

        /// <summary>运行时先累计启动延迟，延迟满足后推进路径移动。</summary>
        protected virtual void FixedUpdate()
        {
            if (!_isRunning)
            {
                return;
            }

            if (_delayTimer >= delay)
            {
                Move();
            }
            else
            {

                switch (updateMode)
                {
                    case UpdateMode.Normal:
                        _delayTimer += UnityEngine.Time.fixedDeltaTime;
                        break;
                    case UpdateMode.UnscaledTime:
                        _delayTimer += UnityEngine.Time.fixedUnscaledDeltaTime;
                        break;
                }


            }
        }



        #endregion

        #region 方法

        /// <summary>检查速度、延迟及所选路径源，填充待构建节点但不替换运行快照。</summary>
        /// <param name="error">失败原因，节点问题包含索引；成功时为 null。</param>
        /// <returns>速度、延迟和节点是否通过此处校验；路径总长度溢出由构建阶段继续检查。</returns>
        public bool ValidatePath(out string error)
        {
            if (speed <= 0 || float.IsNaN(speed) || float.IsInfinity(speed))
            {
                error = "Speed must be finite and greater than zero.";
                return false;
            }
            if (delay < 0 || float.IsNaN(delay) || float.IsInfinity(delay))
            {
                error = "Delay must be finite and non-negative.";
                return false;
            }
            return TryGetPathPoints(out error);
        }

        /// <summary>清空临时节点列表，从当前路径源读取并验证世界坐标。</summary>
        /// <param name="error">失败原因；成功时为 null。</param>
        private bool TryGetPathPoints(out string error)
        {
            _pathPoints.Clear();
            int count = positionType == PositionType.Positions ? positions?.Count ?? 0 : transforms?.Count ?? 0;
            if (count == 0)
            {
                error = "Path has no nodes (index 0).";
                return false;
            }
            for (int i = 0; i < count; i++)
            {
                if (positionType == PositionType.Transforms && !transforms[i])
                {
                    error = $"Missing Transform at path node {i}.";
                    return false;
                }
                var point = positionType == PositionType.Positions ? positions[i] : transforms[i].position;
                if (!MoveablePath.IsFinite(point))
                {
                    error = $"Path node {i} contains NaN/Infinity.";
                    return false;
                }
                _pathPoints.Add(point);
            }
            error = null;
            return true;
        }

        /// <summary>使旧路径流程失效并重建运行快照，失败时取消运行状态并记录诊断。</summary>
        private bool Init()
        {
            _pathVersion++;
            if (ValidatePath(out string error) && _path.TryBuild(_pathPoints, out error))
            {
                return true;
            }

            _isRunning = _hasStarted = false;
            Debug.LogError($"{GetType().Name} on '{name}': {error}", this);
            return false;
        }


        /// <summary>按固定步长计算目标并通知监听者，确认状态仍有效后提交位置和处理路径结束。</summary>
        private void Move()
        {
            if (speed <= 0 || float.IsNaN(speed) || float.IsInfinity(speed))
            {
                Stop();
                return;
            }

            switch (updateMode)
            {
                case UpdateMode.Normal:
                    _timer += UnityEngine.Time.fixedDeltaTime;
                    break;
                case UpdateMode.UnscaledTime:
                    _timer += UnityEngine.Time.fixedUnscaledDeltaTime;
                    break;
            }

            float t = MoveablePath.Progress(_timer, _path.Distance / speed, moveType);

            Vector3 target = GetPosition(t);
            int version = _pathVersion;
            onMove?.Invoke(target);
            // 监听者可能销毁、停用、停止或重建路径，不能继续提交旧版本的目标。
            if (!this || !isActiveAndEnabled || !_isRunning || version != _pathVersion)
            {
                return;
            }

            Move(target);
            if ((moveType == MoveType.Once && t >= 1) || _path.Distance == 0)
            {
                Stop();
            }
        }

        /// <summary>提交目标位置的扩展入口，默认直接写入 Transform 的世界位置。</summary>
        /// <param name="target">本次计算出的目标世界位置。</param>
        protected virtual void Move(Vector3 target)
        {
            transform.position = target;
        }

        /// <summary>未运行时重建路径并从零计时启动；对暂停中的路径调用也会重新开始。</summary>
        public void Run()
        {
            if (_isRunning)
            {
                return;
            }

            _timer = 0;
            _delayTimer = 0;
            if (!Init())
            {
                return;
            }

            _hasStarted = true;
            _isRunning = true;
            onStartRun?.Invoke();
        }

        /// <summary>切换已启动路径的暂停状态，保留进度和延迟计时；不能恢复已停止的路径。</summary>
        /// <param name="pause">true 暂停，false 继续。</param>
        public void Pause(bool pause)
        {
            if (!_hasStarted || _isRunning == !pause)
            {
                return;
            }

            _isRunning = !pause;
            onPause?.Invoke(pause);
        }

        /// <summary>使当前路径流程失效并清零计时，不将对象移回起点；重复停止不再通知。</summary>
        public void Stop()
        {
            if (!_hasStarted && !_isRunning)
            {
                return;
            }

            _pathVersion++;
            _hasStarted = false;
            _isRunning = false;
            _delayTimer = 0;
            _timer = 0;
            onStop?.Invoke();
        }

        /// <summary>向坐标路径源添加节点，并按当前节点来源重建运行快照，不清零已有计时。</summary>
        /// <param name="position">新增节点的世界坐标。</param>
        public void AddPosition(Vector3 position)
        {
            positions.Add(position);
            Init();
        }

        /// <summary>向 Transform 路径源添加节点，并按当前节点来源重建运行快照，不清零已有计时。</summary>
        /// <param name="transform">新增节点，构建时读取其世界位置。</param>
        public void AddPosition(Transform transform)
        {
            transforms.Add(transform);
            Init();
        }

        /// <summary>移除当前路径源的指定节点并重建快照；索引越界时仍执行重建。</summary>
        /// <param name="index">从零开始的节点索引。</param>
        public void RemovePosition(int index)
        {
            if (positionType == PositionType.Positions)
            {
                if (index >= 0 && index < positions.Count)
                {
                    positions.RemoveAt(index);
                }
            }
            else
            {
                if (index >= 0 && index < transforms.Count)
                {
                    transforms.RemoveAt(index);
                }
            }

            Init();
        }

        /// <summary>清空当前路径源并尝试重建；空路径校验失败会取消运行并记录诊断。</summary>
        public void ClearPosition()
        {
            if (positionType == PositionType.Positions)
            {
                positions.Clear();
            }
            else
            {
                transforms.Clear();
            }

            Init();
        }

        /// <summary>按累计路程占比从当前运行快照取得世界坐标。</summary>
        /// <param name="t">路径进度，通常位于零到一之间。</param>
        private Vector3 GetPosition(float t)
            => _path.Evaluate(t);

        #endregion

    }

}
