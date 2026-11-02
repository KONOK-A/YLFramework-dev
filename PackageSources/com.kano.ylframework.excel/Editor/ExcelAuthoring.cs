using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Newtonsoft.Json;
using UnityEngine;
using YLFramework.Excel;

namespace YLFramework.Integrations.Excel.Editor
{
    /// <summary>提供可选 Editor .xlsx 导出与一次性导入预览，不提供 Runtime 工作簿 IO。</summary>
    /// <remarks>工作表前三行为字段名、列型和描述，数据从第四行开始；表头是独立于 C# 字段改名的协议。</remarks>
    public static partial class ExcelAuthoring
    {
        /// <summary>记录 ScriptableObject 具体 CLR 类型全名的保留列名。</summary>
        public const string DataTypeColumn = "DATA_TYPE_FULL_CLASS_NAME";

        /// <summary>按声明行类型的字段导出工作簿，替换目标文件原有的全部工作表和格式。</summary>
        /// <typeparam name="T">用于确定导出字段的声明行类型。</typeparam>
        /// <param name="values">非空行集合，不允许包含 null 行。</param>
        /// <param name="path">父目录已存在的 .xlsx 输出路径。</param>
        /// <param name="sheetName">生成的工作表名。</param>
        public static void ListToExcel<T>(IEnumerable<T> values, string path, string sheetName = "Sheet1")
        {
            if (values == null)
            {
                throw new ArgumentNullException(nameof(values));
            }

            Export(values.Cast<object>().ToList(), typeof(T), path, sheetName, false);
        }

        /// <summary>将工作表转换为新行对象并生成整表替换预览，预览不修改原列表。</summary>
        /// <typeparam name="T">可通过公开无参构造创建的引用类型。</typeparam>
        /// <param name="path">待读取的 .xlsx 工作簿路径。</param>
        /// <param name="values">确认时被新行对象替换内容的目标列表，不在原行对象上合并字段。</param>
        /// <param name="sheetName">工作表名；null 使用后端默认的首张工作表。</param>
        /// <returns>包含错误和差异的一次性计划，调用方须确认、取消或释放。</returns>
        /// <remarks>
        /// 有 ID 字段时使用其文本键，否则使用行位置键；重复键拒绝提交。
        /// 提交前复核源文件、原列表及暂存字段状态，不为自定义转换的外部副作用提供回滚。
        /// </remarks>
        public static ExcelImportPlan PreviewList<T>(string path, List<T> values, string sheetName = null) where T : class, new()
        {
            var plan = new ExcelImportPlan();
            try
            {
                if (values == null)
                {
                    throw new ArgumentNullException(nameof(values));
                }

                var table = ExcelTable.Read(path, sheetName);
                var previous = values.ToArray();
                var previousStates = previous.Select(item => ExcelValues.State(item)).ToArray();
                var before = Snapshots(previous.Cast<object>());
                var staged = new List<T>();
                var keys = new HashSet<string>(StringComparer.Ordinal);
                foreach (var row in table.Rows)
                {
                    var target = new T();
                    ImportRow(table, row, target, plan);
                    try
                    {
                        if (!keys.Add(ExcelValues.Key(target, staged.Count)))
                        {
                            plan.Error(table.Source + ": duplicate ID at row " + row.Key);
                        }
                    }
                    catch (Exception e)
                    {
                        plan.Error(table.Source + ": row " + row.Key + ": " + e.Message);
                    }
                    staged.Add(target);
                }
                if (plan.Errors.Count == 0)
                {
                    plan.Diff(before, Snapshots(staged.Cast<object>()));
                }

                var stagedStates = staged.Select(item => ExcelValues.State(item)).ToArray();
                plan.Prepare(() =>
                {
                    table.AssertUnchanged();
                    if (!values.SequenceEqual(previous) || !values.Select(item => ExcelValues.State(item)).SequenceEqual(previousStates))
                    {
                        throw new InvalidOperationException("Target changed after preview; preview again.");
                    }

                    if (!staged.Select(item => ExcelValues.State(item)).SequenceEqual(stagedStates))
                    {
                        throw new InvalidOperationException("Staged data or asset references changed after preview; preview again.");
                    }

                    if (values.Capacity < staged.Count)
                    {
                        values.Capacity = staged.Count;
                    }

                    values.Clear();
                    values.AddRange(staged);
                });
            }
            catch (Exception e)
            {
                plan.Error(path + ": " + e.Message);
            }
            return plan;
        }

