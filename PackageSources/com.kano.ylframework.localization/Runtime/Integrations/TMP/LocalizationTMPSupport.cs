namespace YLFramework.Localization
{
    /// <summary>如实查询当前编译环境是否提供 TMP 表现组件；程序集存在不等于 SDK 存在。</summary>
    public static class LocalizationTMPSupport
    {
        /// <summary>Unity 6 的 UGUI 内置 TMP，较早版本需要实际安装 TMP 包。</summary>
        public static bool IsAvailable
        {
            get
            {
#if UNITY_6000_0_OR_NEWER || YL_LOCALIZATION_TMP
                return true;
#else
                return false;
#endif
            }
        }
    }
}
