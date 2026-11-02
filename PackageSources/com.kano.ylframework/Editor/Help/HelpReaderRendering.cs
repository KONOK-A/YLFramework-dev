using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace YLFramework.Editor.Help
{
    /// <summary>标题区各控件按窗口宽度算出的矩形布局；所有位置在构造时一次确定。</summary>
    internal readonly struct HelpHeaderLayout
    {
        internal readonly Rect Title;
        internal readonly Rect Module;
        internal readonly Rect Back;
        internal readonly Rect Tabs;
        internal readonly Rect Search;
        internal readonly Rect Clear;
        internal readonly Rect Scope;
        internal readonly float BodyY;

        /// <summary>根据窗口宽度分配标题、模块选择、页签和搜索区域，不改动窗口状态。</summary>
        internal HelpHeaderLayout(float width)
        {
            Title = new Rect(12, 10, 100, 32);
            Back = new Rect(width - 92, Title.y, 80, Title.height);
            Module = new Rect(Title.xMax + 10, Title.y, Mathf.Max(0, Back.x - Title.xMax - 20), Title.height);
            Tabs = new Rect(12, Title.yMax + 12, width - 24, 34);
            Clear = new Rect(width - 86, Tabs.yMax + 12, 74, 30);
            Search = new Rect(12, Clear.y, Mathf.Max(0, Clear.x - 22), Clear.height);
            Scope = new Rect(12, Search.yMax + 8, width - 24, 20);
            BodyY = Scope.yMax + 10;
        }

        /// <summary>取得三个等宽页签之一的矩形；调用方提供 0 到 2 的索引。</summary>
        internal Rect Tab(int index) => new Rect(Tabs.x + Tabs.width * index / 3, Tabs.y, Tabs.width / 3, Tabs.height);
    }

    /// <summary>搜索结果高亮：匹配区间与像素矩形按文本缓存，绘制时在文本下方叠加色块。</summary>
    /// <remarks>几何缓存以文本、样式和 DPI 为键；阅读宽度变化由窗口清除缓存，尺寸不同也不会复用。</remarks>
    internal sealed class HelpSearchHighlight
    {
        /// <summary>文本中左闭右开的字符索引区间，不包含像素坐标。</summary>
        internal readonly struct Range
        {
            internal readonly int Start;
            internal readonly int End;
            /// <summary>保存一次匹配的起始索引与排他结束索引。</summary>
            internal Range(int start, int end)
            {
                Start = start;
                End = end;
            }
        }

        private string _query;
        private string[] _terms = Array.Empty<string>();
        private readonly Dictionary<string, Range[]> _ranges = new Dictionary<string, Range[]>();
        private readonly Dictionary<(string, GUIStyle, float), (Vector2 Size, Rect[] Fragments)> _geometry =
            new Dictionary<(string, GUIStyle, float), (Vector2, Rect[])>();

        /// <summary>丢弃查询、字符区间和几何缓存，供模块或搜索上下文变化时重新建立。</summary>
        internal void Clear()
        {
            _query = null;
            _terms = Array.Empty<string>();
            _ranges.Clear();
            _geometry.Clear();
        }

        /// <summary>只丢弃像素矩形，保留当前查询及文本匹配区间。</summary>
        internal void ClearGeometry() => _geometry.Clear();

        /// <summary>更新按空白分词并去重的查询；查询变化时同时失效所有缓存。</summary>
        internal void SetQuery(string query)
        {
            // 每次绘制前都会调用；查询未变时保留已有缓存。
            if (_query == query)
            {
                return;
            }

            Clear();
            _query = query;
            _terms = (query ?? "").Split((char[])null, StringSplitOptions.RemoveEmptyEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }

        /// <summary>缓存当前查询在文本中的字面匹配，合并重叠与相接区间；返回数组只供借读。</summary>
        internal Range[] GetRanges(string text)
        {
            if (_terms.Length == 0 || string.IsNullOrEmpty(text))
            {
                return Array.Empty<Range>();
            }

            if (_ranges.TryGetValue(text, out var cached))
            {
                return cached;
            }

            var found = new List<Range>();
            foreach (var term in _terms)
            {
                for (var start = text.IndexOf(term, StringComparison.OrdinalIgnoreCase); start >= 0;
                    start = text.IndexOf(term, start + 1, StringComparison.OrdinalIgnoreCase))
                {
                    found.Add(new Range(start, start + term.Length));
                }
            }

            found.Sort((a, b) => a.Start.CompareTo(b.Start));
            // 合并重叠或相接的区间，避免高亮在连接处重复绘制或留缝。
            var merged = new List<Range>();
            foreach (var range in found)
            {
                if (merged.Count != 0 && range.Start <= merged[merged.Count - 1].End)
                {
                    var previous = merged[merged.Count - 1];
                    merged[merged.Count - 1] = new Range(previous.Start, Math.Max(previous.End, range.End));
                }
                else
                {
                    merged.Add(range);
                }
            }
            return _ranges[text] = merged.ToArray();
        }

        /// <summary>取得给定样式、DPI 和绘制尺寸下相对原点的高亮矩形，优先复用缓存。</summary>
        internal Rect[] GetFragments(string text, GUIStyle style, Vector2 size)
        {
            var ranges = GetRanges(text);
            if (ranges.Length == 0)
            {
                return Array.Empty<Rect>();
            }

            var key = (text, style, EditorGUIUtility.pixelsPerPoint);
            // 尺寸不参与缓存键，需要单独比较：同一文本会按不同阅读宽度绘制。
            if (_geometry.TryGetValue(key, out var cached) && cached.Size == size)
            {
                return cached.Fragments;
            }

            var fragments = BuildFragments(text, style, new Rect(Vector2.zero, size), ranges);
            _geometry[key] = (size, fragments);
            return fragments;
        }

        /// <summary>仅在 Repaint 时将缓存片段平移到目标区域并绘制高亮背景。</summary>
        internal void Draw(Rect rect, string text, GUIStyle style, bool dark)
        {
            if (Event.current.type != EventType.Repaint)
            {
                return;
            }

            var color = dark ? new Color(0.66f, 0.46f, 0.13f, 0.42f) : new Color(0.92f, 0.65f, 0.20f, 0.36f);
            foreach (var fragment in GetFragments(text, style, rect.size))
            {
                EditorGUI.DrawRect(new Rect(rect.position + fragment.position, fragment.size), color);
            }
        }

        /// <summary>把字符区间转换成逐行的像素矩形；换行处不绘制，同一行的相邻片段合并。</summary>
        internal static Rect[] BuildFragments(string text, GUIStyle style, Rect rect, Range[] ranges)
        {
            // 与缓存路径共用局部原点，避免 TextCore 的绝对坐标像素取整改变平移后的几何。
            var origin = rect.position;
            rect.position = Vector2.zero;
            var content = new GUIContent(text);
            var fragments = new List<Rect>();
            GUIStyle unwrapped = null;
#if UNITY_6000_2_OR_NEWER
            // TextCore 接收元素索引；CRLF 和有效 surrogate pair 各只占一个元素，原文区间仍保留 UTF-16 索引。
            var cursorIndices = new int[text.Length + 1];
            var cursorIndex = 0;
            for (var i = 0; i < text.Length; i++)
            {
                cursorIndices[i] = cursorIndex;
                if (i + 1 < text.Length && ((text[i] == '\r' && text[i + 1] == '\n') ||
                    (char.IsHighSurrogate(text[i]) && char.IsLowSurrogate(text[i + 1]))))
                {
                    cursorIndices[++i] = cursorIndex;
                }
                cursorIndices[i + 1] = ++cursorIndex;
            }
            int CursorIndex(int sourceIndex) => cursorIndices[sourceIndex];
#else
            int CursorIndex(int sourceIndex) => sourceIndex;
#endif
            foreach (var range in ranges)
            {
                var start = style.GetCursorPixelPosition(rect, content, CursorIndex(range.Start));
                for (var i = range.Start; i < range.End; i++)
                {
                    var end = style.GetCursorPixelPosition(rect, content, CursorIndex(i + 1));
                    if (text[i] != '\r' && text[i] != '\n')
                    {
                        var right = end.x;
                        if (Mathf.Abs(end.y - start.y) > 0.5f)
                        {
                            // 换行边界的光标属于下一行；使用相同字体关闭自动换行，测量本行最后一个字形的宽度。
                            if (unwrapped == null)
                            {
                                unwrapped = new GUIStyle(style) { wordWrap = false };
                            }

                            right = start.x + unwrapped.GetCursorPixelPosition(rect, content, CursorIndex(i + 1)).x -
                                unwrapped.GetCursorPixelPosition(rect, content, CursorIndex(i)).x;
                        }
                        var fragment = Rect.MinMaxRect(Mathf.Max(rect.xMin, start.x), Mathf.Max(rect.yMin, start.y),
                            Mathf.Min(rect.xMax, right), Mathf.Min(rect.yMax, start.y + style.lineHeight));
                        fragment.position += origin;
                        if (fragment.width > 0 && fragment.height > 0)
                        {
                            if (fragments.Count != 0 && Mathf.Abs(fragments[fragments.Count - 1].y - fragment.y) < 0.5f &&
                                fragment.xMin <= fragments[fragments.Count - 1].xMax + 0.5f)
                            {
                                var previous = fragments[fragments.Count - 1];
                                previous.xMax = Mathf.Max(previous.xMax, fragment.xMax);
                                fragments[fragments.Count - 1] = previous;
                            }
                            else
                            {
                                fragments.Add(fragment);
                            }
                        }
                    }
                    start = end;
                }
            }
            return fragments.ToArray();
        }
    }
}
