using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using MiniExcelLibs;
using MiniExcelLibs.OpenXml;
using YLFramework.Excel;

namespace YLFramework.Integrations.Excel.Editor
{
    /// <summary>保留工作表单元格列号、协议字段名、列型及描述的列定义。</summary>
    internal sealed class ExcelColumn
    {
        internal string Cell;
        internal string Name;
        internal ExcelFieldsType Type;
        internal string Notes;
    }

    /// <summary>读取三行表头的 .xlsx 快照，并保留原行号和源文件哈希。</summary>
    internal sealed class ExcelTable
    {
        internal readonly List<ExcelColumn> Columns = new List<ExcelColumn>();
        internal readonly List<KeyValuePair<int, IDictionary<string, object>>> Rows = new List<KeyValuePair<int, IDictionary<string, object>>>();
        internal string Source;
        internal string Hash;

        // 解析字段、类型和描述表头，拒绝重复字段与无效列型并跳过完全空白的数据行。
        internal static ExcelTable Read(string path, string sheetName, string excludePrefix = "")
        {
            if (!string.Equals(Path.GetExtension(path), ".xlsx", StringComparison.OrdinalIgnoreCase))
            {
                throw new FormatException("Only .xlsx workbooks are supported: " + path);
            }

            var table = new ExcelTable { Source = Path.GetFullPath(path), Hash = ExcelFiles.Hash(path) };
            var rows = MiniExcel.Query(path, false, sheetName, ExcelType.XLSX, configuration: new OpenXmlConfiguration { EnableSharedStringCache = false })
                .Cast<IDictionary<string, object>>().ToList();
            if (rows.Count < 3)
            {
                throw new FormatException(path + ": expected field/type/description headers in rows 1-3.");
            }

            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in rows[0])
            {
                string name = Text(item.Value);
                if (name.Length == 0 || (!string.IsNullOrEmpty(excludePrefix) && name.StartsWith(excludePrefix, StringComparison.Ordinal)))
                {
                    continue;
                }

                if (!names.Add(name))
                {
                    throw new FormatException(path + "!" + item.Key + "1: duplicate field " + name);
                }

                rows[1].TryGetValue(item.Key, out var rawType);
                string typeText = Text(rawType);
                if (typeText.Equals("boolean", StringComparison.OrdinalIgnoreCase))
                {
                    typeText = "Bool";
                }

                if (typeText.Equals("int64", StringComparison.OrdinalIgnoreCase))
                {
                    typeText = "Long";
                }

                if (!Enum.TryParse(typeText, true, out ExcelFieldsType type) || type == ExcelFieldsType.None || !Enum.IsDefined(typeof(ExcelFieldsType), type))
                {
                    throw new FormatException(path + "!" + item.Key + "2: invalid field type " + typeText);
                }

                rows[2].TryGetValue(item.Key, out var notes);
                table.Columns.Add(new ExcelColumn { Cell = item.Key, Name = name, Type = type, Notes = Text(notes) });
            }
            if (table.Columns.Count == 0)
            {
                throw new FormatException(path + ": no fields found.");
            }

            for (int i = 3; i < rows.Count; i++)
            {
                if (rows[i].Values.All(v => v == null || Text(v).Length == 0))
                {
                    continue;
                }

                table.Rows.Add(new KeyValuePair<int, IDictionary<string, object>>(i + 1, rows[i]));
            }
            table.AssertUnchanged();
            return table;
        }

        // 在校验时点比较文件哈希以拒绝过期预览，不锁定后续写入期间的源文件。
        internal void AssertUnchanged()
        {
            if (Hash != ExcelFiles.Hash(Source))
            {
                throw new InvalidOperationException("Workbook changed after preview; preview again: " + Source);
            }
        }
        // 统一使用不随区域变化的文本表示，将空值表示为空字符串。
        internal static string Text(object value) => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        // 按原单元格列号读取值，缺失单元格返回 null。
        internal object Value(IDictionary<string, object> row, ExcelColumn column) => row.TryGetValue(column.Cell, out var value) ? value : null;
        // 生成含源路径、单元格和字段名的诊断位置。
        internal string Location(int row, ExcelColumn column) => Source + "!" + column.Cell + row + " (" + column.Name + ")";

        // 将三行表头与数据写入一个新工作簿，不保留目标文件原有工作表和格式。
        internal static void Write(string path, IReadOnlyList<ExcelColumn> columns, IEnumerable<IDictionary<string, object>> data, string sheetName)
        {
            if (!string.Equals(Path.GetExtension(path), ".xlsx", StringComparison.OrdinalIgnoreCase))
            {
                throw new FormatException("Only .xlsx output is supported.");
            }

            var rows = new List<IDictionary<string, object>>();
            rows.Add(columns.ToDictionary(c => c.Name, c => (object)c.Name));
            rows.Add(columns.ToDictionary(c => c.Name, c => (object)c.Type.ToString().ToLowerInvariant()));
            rows.Add(columns.ToDictionary(c => c.Name, c => (object)(c.Notes ?? string.Empty)));
            rows.AddRange(data);
            ExcelFiles.WriteAtomic(path, temporary => MiniExcel.SaveAs(temporary, rows, false, sheetName, ExcelType.XLSX,
                new OpenXmlConfiguration { AutoFilter = false, TableStyles = TableStyles.None }));
        }
    }

    /// <summary>为 Excel 作者流程提供文件哈希与同目录临时文件替换，不协调多文件事务。</summary>
    internal static class ExcelFiles
    {
        // 计算存在文件的 SHA-256，文件不存在时返回 null。
        internal static string Hash(string path)
        {
            if (!File.Exists(path))
            {
                return null;
            }

            using (var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var sha = SHA256.Create())
            {
                return BitConverter.ToString(sha.ComputeHash(stream));
            }
        }

        // 先写同目录临时文件，再替换或移动到目标，并清理本次临时文件。
        internal static void WriteAtomic(string path, Action<string> writer)
        {
            path = Path.GetFullPath(path);
            string parent = Path.GetDirectoryName(path);
            if (!Directory.Exists(parent))
            {
                throw new DirectoryNotFoundException(parent);
            }

            string temporary = Path.Combine(parent, "." + Path.GetFileNameWithoutExtension(path) + "." + Guid.NewGuid().ToString("N") + ".tmp" + Path.GetExtension(path));
            try
            {
                writer(temporary);
                if (File.Exists(path))
                {
                    File.Replace(temporary, path, null);
                }
                else
                {
                    File.Move(temporary, path);
                }
            }
            finally
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
        }
    }
}
