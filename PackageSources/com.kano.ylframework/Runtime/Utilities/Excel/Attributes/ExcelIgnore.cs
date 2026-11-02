using System;

namespace YLFramework.Excel
{
    /// <summary>作者工具跳过该字段；本标记不包含Excel解析或文件操作。</summary>
    /// <remarks>仅影响识别本标记的字段导入导出，不改变 Unity 或 JSON 的常规序列化资格。</remarks>
    public class ExcelIgnore : Attribute
    {
    }
}
