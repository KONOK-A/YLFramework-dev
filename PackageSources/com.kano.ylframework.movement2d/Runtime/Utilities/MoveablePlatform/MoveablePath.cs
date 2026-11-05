using System;
using System.Collections.Generic;
using UnityEngine;

namespace YLFramework.Platform2D
{
    /// <summary>构建移动路径时采用的节点来源。</summary>
    public enum PositionType
    {
        /// <summary>使用配置列表中的世界坐标。</summary>
        [Tooltip("使用坐标点作为路径")] Positions,
        /// <summary>在构建时读取节点 Transform 的世界位置。</summary>
        [Tooltip("使用transform作为路径")] Transforms
    }

    /// <summary>路径到达终点后的进度变化方式。</summary>
    public enum MoveType
    {
        /// <summary>在起点和终点之间连续往返。</summary>
        [Tooltip("在起点和终点之间来回移动")] PingPong,
        /// <summary>走完路径后将进度跳回起点，不补建终点到起点的线段。</summary>
        [Tooltip("循环移动")] Loop,
        /// <summary>到达终点后停止本轮移动。</summary>
        [Tooltip("移动到终点之后自动停止")] Once
    }

    /// <summary>固定更新中的路径计时来源，不改变 FixedUpdate 的调度方式。</summary>
    public enum UpdateMode
    {
        /// <summary>使用受时间缩放影响的固定时间步长。</summary>
        [Tooltip("正常模式")] Normal,
        /// <summary>使用未缩放的固定时间步长，仍须等待固定更新被调用。</summary>
        [Tooltip("忽略时间缩放")] UnscaledTime
    }

    /// <summary>记录路径节点的位置及累计路程标记。</summary>
    internal struct PositionInfo
    {
        /// <summary>构建中存累计距离，正长度路径归一化后存累计路程比例。</summary>
        public float Percent;
        /// <summary>节点的世界坐标快照。</summary>
        public Vector3 Position;
        /// <summary>创建带累计路程标记的节点记录。</summary>
        /// <param name="percent">当前构建阶段使用的距离或归一化比例。</param>
        /// <param name="position">节点世界坐标。</param>
        public PositionInfo(float percent, Vector3 position)
        {
            this.Percent = percent;
            this.Position = position;
        }
    }

    /// <summary>持有节点快照，按累计路程比例对折线路径进行线性插值。</summary>
    internal sealed class MoveablePath
    {
        private readonly List<PositionInfo> _nodes = new List<PositionInfo>();
        /// <summary>当前快照中相邻节点的总距离。</summary>
        public float Distance
        {
            get; private set;
        }
        /// <summary>当前快照保存的节点数量，包含重复坐标节点。</summary>
        public int Count => _nodes.Count;

        /// <summary>判断坐标的三个分量是否都不是 NaN 或无穷值。</summary>
        /// <param name="value">待检查的坐标。</param>
        public static bool IsFinite(Vector3 value) =>
            !float.IsNaN(value.x) && !float.IsInfinity(value.x) &&
            !float.IsNaN(value.y) && !float.IsInfinity(value.y) &&
            !float.IsNaN(value.z) && !float.IsInfinity(value.z);

        /// <summary>复制并验证节点，构建按总路程归一化的快照；失败时不保留旧路径。</summary>
        /// <param name="points">按移动顺序排列的世界坐标，允许单点及重复点。</param>
        /// <param name="error">失败原因；成功时为 null。</param>
        /// <returns>是否成功构建至少包含一个节点的路径。</returns>
        public bool TryBuild(IList<Vector3> points, out string error)
        {
            _nodes.Clear();
            Distance = 0;
            if (points == null || points.Count == 0)
            {
                error = "Path has no nodes (index 0).";
                return false;
            }
            for (int i = 0; i < points.Count; i++)
            {
                if (!IsFinite(points[i]))
                {
                    _nodes.Clear();
                    Distance = 0;
                    error = $"Path node {i} contains NaN/Infinity.";
                    return false;
                }
                if (i > 0)
                {
                    Distance += Vector3.Distance(points[i - 1], points[i]);
                }

                if (float.IsInfinity(Distance))
                {
                    _nodes.Clear();
                    Distance = 0;
                    error = $"Path distance overflow at node {i}.";
                    return false;
                }
                _nodes.Add(new PositionInfo(Distance, points[i]));
            }
            if (Distance > 0)
            {
                for (int i = 0; i < _nodes.Count; i++)
                {
                    _nodes[i] = new PositionInfo(_nodes[i].Percent / Distance, _nodes[i].Position);
                }
            }

            error = null;
            return true;
        }

        /// <summary>按累计路程比例取路径位置，跳过零长度段；总长为零时返回首节点。</summary>
        /// <param name="progress">归一化进度，非正值取起点，达到终点后取末节点。</param>
        /// <exception cref="InvalidOperationException">尚无可用节点快照。</exception>
        public Vector3 Evaluate(float progress)
        {
            if (_nodes.Count == 0)
            {
                throw new InvalidOperationException("Path has not been initialized.");
            }

            if (progress <= 0 || Distance == 0)
            {
                return _nodes[0].Position;
            }

            for (int i = 1; i < _nodes.Count; i++)
            {
                if (progress >= _nodes[i].Percent)
                {
                    continue;
                }

                float span = _nodes[i].Percent - _nodes[i - 1].Percent;
                if (span > 0)
                {
                    return Vector3.Lerp(_nodes[i - 1].Position, _nodes[i].Position, (progress - _nodes[i - 1].Percent) / span);
                }
            }
            return _nodes[_nodes.Count - 1].Position;
        }

        /// <summary>将已过时间换算为往返、循环或单次进度，非正时长直接返回终点进度。</summary>
        /// <param name="elapsed">已累计的移动秒数，不含启动延迟。</param>
        /// <param name="duration">单程所需秒数。</param>
        /// <param name="type">进度变化模式。</param>
        /// <exception cref="ArgumentOutOfRangeException">正时长下传入未知的移动模式。</exception>
        public static float Progress(float elapsed, float duration, MoveType type)
        {
            if (duration <= 0)
            {
                return 1;
            }

            float t = Math.Max(0, elapsed / duration);
            switch (type)
            {
                case MoveType.PingPong:
                    t %= 2;
                    return 1 - Math.Abs(t - 1);
                case MoveType.Loop:
                    return t % 1;
                case MoveType.Once:
                    return Math.Min(t, 1);
                default:
                    throw new ArgumentOutOfRangeException(nameof(type));
            }
        }
    }
}
