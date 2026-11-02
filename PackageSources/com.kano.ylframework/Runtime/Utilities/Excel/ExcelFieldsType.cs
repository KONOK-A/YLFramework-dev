namespace YLFramework.Excel
{
    /// <summary>沿用来源字段种类和数值；不表示已有xlsx读取后端。</summary>
    public enum ExcelFieldsType
    {
        /// <summary>未指定字段种类。</summary>
        None,
        /// <summary>32位整数。</summary>
        Int,
        /// <summary>单精度浮点数。</summary>
        Float,
        /// <summary>双精度浮点数。</summary>
        Double,
        /// <summary>文本。</summary>
        String,
        /// <summary>布尔值。</summary>
        Bool,
        /// <summary>64位整数。</summary>
        Long,
        /// <summary>由作者工具解释的JSON值。</summary>
        Json,
        /// <summary>由作者工具解析的资产引用。</summary>
        ObjectReference,
        /// <summary>枚举值。</summary>
        Enum
    }
}
