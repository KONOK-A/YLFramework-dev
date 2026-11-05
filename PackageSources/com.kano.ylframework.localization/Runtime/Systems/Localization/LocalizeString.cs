using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.UI;

namespace YLFramework.Localization
{
    // TMP可用Other的公开string属性绑定，不让主体程序集依赖可选TMP包。
    /// <summary>本地化字符串写入目标的组件适配方式。</summary>
    public enum ComponentType
    {
        /// <summary>写入同一物体上 UGUI Text 的文本，不替换字体资源。</summary>
        Text,
        /// <summary>写入同一物体上 TextMesh 的文本。</summary>
        TextMesh,
        /// <summary>按组件名反射写入公开、可写的字符串属性或字段。</summary>
        Other
    }
    /// <summary>将本地化文本、格式参数及追加片段写入同物体的文本组件，不负责字体加载或切换。</summary>
    /// <remarks>反射目标成员在 IL2CPP 下需由接入方保留，本组件不生成裁剪配置。</remarks>
    [AddComponentMenu("YLFramework/Localization/Localize String")]
    public class LocalizeString : LocalizeBase
    {
        [SerializeField] private ComponentType componentType;
        [SerializeField] private string componentFullName;
        [SerializeField] private string fieldName = "text";
        [SerializeField] private List<AppendData> args = new();
        [SerializeField] private List<AppendData> appends = new();
        /// <summary>可直接编辑的追加片段列表；直接修改列表后需主动刷新显示。</summary>
        public List<AppendData> Appends => appends;

        /// <summary>重置组件时依据同物体是否有 TextMesh 选择默认文本适配方式。</summary>
        private void Reset() => componentType = GetComponent<TextMesh>() ? ComponentType.TextMesh : ComponentType.Text;

        /// <summary>组合当前文本并写入目标；内置文本目标缺失时跳过，反射目标不合法时抛出异常。</summary>
        protected override void OnLanguageValueChange()
        {
            // 目标可能在组件启用之后才绑定；不缓存永久缺失的查询结果。
            var value = GetString();
            if (componentType == ComponentType.Text)
            {
                var target = GetComponent<Text>();
                if (target)
                {
                    target.text = value;
                }

                return;
            }
            if (componentType == ComponentType.TextMesh)
            {
                var target = GetComponent<TextMesh>();
                if (target)
                {
                    target.text = value;
                }

                return;
            }
            var other = string.IsNullOrEmpty(componentFullName) ? null : GetComponent(componentFullName);
            if (!other)
            {
                throw new InvalidOperationException($"Localization ID={Id}: component '{componentFullName}' is missing.");
            }

            var property = other.GetType().GetProperty(fieldName, BindingFlags.Public | BindingFlags.Instance);
            if (property != null && property.PropertyType == typeof(string) && property.CanWrite && property.GetIndexParameters().Length == 0)
            {
                property.SetValue(other, value);
                return;
            }
            var field = other.GetType().GetField(fieldName, BindingFlags.Public | BindingFlags.Instance);
            if (field != null && field.FieldType == typeof(string) && !field.IsInitOnly)
            {
                field.SetValue(other, value);
                return;
            }
            throw new InvalidOperationException($"Localization ID={Id}: '{componentFullName}.{fieldName}' is not a writable public string member. Preserve reflected members in IL2CPP.");
        }

        /// <summary>按当前请求语言组合主文本、参数及追加内容，不写回显示组件。</summary>
        /// <exception cref="FormatException">格式错误，异常补充当前语言、条目 ID 和配置上下文。</exception>
        public string GetString()
        {
            try
            {
                return LocalizationText.Compose(Id, id => LocalizationManager.GetString(id), args, appends);
            }
            catch (FormatException error)
            {
                throw new FormatException($"Localization language={LocalizationManager.RequestedLanguage}, ID={Id}, configs={LocalizationManager.ConfigNames()}: invalid argument/append format.", error);
            }
        }
        /// <summary>取得按当前语言解析并应用包装后的参数文本，不格式化主模板。</summary>
        public string[] GetArgs() => LocalizationText.GetArguments(id => LocalizationManager.GetString(id), args);

        /// <summary>复制替换参数列表并请求刷新；传入空引用时清空参数。</summary>
        /// <param name="data">新的参数片段，复制后与调用方列表独立。</param>
        public void SetArgs(IList<AppendData> data)
        {
            args = data == null ? new List<AppendData>() : new List<AppendData>(data);
            Refresh();
        }

        /// <summary>追加一个随语言查询的文本片段，并请求刷新显示。</summary>
        /// <param name="id">追加文本的本地化 ID。</param>
        /// <param name="richText">可选格式包装，以 {0} 接收片段文本。</param>
        public void Append(int id, string richText = null)
        {
            appends ??= new List<AppendData>();
            appends.Add(new AppendData { type = AppendType.Id, id = id, richText = richText });
            Refresh();
        }

        /// <summary>追加一个字面文本片段，并请求刷新显示。</summary>
        /// <param name="str">不经过本地化查询的文本。</param>
        /// <param name="richText">可选格式包装，以 {0} 接收片段文本。</param>
        public void Append(string str, string richText = null)
        {
            appends ??= new List<AppendData>();
            appends.Add(new AppendData { type = AppendType.String, str = str, richText = richText });
            Refresh();
        }

        /// <summary>清空追加片段并请求刷新，不清空主文本 ID 或格式参数。</summary>
        public void ClearAppend()
        {
            appends?.Clear();
            Refresh();
        }
    }
}
