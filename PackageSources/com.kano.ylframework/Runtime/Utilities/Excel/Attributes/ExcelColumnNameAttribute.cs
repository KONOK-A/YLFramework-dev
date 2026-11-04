using System;

namespace YLFramework.Excel
{
    /// <summary>Declares a stable workbook column independently of CLR names and Unity field aliases.</summary>
    [AttributeUsage(AttributeTargets.Field, AllowMultiple = false, Inherited = true)]
    public sealed class ExcelColumnNameAttribute : Attribute
    {
        /// <summary>The exact column name used by the Excel authoring backend.</summary>
        public string Name { get; }

        /// <summary>Creates a lightweight field contract without depending on an Excel parser.</summary>
        public ExcelColumnNameAttribute(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("An Excel column name must not be empty.", nameof(name));
            }
            Name = name;
        }
    }
}
