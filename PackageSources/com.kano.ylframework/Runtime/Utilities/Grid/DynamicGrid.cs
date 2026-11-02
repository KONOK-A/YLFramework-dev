using System;
using System.Collections.Generic;

namespace YLFramework.Grids
{
    /// <summary>
    /// 稀疏二维坐标容器：只存储实际写入过的格子，不预设尺寸，支持任意 int 坐标（含负数）。
    /// 未存储坐标读取时返回 default(T)；显式写入 default(T) 的格子仍视为已存在。
    /// </summary>
    /// <typeparam name="T">容器存储的数据类型</typeparam>
    public class DynamicGrid<T>
    {
        private readonly Dictionary<(int x, int y), T> _grid = new();

        /// <summary>
        /// 已存储的格子数量。
        /// </summary>
        public int Count => _grid.Count;

        /// <summary>
        /// 是否没有任何已存储的格子。
        /// </summary>
        public bool IsEmpty => _grid.Count == 0;

        /// <summary>
        /// 判断坐标是否已被显式写入过（写入值为 default(T) 也返回 true）。
        /// </summary>
        /// <param name="x">x 坐标</param>
        /// <param name="y">y 坐标</param>
        /// <returns>已存储返回 true，否则返回 false</returns>
        public bool Contains(int x, int y)
        {
            return _grid.ContainsKey((x, y));
        }

        /// <summary>
        /// 尝试读取指定坐标的值。
        /// 已存储（包括值为 default(T)）返回 true；未存储返回 false 且 value 为 default(T)。
        /// </summary>
        /// <param name="x">x 坐标</param>
        /// <param name="y">y 坐标</param>
        /// <param name="value">读取到的值；未存储时为 default(T)</param>
        /// <returns>坐标已存储返回 true，否则返回 false</returns>
        public bool TryGet(int x, int y, out T value)
        {
            return _grid.TryGetValue((x, y), out value);
        }

        /// <summary>
        /// 遍历所有已存储的格子。顺序不承诺（取决于 Dictionary 枚举顺序）；
        /// 遍历期间不要新增或移除坐标，否则会触发 Dictionary 的 InvalidOperationException。
        /// </summary>
        /// <param name="each">遍历回调，参数依次为 (x, y, value)；为 null 时不执行任何操作</param>
        public void ForEach(Action<int, int, T> each)
        {
            foreach (var item in _grid)
            {
                each?.Invoke(item.Key.x, item.Key.y, item.Value);
            }
        }

        /// <summary>
        /// 遍历所有已存储的值。顺序不承诺（取决于 Dictionary 枚举顺序）；
        /// 遍历期间不要新增或移除坐标，否则会触发 Dictionary 的 InvalidOperationException。
        /// </summary>
        /// <param name="each">遍历回调；为 null 时不执行任何操作</param>
        public void ForEach(Action<T> each)
        {
            foreach (var item in _grid)
            {
                each?.Invoke(item.Value);
            }
        }

        /// <summary>
        /// 按 (x, y) 读取或写入格子数据。读取未存储坐标返回 default(T)；写入即存储该坐标。
        /// </summary>
        /// <param name="indexX">x 坐标</param>
        /// <param name="indexY">y 坐标</param>
        public T this[int indexX, int indexY]
        {
            get
            {
                return _grid.GetValueOrDefault((indexX, indexY));
            }
            set
            {
                _grid[(indexX, indexY)] = value;
            }
        }

        /// <summary>
        /// 移除所有已存储的格子，并对每个已存储项调用一次可选的清理回调。
        /// </summary>
        /// <param name="cleanupItem">可选的清理回调；回调抛异常时不会移除任何条目</param>
        public void Clear(Action<T> cleanupItem = null)
        {
            ForEach(cleanupItem);
            _grid.Clear();
        }
    }
}
