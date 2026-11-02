using System;
using System.Collections.Generic;
using System.Text;

namespace YLFramework.DependencyInjection
{
    /// <summary>
    /// 单次解析链的类型路径栈：用于循环依赖检测与异常依赖链上下文。
    /// 仅在构造流程中 push/pop，异常路径通过 try/finally 保证出栈；不做依赖图建模。
    /// </summary>
    internal sealed class ResolutionPath
    {
        private readonly List<Type> _types = new List<Type>();

        internal void Push(Type type)
        {
            _types.Add(type);
        }

        internal void Pop()
        {
            if (_types.Count > 0)
            {
                _types.RemoveAt(_types.Count - 1);
            }
        }

        /// <summary>
        /// 判断某个类型是否已在本条解析链中（重复出现即循环）。
        /// </summary>
        internal bool Contains(Type type) => _types.Contains(type);

        /// <summary>
        /// 生成依赖链字符串；<paramref name="closingType"/> 不为 null 时追加为链尾（用于表示循环闭环或缺失的依赖类型）。
        /// </summary>
        internal string Format(Type closingType = null)
        {
            if (_types.Count == 0 && closingType == null)
            {
                return string.Empty;
            }

            var builder = new StringBuilder();
            for (var i = 0; i < _types.Count; i++)
            {
                if (builder.Length > 0)
                {
                    builder.Append(" -> ");
                }

                builder.Append(_types[i].Name);
            }

            if (closingType != null)
            {
                if (builder.Length > 0)
                {
                    builder.Append(" -> ");
                }

                builder.Append(closingType.Name);
            }

            return builder.ToString();
        }
    }
}
