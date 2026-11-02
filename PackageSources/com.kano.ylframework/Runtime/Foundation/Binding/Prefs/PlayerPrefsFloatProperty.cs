using UnityEngine;

namespace YLFramework.Binding
{
    /// <summary>
    /// 以 float 持久化到 PlayerPrefs 的属性。
    /// </summary>
    public class PlayerPrefsFloatProperty : PlayerPrefsProperty<float>
    {
        /// <summary>
        /// 创建属性并立即从 PlayerPrefs 读取。
        /// </summary>
        /// <param name="saveKey">PlayerPrefs 存储键，不能为 null 或空字符串。</param>
        /// <param name="defaultValue">键不存在时的默认值。</param>
        public PlayerPrefsFloatProperty(string saveKey, float defaultValue = 0.0f) : base(saveKey, defaultValue)
        {
        }

        /// <inheritdoc />
        protected override float Load(float defaultValue) => PlayerPrefs.GetFloat(Key, defaultValue);

        /// <inheritdoc />
        protected override void Save(float value) => PlayerPrefs.SetFloat(Key, value);
    }
}
