using UnityEngine;

namespace YLFramework.Binding
{
    /// <summary>
    /// 以 string 持久化到 PlayerPrefs 的属性；保存 null 时会写入空字符串。
    /// </summary>
    public class PlayerPrefsStringProperty : PlayerPrefsProperty<string>
    {
        /// <summary>
        /// 创建属性并立即从 PlayerPrefs 读取。
        /// </summary>
        /// <param name="saveKey">PlayerPrefs 存储键，不能为 null 或空字符串。</param>
        /// <param name="defaultValue">键不存在时的默认值。</param>
        public PlayerPrefsStringProperty(string saveKey, string defaultValue = "") : base(saveKey, defaultValue)
        {
        }

        /// <inheritdoc />
        protected override string Load(string defaultValue) => PlayerPrefs.GetString(Key, defaultValue);

        /// <inheritdoc />
        protected override void Save(string value) => PlayerPrefs.SetString(Key, value ?? string.Empty);
    }
}
