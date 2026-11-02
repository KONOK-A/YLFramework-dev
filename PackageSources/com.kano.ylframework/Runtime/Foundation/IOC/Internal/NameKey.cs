namespace YLFramework.DependencyInjection
{
    /// <summary>
    /// 注册名称归一化：null 与空字符串统一视为“无名”，供容器与多键集合复用。
    /// </summary>
    internal static class NameKey
    {
        /// <summary>
        /// 归一化注册名称。
        /// </summary>
        internal static string Normalize(string name) => string.IsNullOrEmpty(name) ? null : name;
    }
}
