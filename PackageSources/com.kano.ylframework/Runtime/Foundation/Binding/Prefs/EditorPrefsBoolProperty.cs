#if UNITY_EDITOR

using UnityEditor;

namespace YLFramework.Binding
{
    /// <summary>
    /// 以 bool 持久化到 EditorPrefs 的属性。
    /// </summary>
    public class EditorPrefsBoolProperty : EditorPrefsProperty<bool>
    {
        /// <summary>
        /// 创建属性并立即从 EditorPrefs 读取。
        /// </summary>
        /// <param name="key">EditorPrefs 存储键，不能为 null 或空字符串。</param>
        /// <param name="initValue">键不存在时的默认值。</param>
        public EditorPrefsBoolProperty(string key, bool initValue = false) : base(key, initValue)
        {
        }

        /// <inheritdoc />
        protected override bool Load(bool defaultValue) => EditorPrefs.GetBool(Key, defaultValue);

        /// <inheritdoc />
        protected override void Save(bool value) => EditorPrefs.SetBool(Key, value);
    }
}

#endif
