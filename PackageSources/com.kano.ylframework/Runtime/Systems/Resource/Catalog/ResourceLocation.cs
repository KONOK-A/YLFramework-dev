using System;
using UnityEngine;

namespace YLFramework.Resource
{
    /// <summary>来源身份，不使用注册顺序或字符串前缀猜测加载后端。</summary>
    public readonly struct ResourceSourceId : IEquatable<ResourceSourceId>
    {
        /// <summary>Unity Resources 共享借用来源。</summary>
        public static ResourceSourceId Resources { get; } = new ResourceSourceId("resources");
        /// <summary>由已验证发布快照与 Bundle 图提供的来源。</summary>
        public static ResourceSourceId AssetBundle { get; } = new ResourceSourceId("asset-bundle");
        /// <summary>从绝对本地文件路径创建纹理的来源。</summary>
        public static ResourceSourceId LocalTexture { get; } = new ResourceSourceId("local-texture");
        /// <summary>从显式 HTTP(S) URL 创建纹理的来源。</summary>
        public static ResourceSourceId UrlTexture { get; } = new ResourceSourceId("url-texture");
        /// <summary>由 Editor 模拟装配提供的来源；声明标识本身不会注册后端。</summary>
        public static ResourceSourceId Editor { get; } = new ResourceSourceId("editor");
        /// <summary>精确的来源标识文本；默认结构体为 null。</summary>
        public string Value
        {
            get;
        }
        /// <summary>是否已通过构造器建立标识，不表示对应后端已装配。</summary>
        public bool IsValid => Value != null;
        /// <summary>校验稳定标识的字符集并原样保存。</summary>
        public ResourceSourceId(string value)
        {
            ResourceAddress.ValidateId(value, nameof(value));
            Value = value;
        }
        /// <summary>按区分大小写的标识文本比较来源。</summary>
        public bool Equals(ResourceSourceId other) => Value == other.Value;
        /// <summary>仅与相同标识类型比较来源身份。</summary>
        public override bool Equals(object obj) => obj is ResourceSourceId other && Equals(other);
        /// <summary>生成集合查找用散列值，不作为持久化身份。</summary>
        public override int GetHashCode() => Value?.GetHashCode() ?? 0;
        /// <summary>返回来源文本，默认结构体使用无效标识提示。</summary>
        public override string ToString() => Value ?? "<invalid-source>";
    }

    /// <summary>最小内存加载描述；locator 原样保真，来源自行验证其语法。不定义持久化格式。</summary>
    public sealed class ResourceLocation : IEquatable<ResourceLocation>
    {
        /// <summary>明确选择的加载后端身份。</summary>
        public ResourceSourceId Source
        {
            get;
        }
        /// <summary>来源解释的精确定位文本；完整 URL 查询参数仍属于身份。</summary>
        public string Locator
        {
            get;
        }
        /// <summary>目录声明的已编译 Unity 对象类型，与来源和 Locator 共同参与身份比较。</summary>
        public Type AssetType
        {
            get;
        }

        /// <summary>仅用于诊断显示；URL query/fragment 不进入日志，原始 Locator 和 ordinal 身份不受影响。</summary>
        public string DisplayLocator
        {
            get
            {
                if (!Source.Equals(ResourceSourceId.UrlTexture))
                {
                    return Locator;
                }

                if (!Uri.TryCreate(Locator, UriKind.Absolute, out var uri))
                {
                    return "<invalid-url>";
                }

                var authority = uri.HostNameType == UriHostNameType.IPv6 ? "[" + uri.Host.Trim('[', ']') + "]" : uri.Host;
                return uri.Scheme + "://" + authority + (uri.IsDefaultPort ? "" : ":" + uri.Port) + uri.AbsolutePath +
                    (string.IsNullOrEmpty(uri.Query) ? "" : "?<redacted>");
            }
        }

        /// <summary>校验来源、非空定位文本和闭合 Unity 对象类型，不在此验证来源专用语法或加载资源。</summary>
        public ResourceLocation(ResourceSourceId source, string locator, Type assetType)
        {
            if (!source.IsValid)
            {
                throw new ArgumentException("A valid source identity is required.", nameof(source));
            }

            if (string.IsNullOrWhiteSpace(locator))
            {
                throw new ArgumentException("An exact source locator is required.", nameof(locator));
            }

            if (assetType == null || !typeof(UnityEngine.Object).IsAssignableFrom(assetType) || assetType.ContainsGenericParameters)
            {
                throw new ArgumentException("Asset types must be compiled UnityEngine.Object types.", nameof(assetType));
            }

            Source = source;
            Locator = locator;
            AssetType = assetType;
        }

        /// <summary>按来源、原始 Locator 与声明类型比较，不使用脱敏后的显示文本。</summary>
        public bool Equals(ResourceLocation other) => other != null && Source.Equals(other.Source) && Locator == other.Locator && AssetType == other.AssetType;
        /// <summary>仅与加载描述按相同的三部分身份比较。</summary>
        public override bool Equals(object obj) => obj is ResourceLocation other && Equals(other);
        /// <summary>为来源、定位文本和类型生成集合散列值。</summary>
        public override int GetHashCode()
        {
            unchecked
            {
                return (Source.GetHashCode() * 397 ^ Locator.GetHashCode()) * 397 ^ AssetType.GetHashCode();
            }
        }
        /// <summary>显示来源、诊断定位文本和声明类型；URL 使用 DisplayLocator 脱敏。</summary>
        public override string ToString() => $"{Source}:{DisplayLocator} ({AssetType.FullName})";
    }
}