        /// <summary>按列型生成 JSON 数组预览，确认后通过临时文件替换目标 JSON。</summary>
        /// <param name="path">待读取的 .xlsx 工作簿路径。</param>
        /// <param name="jsonPath">与输入路径不同、父目录已存在的 .json 输出路径。</param>
        /// <param name="excludePrefix">忽略以此前缀开头的列，按大小写精确匹配；空值不排除列。</param>
        /// <param name="sheetName">工作表名；null 使用后端默认的首张工作表。</param>
        /// <returns>以输出路径为差异键的一次性计划，预检异常记录在计划中。</returns>
        /// <remarks>
        /// 枚举保留文本，资产引用仅校验 GUID[:localFileID] 格式而不解析资产。
        /// 提交前比较输入与目标文件哈希；这不是持续文件锁或跨文件事务。
        /// </remarks>
        public static ExcelImportPlan PreviewJson(string path, string jsonPath, string excludePrefix = "", string sheetName = null)
        {
            var plan = new ExcelImportPlan();
            try
            {
                if (!string.Equals(Path.GetExtension(jsonPath), ".json", StringComparison.OrdinalIgnoreCase))
                {
                    throw new FormatException("JSON output must end with .json.");
                }

                if (Path.GetFullPath(path).Equals(Path.GetFullPath(jsonPath), StringComparison.OrdinalIgnoreCase))
                {
                    throw new FormatException("Input and output must differ.");
                }

                var table = ExcelTable.Read(path, sheetName, excludePrefix);
                string originalHash = ExcelFiles.Hash(jsonPath);
                string original = File.Exists(jsonPath) ? File.ReadAllText(jsonPath) : null;
                var data = new List<Dictionary<string, object>>();
                foreach (var row in table.Rows)
                {
                    var item = new Dictionary<string, object>();
                    foreach (var column in table.Columns)
                    {
                        try
                        {
                            item.Add(column.Name, ExcelValues.Primitive(table.Value(row.Value, column), column.Type));
                        }
                        catch (Exception e)
                        {
                            plan.Error(table.Location(row.Key, column) + ": " + e.Message);
                        }
                    }
                    data.Add(item);
                }
                string json = JsonConvert.SerializeObject(data, Formatting.Indented);
                plan.Diff(original == null ? new Dictionary<string, string>() : new Dictionary<string, string> { [jsonPath] = original }, new Dictionary<string, string> { [jsonPath] = json });
                plan.Prepare(() =>
                {
                    table.AssertUnchanged();
                    if (originalHash != ExcelFiles.Hash(jsonPath))
                    {
                        throw new InvalidOperationException("JSON target changed after preview; preview again.");
                    }

                    ExcelFiles.WriteAtomic(jsonPath, temporary => File.WriteAllText(temporary, json, new UTF8Encoding(false)));
                });
            }
            catch (Exception e)
            {
                plan.Error(path + ": " + e.Message);
            }
            return plan;
        }

        // 将已识别的列写入暂存行，并收集单元格转换、非空未知列和缺少 ID 列的错误。
        private static void ImportRow(ExcelTable table, KeyValuePair<int, IDictionary<string, object>> row, object target, ExcelImportPlan plan)
        {
            var fields = ExcelValues.Fields(target.GetType()).ToDictionary(ExcelValues.ColumnName);
            foreach (var column in table.Columns)
            {
                if (column.Name == DataTypeColumn)
                {
                    continue;
                }

                if (!fields.TryGetValue(column.Name, out var field))
                {
                    if (!string.IsNullOrEmpty(ExcelTable.Text(table.Value(row.Value, column))))
                    {
                        plan.Error(table.Location(row.Key, column) + ": unknown or ignored field on " + target.GetType().FullName);
                    }

                    continue;
                }
                try
                {
                    ExcelValues.Import(target, field, table.Value(row.Value, column), column.Type);
                }
                catch (Exception e)
                {
                    plan.Error(table.Location(row.Key, column) + ": " + e.Message);
                }
            }
            var id = ExcelValues.Id(target.GetType());
            if (id != null && !table.Columns.Any(c => c.Name == ExcelValues.ColumnName(id)))
            {
                plan.Error(table.Source + ": missing ID column " + ExcelValues.ColumnName(id));
            }
        }

        // 以唯一行键记录具体类型及原字段状态，拒绝重复目标身份。
        private static Dictionary<string, string> Snapshots(IEnumerable<object> values, bool requireId = false)
        {
            var snapshot = new Dictionary<string, string>(StringComparer.Ordinal);
            int index = 0;
            foreach (var value in values)
            {
                string key = ExcelValues.Key(value, index++, requireId);
                if (snapshot.ContainsKey(key))
                {
                    throw new InvalidOperationException("Duplicate target ID: " + key);
                }

                snapshot.Add(key, value.GetType().FullName + ":" + ExcelValues.State(value));
            }
            return snapshot;
        }
        // 合并声明类型与所需具体类型的列定义，拒绝同名异型列后按协议导出各行。
        private static void Export(IReadOnlyList<object> values, Type baseType, string path, string sheetName, bool includeType)
        {
            if (values.Any(value => value == null))
            {
                throw new InvalidOperationException("Cannot export null rows.");
            }

            var columns = new List<ExcelColumn>();
            if (includeType)
            {
                columns.Add(new ExcelColumn { Name = DataTypeColumn, Type = ExcelFieldsType.String, Notes = "ScriptableObject full type name" });
            }

            foreach (var type in (includeType ? values.Select(v => v.GetType()).Concat(new[] { baseType }).Distinct() : new[] { baseType }))
            {
                foreach (var field in ExcelValues.Fields(type))
                {
                    var name = ExcelValues.ColumnName(field);
                    var existing = columns.FirstOrDefault(c => c.Name == name);
                    var fieldType = ExcelValues.FieldType(field.FieldType);
                    if (existing != null && existing.Type != fieldType)
                    {
                        throw new InvalidOperationException("Conflicting field types: " + name);
                    }

                    if (existing == null)
                    {
                        columns.Add(new ExcelColumn { Name = name, Type = fieldType, Notes = field.GetCustomAttribute<TooltipAttribute>()?.tooltip ?? string.Empty });
                    }
                }
            }

            if (columns.Count == 0)
            {
                throw new InvalidOperationException("No serializable fields on " + baseType.FullName);
            }

            var data = new List<IDictionary<string, object>>();
            foreach (var target in values)
            {
                var fields = ExcelValues.Fields(target.GetType()).ToDictionary(ExcelValues.ColumnName);
                var row = new Dictionary<string, object>();
                foreach (var column in columns)
                {
                    row.Add(column.Name, column.Name == DataTypeColumn ? target.GetType().FullName : fields.TryGetValue(column.Name, out var field) ? ExcelValues.Export(target, field) : null);
                }

                data.Add(row);
            }
            ExcelTable.Write(path, columns, data, sheetName);
        }
    }
}
