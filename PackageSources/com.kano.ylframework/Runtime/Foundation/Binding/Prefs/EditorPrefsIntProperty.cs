#if UNITY_EDITOR

using UnityEditor;

namespace YLFramework.Binding
{
    /// <summary>
    /// 以 int 持久化到 EditorPrefs 的属性。
    /// </summary>
    public class EditorPrefsIntProperty : EditorPrefsProperty<int>
    {
        /// <summary>
        /// 创建属性并立即从 EditorPrefs 读取。
        /// </summary>
        /// <param name="key">EditorPrefs 存储键，不能为 null 或空字符串。</param>
        /// <param name="initValue">键不存在时的默认值。</param>
        public EditorPrefsIntProperty(string key, int initValue = 0) : base(key, initValue)
        {
        }

        /// <inheritdoc />
        protected override int Load(int defaultValue) => EditorPrefs.GetInt(Key, defaultValue);

        /// <inheritdoc />
        protected override void Save(int value) => EditorPrefs.SetInt(Key, value);
    }
}

#endif
