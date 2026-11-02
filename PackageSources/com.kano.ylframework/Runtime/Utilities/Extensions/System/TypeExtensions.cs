using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;


namespace YLFramework.Extensions
{
    /// <summary>
    /// 将类型与其泛型参数整理为委托风格的可读名称，不用于解析或还原类型。
    /// </summary>
    public static class TypeExtensions
    {
        /// <summary>
        /// 获取委托风格的类型显示名称，泛型参数以名称拼接；支持开放/部分构造泛型。
        /// FullName 不可用时回退到 Name；null 类型抛 ArgumentNullException。
        /// </summary>
        /// <param name="type">待显示的类型，不可为 null。</param>
        /// <param name="fullName">是否使用类型定义的 FullName；泛型参数始终只使用 Name。</param>
        /// <returns>去掉泛型元数后附加括号参数列表的名称；非泛型类型也带空括号。</returns>
        public static string GetDelegateDisplayName(this Type type, bool fullName = true)
        {

            if (type == null)
            {
                throw new ArgumentNullException(nameof(type));
            }

            Type definition = type.IsGenericType ? type.GetGenericTypeDefinition() : type;
            string name = fullName ? definition.FullName ?? definition.Name : definition.Name;
            // 只移除每一段的泛型元数，保留嵌套类型名称；构造类型不展示程序集限定参数文本。
            var display = new StringBuilder();
            for (int i = 0; i < name.Length; i++)
            {
                if (name[i] == '`')
                {
                    while (i + 1 < name.Length && char.IsDigit(name[i + 1]))
                    {
                        i++;
                    }
                }
                else
                {
                    display.Append(name[i]);
                }
            }
            name = display.ToString();
            Type[] args = type.IsGenericType ? type.GetGenericArguments() : Type.EmptyTypes;
            if (args.Length > 0)
            {
                StringBuilder builder = new StringBuilder();
                builder.Append(name);
                builder.Append("(");

                for (int i = 0; i < args.Length; i++)
                {
                    Type t = args[i];
                    builder.Append(t.Name);
                    if (i == args.Length - 1)
                    {
                        builder.Append(")");
                    }
                    else
                    {
                        builder.Append(",");
                    }
                }

                name = builder.ToString();
            }
            else
            {
                name = string.Format("{0}()", name);
            }

            return name;
        }



    }

}
