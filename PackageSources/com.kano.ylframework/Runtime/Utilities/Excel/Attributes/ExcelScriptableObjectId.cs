using System;

namespace YLFramework.Excel
{
    /// <summary>标识ScriptableObject数据的ID字段，由作者工具解释。</summary>
    /// <remarks>也可用于普通行数据的 ID；本标记不生成 ID、不改变 Unity 序列化，行匹配与重复校验由作者工具负责。</remarks>
    public class ExcelScriptableObjectId : Attribute
    {
    }
}
