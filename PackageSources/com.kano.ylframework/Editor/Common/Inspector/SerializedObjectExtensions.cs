using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;

namespace YLFramework.Editor.Common
{

    /// <summary>枚举 Inspector 可见的顶层序列化属性，供自定义 Inspector 按需筛选绘制。</summary>
    public static class SerializedObjectExtensions
    {

        /// <summary>
        /// 通过 Unity 属性迭代器收集可见的顶层属性，不递归展开子属性，也不使用字段反射。
        /// </summary>
        /// <param name="serializedObject">调用方维护有效期和更新/应用操作的序列化视图。</param>
        /// <returns>独立游标组成的列表，仍指向原 SerializedObject，并非值快照；由调用方管理使用与释放。</returns>

        public static List<SerializedProperty> GetAllSerializedProperty(this SerializedObject serializedObject)
        {
            var list = new List<SerializedProperty>();
            var iterator = serializedObject.GetIterator();

            if (iterator.NextVisible(true))
            {
                do
                {
                    // 迭代器会持续移动，保存 Copy 才能让列表中各项停留在各自的属性位置。
                    list.Add(iterator.Copy());
                }
                while (iterator.NextVisible(false));
            }

            return list;
        }

    }

}
