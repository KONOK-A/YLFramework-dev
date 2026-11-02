using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using Newtonsoft.Json;
using YLFramework.Excel;

namespace YLFramework.Buff
{
    /// <summary>沿用效果ID数组的作者格式；仅Editor解析真实资产，不序列化Unity对象JSON。</summary>
    public sealed class BuffEffectExcel : CustomExcelFieldImportAndExport
    {
        /// <summary>将效果定义引用列表导出为保留原顺序及重复项的 ID JSON 数组。</summary>
        /// <param name="target">所属数据对象，此转换不读取它。</param>
        /// <param name="value">待导出的效果定义列表，不允许空引用项。</param>
        /// <param name="type">字段类型信息，此转换不按它分支。</param>
        /// <returns>效果 ID 数组的 JSON 文本。</returns>
        public override object Export(object target, object value, ExcelFieldsType type)
        {
            var ids = new List<int>();
            foreach (var effect in (List<EffectData>)value)
            {
                if (!effect)
                {
                    throw new InvalidOperationException("Buff效果引用为空，不能导出。");
                }

                ids.Add(effect.id);
            }
            return JsonConvert.SerializeObject(ids);
        }
        /// <summary>仅在 Editor 中把 ID JSON 数组解析为项目内的效果资产引用，缺失或歧义时抛错。</summary>
        /// <param name="target">所属数据对象，此转换不读取它。</param>
        /// <param name="value">包含效果 ID 数组的单元格值。</param>
        /// <param name="type">字段类型信息，此转换不按它分支。</param>
        /// <returns>按输入顺序排列的效果定义引用，不复制效果资产。</returns>
        public override object Import(object target, object value, ExcelFieldsType type)
        {
#if UNITY_EDITOR
            var ids = JsonConvert.DeserializeObject<List<int>>(Convert.ToString(value, CultureInfo.InvariantCulture) ?? "[]") ?? new List<int>();
            var assets = new Dictionary<int, EffectData>();
            var needed = new HashSet<int>(ids);
            foreach (var guid in UnityEditor.AssetDatabase.FindAssets("t:EffectData"))
            {
                var effect = UnityEditor.AssetDatabase.LoadAssetAtPath<EffectData>(UnityEditor.AssetDatabase.GUIDToAssetPath(guid));
                if (!effect || !needed.Contains(effect.id))
                {
                    continue;
                }

                if (assets.ContainsKey(effect.id))
                {
                    throw new InvalidOperationException($"效果ID歧义: {effect.id}");
                }

                assets.Add(effect.id, effect);
            }
            var result = new List<EffectData>();
            foreach (var id in ids)
            {
                if (!assets.TryGetValue(id, out var effect))
                {
                    throw new InvalidOperationException($"效果ID未找到: {id}");
                }

                result.Add(effect);
            }
            return result;
#else
            throw new InvalidOperationException("Buff效果Excel导入仅在Editor作者工具执行。");
#endif
        }
        /// <summary>把转换结果直接赋给目标字段，不额外创建资产或保存文件。</summary>
        /// <param name="target">被赋值的数据对象。</param>
        /// <param name="field">待写入的字段。</param>
        /// <param name="value">已解析的效果引用列表。</param>
        public override void SetValue(object target, FieldInfo field, object value) => field.SetValue(target, value);
    }
}
