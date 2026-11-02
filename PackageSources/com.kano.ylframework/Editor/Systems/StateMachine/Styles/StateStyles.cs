using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;


namespace YLFramework.Editor.StateMachine
{
    /// <summary>节点配色、选中态与六边形样式的内部组合键；数值排列用于映射编辑器样式名。</summary>
    internal enum Style
    {
        Normal = 0,
        Blue,
        Mint,
        Green,
        Yellow,
        Orange,
        Red,
        NormalOn,
        BlueOn,
        MintOn,
        GreenOn,
        YellowOn,
        OrangeOn,
        RedOn,

        NormalHEX,
        BlueHEX,
        MintHEX,
        GreenHEX,
        YellowHEX,
        OrangeHEX,
        RedHEX,
        NormalOnHEX,
        BlueOnHEX,
        MintOnHEX,
        GreenOnHEX,
        YellowOnHEX,
        OrangeOnHEX,
        RedOnHEX,
    }

    /// <summary>缓存节点 GUIStyle，并为编辑器命名样式提供缺失时的后备样式。</summary>
    internal class StateStyles
    {
        private static Dictionary<Style, GUIStyle> _styleDictionary = null;
        /// <summary>按样式键的数值布局生成编辑器样式名称，并缓存独立 GUIStyle 副本。</summary>
        static StateStyles()
        {

            _styleDictionary = new Dictionary<Style, GUIStyle>();

            foreach (Style style in Enum.GetValues(typeof(Style)))
            {
                int value = (int)style;
                string name = $"flow node {(value >= 14 ? "hex " : string.Empty)}{value % 7}{((value / 7) % 2 == 1 ? " on" : string.Empty)}";
                _styleDictionary.Add(style, new GUIStyle(Named(name, EditorStyles.helpBox)));
            }

        }

        /// <summary>读取已缓存的节点样式，不重新创建样式对象。</summary>
        /// <param name="style">节点样式组合键。</param>
        /// <returns>缓存样式；键未定义时为 null。</returns>
        public static GUIStyle Get(Style style)
        {
            if (_styleDictionary.ContainsKey(style))
            {
                return _styleDictionary[style];
            }

            return null;
        }

        /// <summary>从当前 GUI 皮肤查找命名样式，缺失时使用指定后备或标准标签样式。</summary>
        /// <param name="name">编辑器样式名称。</param>
        /// <param name="fallback">可选后备样式。</param>
        /// <returns>找到的样式或后备样式。</returns>
        public static GUIStyle Named(string name, GUIStyle fallback = null)
            => GUI.skin.FindStyle(name) ?? fallback ?? EditorStyles.label;

    }

}
