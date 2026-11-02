using UnityEngine;

namespace YLFramework.Binding
{
    /// <summary>
    /// 以 int 持久化到 PlayerPrefs 的属性。
    /// </summary>
    public class PlayerPrefsIntProperty : PlayerPrefsProperty<int>
    {
        /// <summary>
        /// 创建属性并立即从 PlayerPrefs 读取。
        /// </summary>
        /// <param name="saveKey">PlayerPrefs 存储键，不能为 null 或空字符串。</param>
        /// <param name="defaultValue">键不存在时的默认值。</param>
        public PlayerPrefsIntProperty(string saveKey, int defaultValue = 0) : base(saveKey, defaultValue)
        {
        }

        /// <inheritdoc />
        protected override int Load(int defaultValue) => PlayerPrefs.GetInt(Key, defaultValue);

        /// <inheritdoc />
        protected override void Save(int value) => PlayerPrefs.SetInt(Key, value);
    }
}
