using System;
using System.Reflection;

namespace YLFramework.Excel
{
    /// <summary>字段作者工具的转换与赋值边界；保留来源扩展方式，不引入解析器。</summary>
    /// <remarks>
    /// 导入由作者工具先调用 Import，再把其结果传给 SetValue；导出只调用 Export。
    /// 所属行可以是普通对象或 ScriptableObject；转换器不负责工作簿读写、资产保存或导入事务。
    /// </remarks>
    public abstract class CustomExcelFieldImportAndExport : Attribute
    {
        /// <summary>将输入单元值转换为领域字段所需的数据。</summary>
        /// <param name="scriptableObject">所属行数据；资产预览导入时为作者工具准备的暂存副本。</param>
        /// <param name="value">工作簿读取后传入的原始单元格值。</param>
        /// <param name="type">表头声明的字段种类，由转换器解释。</param>
        /// <returns>交给 SetValue 的转换结果，不要求与单元格值同类型。</returns>
        public abstract object Import(object scriptableObject, object value, ExcelFieldsType type);

        /// <summary>将领域字段转换为作者工具可写入的值。</summary>
        /// <param name="scriptableObject">被导出的所属行数据，不转移其所有权。</param>
        /// <param name="value">从目标字段读取的当前值。</param>
        /// <param name="type">作者工具根据字段类型确定的种类。</param>
        /// <returns>用于写入单元格的值；例如把领域引用列表转换为 ID JSON 文本。</returns>
        public abstract object Export(object scriptableObject, object value, ExcelFieldsType type);

        /// <summary>将Import的结果赋给目标字段；由具体转换器处理领域引用。</summary>
        /// <param name="scriptableObject">接收赋值的所属行数据。</param>
        /// <param name="fieldInfo">需要写入的字段，不由本契约自动执行反射赋值。</param>
        /// <param name="value">Import 返回的已转换结果。</param>
        public abstract void SetValue(object scriptableObject, FieldInfo fieldInfo, object value);
    }
}
