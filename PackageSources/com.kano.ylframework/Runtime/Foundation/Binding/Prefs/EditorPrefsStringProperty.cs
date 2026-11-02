#if UNITY_EDITOR

using UnityEditor;

namespace YLFramework.Binding
{
    /// <summary>
    /// 以 string 持久化到 EditorPrefs 的属性；保存 null 时会写入空字符串。
    /// </summary>
    public class EditorPrefsStringProperty : EditorPrefsProperty<string>
    {
        /// <summary>
        /// 创建属性并立即从 EditorPrefs 读取。
        /// </summary>
        /// <param name="key">EditorPrefs 存储键，不能为 null 或空字符串。</param>
        /// <param name="initValue">键不存在时的默认值。</param>
        public EditorPrefsStringProperty(string key, string initValue = "") : base(key, initValue)
        {
        }

        /// <inheritdoc />
        protected override string Load(string defaultValue) => EditorPrefs.GetString(Key, defaultValue);

        /// <inheritdoc />
        protected override void Save(string value) => EditorPrefs.SetString(Key, value ?? string.Empty);
    }
}

#endif
