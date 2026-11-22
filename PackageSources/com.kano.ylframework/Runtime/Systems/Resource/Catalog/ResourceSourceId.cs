using System;

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

}
