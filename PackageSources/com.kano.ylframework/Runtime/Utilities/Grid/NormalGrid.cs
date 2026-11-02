using System;
using UnityEngine;

namespace YLFramework.Grids
{
    /// <summary>
    /// 固定尺寸的二维网格容器，按 (x, y) 存取数据。
    /// 合法坐标范围为 0 &lt;= x &lt; Width、0 &lt;= y &lt; Height，不支持负坐标。
    /// 越界访问不抛异常：索引器记录警告后返回 default(T) 或忽略写入，TryGet / TrySet 静默返回 false。
    /// </summary>
    /// <typeparam name="T">网格存储的数据类型</typeparam>
    public class NormalGrid<T>
    {
        private T[,] _grid;

        /// <summary>
        /// 网格宽度（x 方向的有效坐标数量）。
        /// </summary>
        public int Width
        {
            get; private set;
        }

        /// <summary>
        /// 网格高度（y 方向的有效坐标数量）。
        /// </summary>
        public int Height
        {
            get; private set;
        }

        /// <summary>
        /// 网格总格子数（Width * Height）。
        /// </summary>
        public int Count => Width * Height;

        /// <summary>
        /// 网格是否为空（Width 或 Height 为 0）。
        /// </summary>
        public bool IsEmpty => Width == 0 || Height == 0;

        /// <summary>
        /// 构造指定尺寸的网格，所有格子初始为 default(T)。
        /// </summary>
        /// <param name="width">网格宽度，不能为负数</param>
        /// <param name="height">网格高度，不能为负数</param>
        /// <exception cref="ArgumentOutOfRangeException">尺寸为负数或总元素数超出支持上限时抛出</exception>
        public NormalGrid(int width, int height)
        {
            ValidateSize(width, height);
            _grid = new T[width, height];
            Width = width;
            Height = height;
        }

        /// <summary>
        /// 按 (x, y) 存取格子数据。
        /// 越界读取返回 default(T) 并输出警告；越界写入被忽略并输出警告。
        /// </summary>
        /// <param name="indexX">x 坐标</param>
        /// <param name="indexY">y 坐标</param>
        public T this[int indexX, int indexY]
        {
            get
            {
                if (IsInBounds(indexX, indexY))
                {
                    return _grid[indexX, indexY];
                }

                Debug.LogWarning($"FrameWorkError: out of bounds [{indexX}:{indexY}] in grid[{Width}:{Height}]");
                return default;
            }
            set
            {
                if (IsInBounds(indexX, indexY))
                {
                    _grid[indexX, indexY] = value;
                }
                else
                {
                    Debug.LogWarning($"FrameWorkError: out of bounds [{indexX}:{indexY}] in grid[{Width}:{Height}]");
                }
            }
        }

        /// <summary>
        /// 判断坐标是否落在有效范围内。
        /// </summary>
        /// <param name="x">x 坐标</param>
        /// <param name="y">y 坐标</param>
        /// <returns>坐标有效返回 true，否则返回 false</returns>
        public bool IsInBounds(int x, int y)
        {
            return x >= 0 && x < Width && y >= 0 && y < Height;
        }

        /// <summary>
        /// 尝试读取指定坐标的值；越界时静默返回 false，且 value 为 default(T)。
        /// </summary>
        /// <param name="x">x 坐标</param>
        /// <param name="y">y 坐标</param>
        /// <param name="value">读取到的值；越界时为 default(T)</param>
        /// <returns>读取成功返回 true，越界返回 false</returns>
        public bool TryGet(int x, int y, out T value)
        {
            if (IsInBounds(x, y))
            {
                value = _grid[x, y];
                return true;
            }

            value = default;
            return false;
        }

        /// <summary>
        /// 尝试写入指定坐标的值；越界时静默返回 false 且不修改网格。
        /// </summary>
        /// <param name="x">x 坐标</param>
        /// <param name="y">y 坐标</param>
        /// <param name="value">要写入的值</param>
        /// <returns>写入成功返回 true，越界返回 false</returns>
        public bool TrySet(int x, int y, T value)
        {
            if (!IsInBounds(x, y))
            {
                return false;
            }

            _grid[x, y] = value;
            return true;
        }

        /// <summary>
        /// 用同一个值填充整个网格，遍历顺序为 x 外层、y 内层。
        /// </summary>
        /// <param name="t">填充的值</param>
        public void Fill(T t)
        {
            for (var x = 0; x < Width; x++)
            {
                for (var y = 0; y < Height; y++)
                {
                    _grid[x, y] = t;
                }
            }
        }

        /// <summary>
        /// 用委托按坐标生成的值填充整个网格，遍历顺序为 x 外层、y 内层。
        /// </summary>
        /// <param name="valueGetter">值获取器，参数依次为 (x, y)</param>
        /// <exception cref="ArgumentNullException">valueGetter 为 null 时抛出</exception>
        public void Fill(Func<int, int, T> valueGetter)
        {
            if (valueGetter == null)
            {
                throw new ArgumentNullException(nameof(valueGetter));
            }

            for (var x = 0; x < Width; x++)
            {
                for (var y = 0; y < Height; y++)
                {
                    _grid[x, y] = valueGetter(x, y);
                }
            }
        }

