#if UNITY_EDITOR

using UnityEditor;

namespace YLFramework.Binding
{
    /// <summary>
    /// 以 float 持久化到 EditorPrefs 的属性。
    /// </summary>
    public class EditorPrefsFloatProperty : EditorPrefsProperty<float>
    {
        /// <summary>
        /// 创建属性并立即从 EditorPrefs 读取。
        /// </summary>
        /// <param name="key">EditorPrefs 存储键，不能为 null 或空字符串。</param>
        /// <param name="initValue">键不存在时的默认值。</param>
        public EditorPrefsFloatProperty(string key, float initValue = 0.0f) : base(key, initValue)
        {
        }

        /// <inheritdoc />
        protected override float Load(float defaultValue) => EditorPrefs.GetFloat(Key, defaultValue);

        /// <inheritdoc />
        protected override void Save(float value) => EditorPrefs.SetFloat(Key, value);
    }
}

#endif