        /// <summary>
        /// 调整网格尺寸，保留新旧范围交集中的已有数据，新增区域填充 default(T)。
        /// 缩小尺寸时超范围数据被丢弃；操作失败时原网格保持不变。
        /// </summary>
        /// <param name="width">新宽度</param>
        /// <param name="height">新高度</param>
        /// <exception cref="ArgumentOutOfRangeException">尺寸为负数或总元素数超出支持上限时抛出</exception>
        public void Resize(int width, int height)
        {
            ResizeInternal(width, height, null);
        }

        /// <summary>
        /// 调整网格尺寸，保留新旧范围交集中的已有数据，新增区域由 addValueGetter 生成。
        /// 缩小尺寸时超范围数据被丢弃；操作失败时原网格保持不变。
        /// </summary>
        /// <param name="width">新宽度</param>
        /// <param name="height">新高度</param>
        /// <param name="addValueGetter">新增区域的值获取器，参数依次为 (x, y)</param>
        /// <exception cref="ArgumentNullException">addValueGetter 为 null 时抛出</exception>
        /// <exception cref="ArgumentOutOfRangeException">尺寸为负数或总元素数超出支持上限时抛出</exception>
        public void Resize(int width, int height, Func<int, int, T> addValueGetter)
        {
            if (addValueGetter == null)
            {
                throw new ArgumentNullException(nameof(addValueGetter));
            }

            ResizeInternal(width, height, addValueGetter);
        }

        /// <summary>在新数组中保留交集并填充新增格子，全部准备完成后再替换网格与尺寸。</summary>
        private void ResizeInternal(int width, int height, Func<int, int, T> addValueGetter)
        {
            ValidateSize(width, height);

            var newGrid = new T[width, height];

            // 拷贝重叠区域，缩小尺寸时不会越界；保留数据的新旧坐标保持一致。
            var copyWidth = Math.Min(Width, width);
            var copyHeight = Math.Min(Height, height);
            for (var x = 0; x < copyWidth; x++)
            {
                for (var y = 0; y < copyHeight; y++)
                {
                    newGrid[x, y] = _grid[x, y];
                }
            }

            // 新增区域按 (x, y) 递增顺序生成；委托抛异常时尚未提交，原网格不受影响。
            if (addValueGetter != null)
            {
                for (var x = 0; x < width; x++)
                {
                    for (var y = 0; y < height; y++)
                    {
                        if (x >= copyWidth || y >= copyHeight)
                        {
                            newGrid[x, y] = addValueGetter(x, y);
                        }
                    }
                }
            }

            // 一次性提交，保证 Width / Height / _grid 始终一致。
            Width = width;
            Height = height;
            _grid = newGrid;
        }

        /// <summary>
        /// 遍历整个网格，顺序为 x 外层、y 内层递增。
        /// 遍历使用进入时的数组与尺寸快照，回调中 Resize 不会改变本次遍历范围或造成越界。
        /// </summary>
        /// <param name="each">遍历回调，参数依次为 (x, y, value)；为 null 时不执行任何操作</param>
        public void ForEach(Action<int, int, T> each)
        {
            // 快照本次遍历的存储与边界，避免回调中 Resize 后旧索引访问新数组。
            var grid = _grid;
            var width = Width;
            var height = Height;
            for (var x = 0; x < width; x++)
            {
                for (var y = 0; y < height; y++)
                {
                    each?.Invoke(x, y, grid[x, y]);
                }
            }
        }

        /// <summary>
        /// 遍历整个网格的所有值，顺序为 x 外层、y 内层递增。
        /// 遍历使用进入时的数组与尺寸快照，回调中 Resize 不会改变本次遍历范围或造成越界。
        /// </summary>
        /// <param name="each">遍历回调；为 null 时不执行任何操作</param>
        public void ForEach(Action<T> each)
        {
            var grid = _grid;
            var width = Width;
            var height = Height;
            for (var x = 0; x < width; x++)
            {
                for (var y = 0; y < height; y++)
                {
                    each?.Invoke(grid[x, y]);
                }
            }
        }

        /// <summary>
        /// 清空网格内容：保留 Width / Height，先对每个格子调用 cleanupItem，再写回 default(T)。
        /// </summary>
        /// <param name="cleanupItem">
        /// 可选的清理回调，每个格子调用一次（包括值为 default(T) 的格子）；
        /// 回调抛异常时清空会被中断，但尺寸与网格后续可用性不受影响。
        /// </param>
        public void Clear(Action<T> cleanupItem = null)
        {
            for (var x = 0; x < Width; x++)
            {
                for (var y = 0; y < Height; y++)
                {
                    cleanupItem?.Invoke(_grid[x, y]);
                    _grid[x, y] = default;
                }
            }
        }

        /// <summary>拒绝负尺寸或总格子数超出 int 上限的请求，实际数组分配仍受可用内存限制。</summary>
        private static void ValidateSize(int width, int height)
        {
            if (width < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(width), width, "网格宽度不能为负数。");
            }

            if (height < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(height), height, "网格高度不能为负数。");
            }

            if ((long)width * height > int.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(width), "网格总元素数超出支持上限。");
            }
        }
    }
}
